# Apotheosis ECS 粒子系统架构设计与开发计划

> 本文档为基于 `Friflo.Engine.ECS` 的多线程 ECS 粒子系统总体设计蓝图与**小步快跑**实施计划。  
> 严格遵循“按指令分步执行、每步可独立编译验证、不静默改动既有渲染契约”的开发原则。

---

## 1. 设计目标与核心技术选型

| 维度 | 选型与约束 |
| :--- | :--- |
| **ECS 框架** | `Friflo.Engine.ECS` **`3.6.0`**（稳定版、纯托管零 `unsafe`、原生支持 `.NET 10`） |
| **遍历方式** | 热点系统统一采用 `ArchetypeQuery.Chunks` + `Span<T>`（与 Query Source Generator 同级极限性能，更灵活适配多 Store、外层分支提升与未来 SIMD；首期不引入 `Friflo.Engine.ECS.Generators`，后续按需可选追加） |
| **执行线程** | **独立粒子工作线程**（独占全部 `EntityStore` 读写，三缓冲 `Write / Ready / Reading` 向主线程交付渲染数据） |
| **渲染提交** | 复用既有 `RenderManager.Commands`（8B `RenderCommand` + `InstancedMesh` + `UnitQuad2D` + `DefaultBatch.fx / InstancedTech`），普通面片走硬件实例化 |
| **坐标系** | **2D / 3D 混合模式**（遵循 `Transform3D` 右手系：`+X` 右、`+Y` 下、`+Z` 屏内），**统一 `Vector3` 位置/速度 + 2D/3D 分离姿态组件** |
| **多系统支持** | 常驻 `GlobalWorldStore` + 对象池化独立小型 `EntityStore`（支持 Boss/弹幕跟随、局部空间、跨 Store 脱钩迁移，并预留 UI 窗口粒子扩展口） |
| **组合复用** | 引入 **`Fragment`** 预置积木与 **`ParticleTemplate`** 烘焙机制，零 GC 批量 Archetype 分配 |

---

## 2. 总体架构与线程数据流

```text
主线程 (Game / Draw)                                    粒子专用工作线程 (ParticleWorker)
────────────────────────────────────────                ──────────────────────────────────────────────
[业务层 API]                                            [PendingCommandQueue]
  ParticleTemplate (由多个 Fragment 烘焙)                    │ 消费发射 / 启停 / 迁移 / 力场更新指令
  ParticleSystemHandle (绑定 Transform / 坐标)               ▼
        │ 写入轻量值类型指令 (零阻塞)                    [ParticleRuntime (顶层全局宿主)]
        ▼                                                    ├─ GlobalWorldFields (全局世界力场表)
[ModSystem.PostUpdateEverything (60Hz)]                      ├─ GlobalWorldStore  (常驻全局世界 Store)
  1. 采集只读帧快照: dt, 暂停态, 风速,                        ├─ ActiveInstances[] (池化小型独立 Store)
     各活跃 Instance 绑定 TransformHandle 世界矩阵            │    └─ ParticleInstance (小系统宿主)
  2. 唤醒工作线程 (AutoResetEvent, 零空转) ────通知────▶      │         ├─ EntityStore (对象池借出/归还)
                                                             │         ├─ OriginBinding (World/Local 矩阵)
[RenderManager 渲染前挂钩 (主线程)]                           │         └─ LocalFields (专属局部力场表)
  原子接管 Ready -> Reading 槽只读提交  ◀══ 三缓冲交换 ═══    ├─ Simulation Systems (全局唯一 1 套)
  └─ RenderManager.Commands.Submit(...)                      │    └─ Execute(slot, in ParticleSimContext)
       ├─ AfterTiles / BeforeProjectiles /                   ├─ CommandBuffer.Playback (集中回收死亡粒子)
       │  AfterProjectiles / AfterDust                       └─ Render Systems (按 Shader 粒度切分)
       ├─ PostProcess.BloomMask / DistortionMask                  ├─ BasicQuadRenderSystem (图集面片, 2D+3D)
       └─ (未来预留) UI 窗口 RT / AfterInterface                  └─ 特种 Shader RenderSystem (按需扩展)
```

