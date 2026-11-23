# Apotheosis 项目开发规范与约束

## 1. 编码风格与注释规范
- **紧凑参数行**：C# 方法声明、构造函数与调用参数保持紧凑排版（参考单行宽度 **200 列**，尽量单行或紧凑双行），不要强行拆成每行一个参数。
- **注释职责分离**：
  - 对外 XML 注释（`/// <summary>`）保持简短精炼，只说明**这个方法/类型做什么**，避免堆砌实现细节与修饰词；
  - 具体实现原理、位布局、并发同步或边界原因统一用简短行注释（`//`）写在方法体内部。

## 2. 架构与协作边界
- **未下令不动手**：在讨论方案、回答疑问、提出修复建议或给出计划阶段，**严禁提前修改或创建业务代码文件**，必须等用户明确下令开工后再开始动手改代码。
- **禁止静默改架构**：修复 Bug 或处理代码审查问题时，不得擅自修改既定架构、句柄位宽布局或公开模块契约；凡涉及架构调整，必须先向用户说明并取得确认。
- **优先通用复用组件**：新增渲染/基础能力时优先设计通用可复用容器与无状态工具，避免为每个具体业务特效造大量定制子类。

## 3. 构建与环境约定
- **本地 .NET SDK 路径**：命令行执行 `dotnet` 前需将 `$env:USERPROFILE\.dotnet` 置于 `$env:PATH` 最前（系统默认 `PATH` 缺少 .NET 10 SDK）。
- **目标环境**：tModLoader `1.4.5-dev` / `.NET 10`；`Tools/Apotheosis.ShaderTools` 保持 `net10.0`。
- **双配置验证**：涉及核心代码或工具链改动后，需验证 `dotnet build Apotheosis.slnx -c Development -m` 与 `dotnet build Apotheosis.csproj -c Release` 均 0 警告 0 错误（确保 `Release` 零 `DebugTool` / `Hexa.NET.ImGui` 残留）。

## 4. tModLoader / FNA 渲染铁律
- **卸载与线程安全**：`ModSystem.Unload()` 在 tML 后台线程执行，而 `Main.DoDraw` / `OnPreDraw` 仍在主线程运行；所有主线程绘制钩子须与 `Unload()` 通过锁与卸载标记串行化，GPU 资源释放（`VertexBuffer` / `IndexBuffer` / `RenderTarget2D` / `Effect`）若处于非主线程必须通过 `Main.QueueMainThreadAction` 派发至主线程。
- **原生绘制挂钩时机**：直接调用 `GraphicsDevice.Draw*` 时必须挂在 `Main.spriteBatch` 已关闭的干净间隙（如 `DoDraw_UpdateCameraPosition`、`PostDrawTiles`、`DrawProjectiles` 前后、`DrawDust` 后、`DrawInterface` 后），避免与 `Deferred` 批次冲突。
- **Shader Model 3.0 约束**：`.fx` 面向 D3D9 `fx_2_0`（`vs_3_0` / `ps_3_0`），矩阵 uniform 统一使用 `float4x4`（不使用 `float3x3`），骨骼矩阵数组单批建议不超过 55 根骨骼。
- **原版贴图细节**：`TextureAssets.MagicPixel.Value` 实际尺寸为 `1 × 1000`（非 `1 × 1`），当作单像素白色面片绘制时务必传入 `sourceRect: (0, 0, 1, 1)`。