### 线程安全与生命周期铁律
1. **单写者隔离**：所有 `EntityStore` 仅由粒子工作线程读写，杜绝并发结构修改竞态。
2. **主线程句柄解析**：粒子工作线程**严禁**创建或懒加载 `Texture2D` / `Effect` / `MaterialHandle` / `MeshHandle`；所有资源句柄必须在主线程解析好，仅传递纯值类型句柄。
3. **三缓冲无踩踏交付（`Write` / `Ready` / `Reading`）**：每个渲染合批桶维护 3 份槽位；工作线程始终独占写入 `Write`，完成后原子与 `Ready` 互换（永不触碰主线程正在绘制的 `Reading`）；主线程在 `OnPreDraw` 原子将 `Ready` 换入 `Reading`，整帧 `DoDraw`（覆盖 `AfterTiles` 至 `AfterInterface` 全部 `Flush` 时机）独占读取，实现零等待、零拷贝、零踩踏；平时工作线程 < 1ms 完成即天然 0 帧延迟，极端爆发帧优雅降级为慢 1 帧而不卡主线程。
4. **安全停机栅栏**：`OnWorldUnload` 与 `ModSystem.Unload()` 先发送停顿/停止信号并同步等待工作线程安全退出当前步进，再清空 `EntityStore` 并按主线程规则注销释放 `InstancedMesh` 句柄。

---

## 3. 多 `EntityStore` 宿主模型与跨 Store 迁移

### 3.1 两层宿主设计
裸 `EntityStore` 仅负责存实体与组件，系统级上下文交由两层宿主承载：
- **顶层全局宿主 `ParticleRuntime`**：持有 `GlobalWorldFields`、常驻 `GlobalWorldStore`、小型 `EntityStore` 对象池（`BoundedObjectPool<ParticleStoreSlot>`）、活跃实例表、以及全局唯一的模拟/渲染 System 列表。
- **小型子系统宿主 `ParticleInstance`**（对外暴露代际安全的 `ParticleSystemHandle`）：
  - 包裹 1 个从池中借出的 `ParticleStoreSlot`（内含 `EntityStore` 与按 System ID 缓存的 `ArchetypeQuery[]`，稳态 0 GC）；
  - 持有 `OriginBinding`（静态 `Transform3D` / 绑定项目既有 `TransformHandle` / 自定义锚点）；
  - 持有模拟空间模式（`WorldSpace` / `LocalSpace`）、时间缩放、`AffectByGlobalFields` 开关、专属 `LocalFields` 列表、以及目标 `RenderTargetHandle` 覆盖项（为未来 UI 窗口/离屏 RT 预留）。

### 3.2 跨 `EntityStore` 脱钩迁移（Detach & Transfer）
- **典型场景**：跟随弹幕或 Boss 运动的 `LocalSpace` 独立子系统，在弹幕销毁/Boss 切阶段时，希望残留粒子继续在世界中自然飘散淡出，同时立刻归还该 `EntityStore` 给对象池。
- **低代价实现**：
  1. 按源 `EntityStore` 的每个 `Archetype`，在 `GlobalWorldStore` 用 `targetArchetype.CreateEntities(count)` 一次性批量划出连续槽位（无逐个 `AddComponent` 迁移开销）；
  2. 按组件列 `Span` 整块拷贝，若源为 `LocalSpace`，同步将 `Position` / `Velocity` / `Pose` 乘以最后一帧 `OriginWorldMatrix` 烘焙为世界空间；
  3. 清空源 `EntityStore` 并立即归还对象池复用（单次迁移通常仅几十微秒且在工作线程执行）。

### 3.3 UI 层粒子预留边界（后续专题细化）
- UI 粒子本质差异在于 **目标 RT（窗口离屏 RT 或 `BuiltInRenderTarget.AfterInterface`）+ 坐标系矩阵（`Main.UIScaleMatrix * ProjectionMatrix`）+ 独立暂停策略**。
- 架构上预留 `ParticleUpdateDomain`（`World` / `UI`）与 `ParticleInstance.TargetOverride`；待后续讨论 UI 阶段时，再按需在 `ManagedMaterial` 扩展可选的 `RenderCoordinateSpace`（默认 `World`，向后兼容）。

---

## 4. 组件（Component）与碎片（Fragment）体系

### 4.1 模拟类组件（Simulation Components，纯 `struct : IComponent`）
所有模拟组件完全不依赖贴图/材质/GPU 对象：

- **核心必备**：
  - `ParticleLife`：`float Age`, `float MaxAge`, `float NormalizedAge`（`0..1`）
  - `ParticleSpatial`：`Vector3 Position`, `Vector3 Velocity`（2D 时 `Position.Z` 兼作景深 `DepthZ`，`Velocity.Z = 0`）
  - **二选一姿态组件（Archetype 物理分流，热循环零分支）**：
    - `ParticlePose2D`：`float Rotation`, `float AngularVelocity`, `Vector2 Scale`（纯 `SinCos` 快路径）
    - `ParticlePose3D`：`Quaternion Rotation`, `Vector3 AngularVelocity`, `Vector3 Scale`, `ParticleBillboardMode Billboard`（全 3D 翻滚 / 朝向屏幕 / 可选伪透视）
- **按需组合的功能组件（用什么挂什么）**：
  - `ParticleGravity`：`Vector3 Acceleration`
  - `ParticleDrag`：`float LinearDrag`, `float AngularDrag`
  - `ParticleColorOverLife`：紧凑颜色插值端点/曲线参数 + 缓动枚举（纯值类型，0 托管分配）
  - `ParticleScaleOverLife`：紧凑缩放插值曲线参数 + 缓动枚举
  - `ParticleVelocityAlign`：速度方向自动对齐 + 速度拉伸系数（适合火花/剑气）
  - `ParticleFieldReceiver`：`uint FieldMask`, `float ForceScale`（按位掩码接收全局/局部力场）
  - `ParticleTileCollision`：反弹系数、摩擦力、碰撞行为（反弹 / 销毁 / 黏附）
  - `ParticleSheetAnim`：序列帧行列数、帧率/随寿命同步参数

### 4.2 渲染数据类组件（Render Data Components，两层切割）
1. **基础渲染组件（所有可渲染粒子共有）**：
   - `ParticleRenderBase`：`RenderTargetHandle Target`, `byte SubLayer`, `short SortOrder`, `MaterialHandle Material`, `Vector4 Color`（HDR RGBA，支持 `> 1.0` 直通 Bloom）, `Vector2 BaseSize`, `Vector2 Pivot`
2. **Shader 专属渲染组件（按 Shader 切分，独立扩展）**：
   - `QuadAtlasRenderData`：基础图集面片 Shader（`DefaultBatch.fx / InstancedTech`），仅含 `Vector4 UvRect`
   - 后续按需扩展示例：`DissolveRenderData`、`DistortionRenderData`（对接 `DistortionMask`）、`Mesh3DParticleRenderData`（自定义 `MeshHandle BaseMesh`）

### 4.3 `Fragment` 与 `ParticleTemplate`（高层配方与零 GC 烘焙）
- **`IParticleFragment`**：将常用组件及其初始化逻辑（含 Min~Max 随机区间、默认路由、缓动配置）封装成独立积木，如：
  - 运动/行为类：`LifetimeFragment`、`VelocityConeFragment`、`GravityFragment`、`DragFragment`、`Spin2DFragment`、`Tumble3DFragment`、`ColorOverLifeFragment`、`ScaleOverLifeFragment`、`VelocityStretchFragment`、`FieldReceiverFragment`、`TileCollisionFragment`
  - 渲染类：`QuadAtlasRenderFragment`、`SheetAnimFragment` 及各特种 Shader Fragment
- **`ParticleTemplate`**：
  - 通过链式 `.Add(fragment)` 拼装粒子模板；
  - **Bake（烘焙）**：收集全部 `Fragment` 声明的 `ComponentTypes` 与 `Tags`，固化成标准组件签名；
  - **批量生成**：工作线程一次性在目标 `Archetype` 创建 $N$ 个连续粒子，再交由各 `Fragment` 按组件列 `Span` 批量填充初始值（杜绝逐个 `AddComponent` 迁移，稳态 0 GC）。

---

## 5. System 调度、力场与 Shader 粒度渲染

### 5.1 自定义 `Execute` 调度（替代原生 `SystemRoot`）
- 不实例化 Friflo 原生 `SystemRoot`，彻底避免 `UpdateTick` 干扰与字典反查；
- 定义统一接口 `IParticleSimSystem.Execute(ParticleStoreSlot slot, in ParticleSimContext ctx)`；
- `ParticleSimContext` 为栈上 `readonly ref struct`，直接携带 `DeltaTime`、`GlobalFields`、`LocalFields`、`IsLocalSpace`、`OriginWorldMatrix`、`InverseOriginMatrix`；
- 每个 `ParticleStoreSlot` 内部用数组按 System ID 缓存 `ArchetypeQuery`，`O(1)` 下标直取且随 Store 回池复用（0 GC）。

### 5.2 力场与碰撞执行策略
- **全局唯一 1 套 `ForceFieldSystem` / `TileCollisionSystem`**，依次扫过 `GlobalWorldStore` 与所有活跃世界 `ParticleInstance`：
  - 若当前 Store 无生效力场，第一行 `O(1)` 短路跳过；
  - 若当前 Store 为 `LocalSpace` 且开启 `AffectByGlobalFields`，进入 Chunk 前仅将生效全局力场乘 `InverseOriginMatrix` 转入局部空间（复杂度 $O(\text{力场数})$）；
  - 碰撞系统遵循世界卸载停顿栅栏保护，仅对挂有 `ParticleTileCollision` 的世界域粒子生效。

### 5.3 以 Shader 为粒度的渲染系统（单 System 统一处理 2D + 3D Pose）
- 抽象通用基类 `InstancedShaderRenderSystemBase<TRenderData>`：
  - 封装 **2D Pose Query + 3D Pose Query 双路径遍历**（各自走零分支紧循环，2D 走纯 `SinCos`，3D 走四元数/Billboard/可选伪透视）；
  - 封装外层 `IsLocalSpace` 原点矩阵叠加；
  - 封装跨 Store 共享的三缓冲 `InstancedMesh` 合批桶（按 `Target + SubLayer + SortOrder + Material + BaseMesh` 分组），**同材质粒子跨 2D/3D、跨多 Store 自动合批为单 DrawCall**。
- 首期内置 **`BasicQuadRenderSystem`**（绑定 `QuadAtlasRenderData` + `UnitQuad2D`），后续新增任意特种粒子 Shader 仅需“1 个 `.fx` + 1 个 `TRenderData` struct + 继承基类实现 `PackCustom`”。

---

## 6. 规划目录结构

```text
Runtime/Particles/
├── Core/
│   ├── ParticleSystemHandle.cs          # 代际安全的小型子系统句柄
│   ├── ParticleStoreSlot.cs             # EntityStore + Query 缓存槽（对象池化）
│   ├── ParticleInstance.cs              # 小型独立粒子系统宿主（原点/空间/局部力场）
│   ├── ParticleOriginBinding.cs         # TransformHandle / 静态坐标原点绑定
│   ├── ParticleRuntime.cs               # 工作线程主引擎（命令队列/三缓冲/调度/迁移）
│   └── ParticleWorldSystem.cs           # ModSystem 生命周期入口与主线程帧同步
├── Components/
│   ├── SimulationComponents.cs          # 生命周期、空间、2D/3D 姿态、力场、碰撞、曲线等
│   └── RenderComponents.cs              # ParticleRenderBase + QuadAtlasRenderData 等
├── Fragments/
│   ├── IParticleFragment.cs             # Fragment 接口与批填充上下文
│   ├── ParticleTemplate.cs              # 模板构建与 Archetype 烘焙
│   ├── MotionFragments.cs               # 寿命/初速度/重力/阻尼/旋转/曲线/拉伸等常用 Fragment
│   └── RenderFragments.cs               # 图集面片/序列帧等渲染 Fragment
├── Systems/
│   ├── IParticleSimSystem.cs            # 模拟系统接口与 ParticleSimContext
│   ├── SimulationSystems.cs             # 寿命回收/力场/运动积分/曲线/序列帧/碰撞系统
│   ├── InstancedShaderRenderSystemBase.cs # Shader 粒度实例化渲染系统基类（双 Pose + 三缓冲）
│   └── BasicQuadRenderSystem.cs         # 默认图集面片硬件实例化渲染系统
├── Fields/
│   └── ForceFieldData.cs                # 力场数据结构（风/引力/排斥/涡流/掩码）与句柄管理
└── ParticleManager.cs                   # 对外静态门面（全局发射/创建独立系统/力场注册）

DebugTool/Panels/
└── ParticlePanel.cs                     # (仅 Development) 粒子统计监控与多场景压测面板
```

*(配套小幅非破坏性扩展：`MaterialModule.GetOrCreateInstancedMaterial(...)`；`RenderManager` 暴露渲染前粒子提交钩子点。)*

---

## 7. 小步快跑分阶段实施路线

每一步均为独立、可控的最小增量；**仅在收到明确指令后执行对应步骤**，每步完成后执行 `Development` + `Release` 双配置编译验证（0 警告 0 错误）：

| 阶段 | 名称 | 核心内容 | 验收标准 |
| :--- | :--- | :--- | :--- |
| **Step 1** | **依赖接入与环境验证** | 引入 `Friflo.Engine.ECS 3.6.0`，配置 `lib/`、`Apotheosis.csproj` 与 `build.txt` `dllReferences` | `Development` / `Release` 均 0 警告 0 错误，确认 `.tmod` 打包依赖完整 |
| **Step 2** | **渲染层极小前置补齐** | 在 `MaterialModule` 补充 `GetOrCreateInstancedMaterial`；在 `RenderManager` 暴露渲染前粒子提交钩子 | 不改动既有渲染契约与位宽布局，现有 Demo 全正常 |
| **Step 3** | **多线程运行时与多 Store 骨架** | 实现 `ParticleStoreSlot`（池化 + Query 缓存）、`ParticleInstance`、`ParticleOriginBinding`、`ParticleRuntime`（工作线程 + 指令队列 + 停机栅栏）与 `ParticleWorldSystem` | 空载零 CPU 空转、进出世界/重载 Mod 零死锁零泄漏 |
| **Step 4** | **基础组件、Fragment 与模板烘焙** | 实现核心模拟组件（寿命/空间/2D&3D 姿态/重力/阻尼/颜色与缩放曲线）+ 基础渲染组件（`ParticleRenderBase` + `QuadAtlasRenderData`）+ `IParticleFragment` & `ParticleTemplate` 烘焙批创建 | 支持通过 Fragment 组合模板向 `GlobalWorldStore` 与独立 `ParticleInstance` 零迁移批量生成粒子 |
| **Step 5** | **核心模拟与实例化渲染闭环** | 实现核心模拟系统（寿命回收/重力/阻尼/积分/曲线）+ `InstancedShaderRenderSystemBase` + `BasicQuadRenderSystem`（2D/3D 双 Pose + 三缓冲提交至 `RenderCommand`） | 游戏内成功渲染 2D/3D 混合图集粒子，同材质跨 Store 合并为 1 DrawCall，支持 `BloomMask` |
| **Step 6** | **力场、碰撞与跨 Store 脱钩迁移** | 实现全局/局部双层 `ForceFieldData` 与 `ForceFieldSystem`、`TileCollisionSystem`、`VelocityAlign` / `SheetAnim`，以及 `DetachAndTransferToGlobal` 批量迁移 | 局部小 Store 受全局/局部力场按掩码正确驱动；宿主销毁时残留粒子平滑脱钩淡出 |
| **Step 7** | **DebugTool 监控与实测面板** | 在 `DebugTool/Panels/` 新增 `ParticlePanel`（活跃粒子数/Store 数/批次数/工作线程耗时监控 + 万级 2D 火花、3D 翻滚、Transform 跟随、脱钩迁移、Bloom 压测开关） | `Development` 实测满帧稳定、`Release` 零残留 |
| **Step 8** | ***(后续按需)*** **特种 Shader 与 UI 窗口粒子** | 按需扩展溶解/扭曲等独立 Shader RenderSystem；专题设计并接入 UI 坐标空间与窗口 RT 粒子 | 按具体业务需求逐步追加 |
