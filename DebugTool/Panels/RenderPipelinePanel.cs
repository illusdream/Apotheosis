#if DEBUG
#nullable enable

using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Apotheosis.DebugTool.Core;
using Apotheosis.DebugTool.Theme;
using Apotheosis.Runtime.Foundation.Transform;
using Apotheosis.Runtime.Rendering;
using Apotheosis.Runtime.Rendering.Commands;
using Apotheosis.Runtime.Rendering.Geometry;
using Apotheosis.Runtime.Rendering.Materials;
using Apotheosis.Runtime.Rendering.Targets;
using Hexa.NET.ImGui;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRect = Microsoft.Xna.Framework.Rectangle;
using XnaVec2 = Microsoft.Xna.Framework.Vector2;
using XnaVec3 = Microsoft.Xna.Framework.Vector3;
using XnaVec4 = Microsoft.Xna.Framework.Vector4;

namespace Apotheosis.DebugTool.Panels
{
    /// <summary>渲染管线与 RT 池诊断验证面板（仅 Development 模式自动注册，内置 7 项默认关闭的实测开关）。</summary>
    [NoJIT]
    internal sealed class RenderPipelinePanel : IDebugPanel
    {
        private const int DemoTrailCount = 6;
        private const string OffscreenDemoChannelName = "Debug.OffscreenDemo";

        private bool _demoTrails;
        private bool _demoMagicCircle;
        private bool _demoStaticTransform;
        private bool _demoSprites;
        private bool _demoInstanced;
        private bool _demoWorkerThread;
        private bool _demoOffscreenChannel;

        private int _instancedCount = 2000;
        private float _elapsed;
        private ulong _lastSubmittedRenderFrame = ulong.MaxValue;

        // Demo 资源句柄（懒初始化，卸载时统一释放）
        private MeshHandle[]? _trailHandles;
        private TrailMesh[]? _trailMeshes;

        private MeshHandle _magicCircleHandle;
        private MeshHandle _staticBakedStarHandle;
        private StaticMesh? _staticBakedStar;
        private StaticMeshInstance? _staticStarInstance;
        private MeshHandle _staticStarInstanceHandle;

        private MeshHandle _instancedDiamondBase;
        private MultiGroupInstancedMesh? _instancedSwarm;
        private MeshHandle _instancedSwarmHandle;

        // Demo 7：离屏命名帧通道验证资源
        private RenderTargetHandle _offscreenChannelHandle;
        private bool _offscreenChannelRegistered;
        private MeshHandle _offscreenRingHandle;
        private bool _offscreenCompositeHooked;
        private int _lastManualEvictCount;

        // 双缓冲工作线程网格（Ping-Pong 避免与主线程 Flush 并发读写同一 DynamicMesh）
        private readonly MeshHandle[] _workerMeshHandles = new MeshHandle[2];
        private readonly DynamicMesh?[] _workerMeshes = new DynamicMesh?[2];
        private int _workerBufferIndex;
        private Task? _workerTask;
        private int _workerSubmittedFrames;
        private volatile bool _unloaded;

        public string Id => "render_pipeline";
        public string DisplayName => "渲染管线";
        public int Order => 10;

        public void OnUpdate(float deltaSeconds)
        {
            if (_unloaded || Main.dedServ || Main.gameMenu || Main.LocalPlayer is not { active: true } player)
                return;

            // 追帧或窗口最小化期间若 DoDraw 未推进，避免在同一渲染帧重复堆叠提交 Demo 命令
            ulong currentRenderFrame = RenderManager.FrameId;
            if (currentRenderFrame == _lastSubmittedRenderFrame)
                return;
            _lastSubmittedRenderFrame = currentRenderFrame;

            if (!Main.gamePaused)
                _elapsed += deltaSeconds;

            XnaVec2 center = player.Center;

            // 1. 多拖尾自动合批验证（6 条独立 TrailMesh 共用 DefaultAdditive2D → 合并为 1 DrawCall）
            if (_demoTrails)
            {
                EnsureTrailsInitialized();
                MaterialHandle mat = RenderManager.Materials.DefaultAdditive2D;
                RenderTargetHandle target = RenderManager.Targets.AfterProjectiles;

                for (int i = 0; i < DemoTrailCount; i++)
                {
                    if (!Main.gamePaused)
                    {
                        float phase = _elapsed * 3.2f + i * (MathF.Tau / DemoTrailCount);
                        float radius = 72f + 22f * MathF.Sin(_elapsed * 2.1f + i);
                        XnaVec2 pos = center + new XnaVec2(MathF.Cos(phase) * radius, MathF.Sin(phase) * radius);
                        _trailMeshes![i].PushPoint(pos);
                    }

                    RenderManager.Commands.Submit(target, mat, _trailHandles![i], subLayer: 2, sortOrder: (short)i);
                }
            }

            // 2. 动态魔法阵点线面构建测试（恒定像素线宽圆环 + 弧线 + 双旋转六芒星）
            if (_demoMagicCircle)
            {
                if (!_magicCircleHandle.IsValid)
                    _magicCircleHandle = RenderManager.Meshes.AcquireDynamic(512, 1536);

                DynamicMesh mesh = RenderManager.Meshes.GetDynamic(_magicCircleHandle);
                mesh.Clear();

                float pulse = 1f + 0.06f * MathF.Sin(_elapsed * 2.5f);
                float rOuter = 135f * pulse;
                float rMid = 104f * pulse;
                float rInner = 64f * pulse;

                var cyan = new XnaColor(70, 235, 255, 220);
                var magenta = new XnaColor(255, 90, 220, 200);
                var gold = new XnaColor(255, 225, 110, 230);

                mesh.AddRing(center, rOuter, thickness: 2.5f, segments: 64, cyan);
                mesh.AddRing(center, rMid, thickness: 2.0f, segments: 48, magenta);
                mesh.AddPolygon(center, rMid, sides: 3, thickness: 2.2f, rotation: _elapsed * 0.9f, gold);
                mesh.AddPolygon(center, rMid, sides: 3, thickness: 2.2f, rotation: _elapsed * 0.9f + MathF.PI, gold);

                for (int a = 0; a < 4; a++)
                {
                    float startAng = -_elapsed * 1.4f + a * (MathF.PI * 0.5f);
                    mesh.AddArc(center, rInner, thickness: 3.0f, segments: 14, startAng, MathF.PI * 0.34f, cyan, magenta);
                }

                RenderManager.Commands.Submit(
                    RenderManager.Targets.AfterTiles,
                    RenderManager.Materials.DefaultAdditive2D,
                    _magicCircleHandle,
                    subLayer: 1,
                    sortOrder: 0);
            }

            // 3. StaticMeshInstance + Transform3D 联动验证（零每帧顶点拷贝，直接传世界矩阵）
            if (_demoStaticTransform)
            {
                EnsureStaticStarInitialized();
                _staticStarInstance!.Transform = Transform3D.FromEuler(
                    position: new XnaVec3(center.X, center.Y - 96f, 0f),
                    eulerRadians: new XnaVec3(_elapsed * 1.1f, _elapsed * 1.7f, _elapsed * 0.8f),
                    scale: new XnaVec3(1f + 0.15f * MathF.Sin(_elapsed * 3f)));

                RenderManager.Commands.Submit(
                    RenderManager.Targets.AfterProjectiles,
                    RenderManager.Materials.DefaultAdditive2D,
                    _staticStarInstanceHandle,
                    subLayer: 3,
                    sortOrder: 0);
            }

            // 4. 散装 2D DrawSprite 内部槽位池合批验证（MagicPixel 实际尺寸 1x1000，显式裁剪 1x1 像素）
            if (_demoSprites)
            {
                var tex = TextureAssets.MagicPixel?.Value;
                if (tex is not null && !tex.IsDisposed)
                {
                    RenderTargetHandle target = RenderManager.Targets.AfterDust;
                    var pixelRect = new XnaRect(0, 0, 1, 1);
                    for (int i = 0; i < 120; i++)
                    {
                        float angle = _elapsed * 0.8f + i * (MathF.Tau / 120f);
                        float dist = 160f + 24f * MathF.Sin(_elapsed * 3f + i * 0.35f);
                        XnaVec2 pos = center + new XnaVec2(MathF.Cos(angle) * dist, MathF.Sin(angle) * dist);
                        var col = XnaColor.Lerp(XnaColor.Cyan, XnaColor.HotPink, (i % 20) / 19f) * 0.85f;

                        RenderManager.Commands.DrawSprite(
                            target,
                            tex,
                            pos,
                            col,
                            sourceRect: pixelRect,
                            rotation: angle,
                            origin: new XnaVec2(0.5f, 0.5f),
                            scale: new XnaVec2(6f, 6f),
                            blendState: Microsoft.Xna.Framework.Graphics.BlendState.Additive,
                            subLayer: 4,
                            sortOrder: 0);
                    }
                }
            }

            // 5. 多分组硬件实例化压测（2 组不同 BaseMesh：UnitQuad2D + 菱形静态几何，1 次 SetData + 2 次 DrawInstancedPrimitives）
            if (_demoInstanced)
            {
                EnsureInstancedSwarmInitialized();
                _instancedSwarm!.ClearAll();

                int count = Math.Clamp(_instancedCount, 100, 10000);
                for (int i = 0; i < count; i++)
                {
                    float t = i / (float)count;
                    float angle = _elapsed * (0.35f + (i % 7) * 0.08f) + i * 2.3999632f; // 黄金角分布
                    float dist = 48f + MathF.Sqrt(t) * 320f;
                    XnaVec2 pos = center + new XnaVec2(MathF.Cos(angle) * dist, MathF.Sin(angle) * dist);
                    float sz = 4f + (i % 5) * 1.4f;
                    int group = i & 1;

                    XnaColor col = group == 0
                        ? new XnaColor(90, 230, 255, 190)
                        : new XnaColor(255, 180, 90, 210);

                    _instancedSwarm.Add(group, InstanceData.From2D(pos, new XnaVec2(sz, sz), angle, col));
                }

                RenderManager.Commands.Submit(
                    RenderManager.Targets.AfterDust,
                    RenderManager.Materials.DefaultInstancedAdditive,
                    _instancedSwarmHandle,
                    subLayer: 5,
                    sortOrder: 0);
            }

            // 6. 后台工作线程双缓冲并发写 DynamicMesh + 提交 8B RenderCommand 验证
            if (_demoWorkerThread)
            {
                EnsureWorkerMeshesInitialized();

                if (_workerTask is null || _workerTask.IsCompleted)
                {
                    int bufIdx = _workerBufferIndex;
                    _workerBufferIndex = 1 - _workerBufferIndex;

                    XnaVec2 capturedCenter = center;
                    float capturedTime = _elapsed;
                    MeshHandle handle = _workerMeshHandles[bufIdx];
                    DynamicMesh mesh = _workerMeshes[bufIdx]!;
                    MaterialHandle mat = RenderManager.Materials.DefaultAdditive2D;
                    RenderTargetHandle target = RenderManager.Targets.AfterDust;

                    _workerTask = Task.Run(() =>
                    {
                        if (_unloaded)
                            return;

                        mesh.Clear();
                        XnaVec2 pos = capturedCenter + new XnaVec2(MathF.Cos(capturedTime * 4f) * 90f, -120f);
                        mesh.AddPolygon(pos, radius: 18f, sides: 5, thickness: 2.5f, rotation: capturedTime * 3f, XnaColor.LimeGreen);

                        if (!_unloaded)
                        {
                            RenderManager.Commands.Submit(target, mat, handle, subLayer: 6, sortOrder: 0);
                            Interlocked.Increment(ref _workerSubmittedFrames);
                        }
                    });
                }
            }

            // 7. 离屏命名帧通道绘制（PreDrawOffscreen 写入离屏 RT，同帧 DrawDust 阶段读取 CurrentTexture 合成回主屏）
            if (_demoOffscreenChannel)
            {
                EnsureOffscreenDemoInitialized();

                DynamicMesh mesh = RenderManager.Meshes.GetDynamic(_offscreenRingHandle);
                mesh.Clear();

                float pulse = 1f + 0.08f * MathF.Sin(_elapsed * 3.1f);
                var aqua = new XnaColor(60, 245, 220, 235);
                var gold = new XnaColor(255, 215, 95, 225);
                var violet = new XnaColor(185, 110, 255, 210);

                mesh.AddRing(center, 152f * pulse, thickness: 3.5f, segments: 64, aqua);
                mesh.AddRing(center, 118f * pulse, thickness: 2.2f, segments: 48, violet);
                mesh.AddPolygon(center, 118f * pulse, sides: 4, thickness: 2.8f, rotation: _elapsed * 1.1f, gold);
                mesh.AddPolygon(center, 118f * pulse, sides: 4, thickness: 2.8f, rotation: _elapsed * 1.1f + MathF.PI * 0.25f, aqua);

                RenderManager.Commands.Submit(_offscreenChannelHandle, RenderManager.Materials.DefaultAdditive2D, _offscreenRingHandle, subLayer: 0, sortOrder: 0);
            }
        }

        public void OnDraw()
        {
            RenderTimingStats total = RenderManager.Commands.GetLastFrameTotalStats();

            CyberpunkWidgets.SectionHeader("PIPELINE_TELEMETRY", "8B_COMMAND");

            float availW = ImGui.GetContentRegionAvail().X;
            float cardW = Math.Max(105f, (availW - 8f) * 0.5f);

            CyberpunkWidgets.MetricCard("COMMANDS", $"{total.CommandsSubmitted}", CyberpunkTheme.NeonCyan, cardW);
            ImGui.SameLine(0f, 8f);
            CyberpunkWidgets.MetricCard("DRAW_CALLS", $"{total.DrawCallsIssued}", CyberpunkTheme.NeonGreen, cardW);

            CyberpunkWidgets.MetricCard("MERGED_BATCHES", $"{total.BatchesMerged}", CyberpunkTheme.AcidYellow, cardW);
            ImGui.SameLine(0f, 8f);
            CyberpunkWidgets.MetricCard("INSTANCES", $"{total.InstancesDrawn}", CyberpunkTheme.NeonMagenta, cardW);

            ImGui.TextUnformatted(
                $"Shaders: {RenderManager.Shaders.RegisteredCount}  |  " +
                $"Targets: {RenderManager.Targets.RegisteredCount}  |  " +
                $"Materials: {RenderManager.Materials.ActiveCount}  |  " +
                $"Meshes: {RenderManager.Meshes.ActiveCount}");

            CyberpunkWidgets.SectionHeader("STAGE_BREAKDOWN", "LAST_FRAME");

            if (ImGui.BeginTable("##StageStats", 6, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Timing", ImGuiTableColumnFlags.WidthFixed, 125f);
                ImGui.TableSetupColumn("Cmd", ImGuiTableColumnFlags.WidthFixed, 45f);
                ImGui.TableSetupColumn("DC", ImGuiTableColumnFlags.WidthFixed, 40f);
                ImGui.TableSetupColumn("Merge", ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupColumn("Prims", ImGuiTableColumnFlags.WidthFixed, 65f);
                ImGui.TableSetupColumn("Inst", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();

                for (int i = 0; i < RenderTimingConstants.Count; i++)
                {
                    var timing = (RenderTiming)i;
                    RenderTimingStats s = RenderManager.Commands.GetLastFrameStats(timing);

                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(timing.ToString());
                    ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(s.CommandsSubmitted.ToString());
                    ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(s.DrawCallsIssued.ToString());
                    ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(s.BatchesMerged.ToString());
                    ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(s.PrimitivesDrawn.ToString());
                    ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(s.InstancesDrawn.ToString());
                }

                ImGui.EndTable();
            }

            CyberpunkWidgets.SectionHeader("RT_POOL_TELEMETRY", "POOL_AND_VRAM");

            RenderTargetStats rtStats = RenderManager.Targets.GetStats();
            double totalVramMb = rtStats.TotalEstimatedVramBytes / (1024.0 * 1024.0);
            long totalLeases = rtStats.TotalCreated + rtStats.TotalReused;
            double hitRatePct = totalLeases > 0 ? (rtStats.TotalReused * 100.0 / totalLeases) : 0.0;

            CyberpunkWidgets.MetricCard("POOL_ACTIVE", $"{rtStats.ActiveTransientCount}T / {rtStats.ActiveChannelLeaseCount}C", CyberpunkTheme.NeonCyan, cardW);
            ImGui.SameLine(0f, 8f);
            CyberpunkWidgets.MetricCard("POOL_IDLE", $"{rtStats.IdlePooledCount}", CyberpunkTheme.NeonGreen, cardW);

            CyberpunkWidgets.MetricCard("EST_VRAM", $"{totalVramMb:F2} MB", CyberpunkTheme.AcidYellow, cardW);
            ImGui.SameLine(0f, 8f);
            CyberpunkWidgets.MetricCard("REUSE_HIT", $"{hitRatePct:F1}%", CyberpunkTheme.NeonMagenta, cardW);

            ImGui.TextUnformatted(
                $"Ref: {rtStats.ReferenceWidth}x{rtStats.ReferenceHeight}  |  " +
                $"Channels: {rtStats.RegisteredChannelCount}  |  " +
                $"Persistent: {rtStats.PersistentCount}  |  " +
                $"Rebuilds: {rtStats.ResizeRebuilds}");
            ImGui.TextUnformatted(
                $"Created: {rtStats.TotalCreated}  |  " +
                $"Reused: {rtStats.TotalReused}  |  " +
                $"Evicted: {rtStats.TotalEvicted}");

            if (rtStats.SuspectedLeakCount > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.35f, 0.35f, 1f));
                ImGui.TextUnformatted($"[WARNING] Suspected Transient RT Leaks: {rtStats.SuspectedLeakCount}");
                ImGui.PopStyleColor();
            }

            if (ImGui.Button("立即回收全部空闲 RT"))
                _lastManualEvictCount = RenderManager.Targets.EvictIdleTargets();
            if (_lastManualEvictCount > 0)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted($"(上次释放 {_lastManualEvictCount} 张)");
            }

            CyberpunkWidgets.SectionHeader("VERIFICATION_DEMOS", Main.gameMenu ? "ENTER_WORLD" : "READY");

            if (Main.gameMenu)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.TextMuted);
                ImGui.TextUnformatted("进入世界后可勾选下方开关实时验证各项合批与实例化能力。");
                ImGui.PopStyleColor();
                return;
            }

            ImGui.Checkbox("1. 多拖尾自动合批 (6x TrailMesh -> 1 DrawCall)", ref _demoTrails);
            ImGui.Checkbox("2. 动态魔法阵点线面构建 (DynamicMesh + GeometryBuilder)", ref _demoMagicCircle);
            ImGui.Checkbox("3. StaticMeshInstance + Transform3D 旋转联动", ref _demoStaticTransform);
            ImGui.Checkbox("4. 散装 2D DrawSprite 槽位池合批 (120 Quads -> 1 DrawCall)", ref _demoSprites);
            ImGui.Checkbox("5. 多分组硬件实例化压测 (2 BaseMeshes, 1 SetData)", ref _demoInstanced);
            if (_demoInstanced)
            {
                ImGui.Indent(16f);
                ImGui.SliderInt("实例数量", ref _instancedCount, 100, 10000);
                ImGui.Unindent(16f);
            }

            ImGui.Checkbox($"6. 后台工作线程并发录制验证 (已提交 {Volatile.Read(ref _workerSubmittedFrames)} 次)", ref _demoWorkerThread);
            ImGui.Checkbox("7. 离屏命名帧通道绘制 + 主屏合成 (PooledFrameRenderTarget)", ref _demoOffscreenChannel);
        }

        public void OnUnload()
        {
            _unloaded = true;
            _demoTrails = false;
            _demoMagicCircle = false;
            _demoStaticTransform = false;
            _demoSprites = false;
            _demoInstanced = false;
            _demoWorkerThread = false;
            _demoOffscreenChannel = false;

            if (_offscreenCompositeHooked)
            {
                On_Main.DrawDust -= Hook_DrawDustCompositeOffscreenDemo;
                _offscreenCompositeHooked = false;
            }
            _offscreenChannelRegistered = false;
            _offscreenChannelHandle = default;

            if (_workerTask is not null)
            {
                try { _workerTask.Wait(500); }
                catch { /* 卸载阶段忽略 */ }
                _workerTask = null;
            }

            if (_trailHandles is not null)
            {
                for (int i = 0; i < _trailHandles.Length; i++)
                    RenderManager.Meshes.Unregister(_trailHandles[i]);
                _trailHandles = null;
                _trailMeshes = null;
            }

            if (_magicCircleHandle.IsValid)
            {
                RenderManager.Meshes.ReleaseDynamic(_magicCircleHandle);
                _magicCircleHandle = default;
            }

            if (_offscreenRingHandle.IsValid)
            {
                RenderManager.Meshes.ReleaseDynamic(_offscreenRingHandle);
                _offscreenRingHandle = default;
            }

            if (_staticStarInstanceHandle.IsValid)
            {
                RenderManager.Meshes.Unregister(_staticStarInstanceHandle);
                _staticStarInstanceHandle = default;
                _staticStarInstance = null;
            }

            if (_staticBakedStarHandle.IsValid)
            {
                RenderManager.Meshes.Unregister(_staticBakedStarHandle);
                _staticBakedStarHandle = default;
                _staticBakedStar = null;
            }

            if (_instancedSwarmHandle.IsValid)
            {
                RenderManager.Meshes.Unregister(_instancedSwarmHandle);
                _instancedSwarmHandle = default;
                _instancedSwarm = null;
            }

            if (_instancedDiamondBase.IsValid)
            {
                RenderManager.Meshes.Unregister(_instancedDiamondBase);
                _instancedDiamondBase = default;
            }

            for (int i = 0; i < _workerMeshHandles.Length; i++)
            {
                if (_workerMeshHandles[i].IsValid)
                {
                    RenderManager.Meshes.Unregister(_workerMeshHandles[i]);
                    _workerMeshHandles[i] = default;
                }
                _workerMeshes[i] = null;
            }
        }

        private void EnsureWorkerMeshesInitialized()
        {
            for (int i = 0; i < _workerMeshHandles.Length; i++)
            {
                if (!_workerMeshHandles[i].IsValid)
                {
                    _workerMeshes[i] = new DynamicMesh(32, 96);
                    _workerMeshHandles[i] = RenderManager.Meshes.Register(_workerMeshes[i]!);
                }
            }
        }

        private void EnsureTrailsInitialized()
        {
            if (_trailHandles is not null)
                return;

            _trailHandles = new MeshHandle[DemoTrailCount];
            _trailMeshes = new TrailMesh[DemoTrailCount];

            XnaColor[] palette =
            {
                XnaColor.Cyan,
                XnaColor.DeepSkyBlue,
                XnaColor.HotPink,
                XnaColor.Gold,
                XnaColor.MediumSpringGreen,
                XnaColor.Violet,
            };

            for (int i = 0; i < DemoTrailCount; i++)
            {
                var trail = new TrailMesh(maxPoints: 24)
                {
                    StartWidth = 14f,
                    EndWidth = 1f,
                    StartColor = palette[i % palette.Length],
                    EndColor = XnaColor.Transparent,
                };
                _trailMeshes[i] = trail;
                _trailHandles[i] = RenderManager.Meshes.Register(trail);
            }
        }

        private void EnsureStaticStarInitialized()
        {
            if (_staticStarInstanceHandle.IsValid)
                return;

            var builder = new DynamicMesh(64, 192);
            builder.AddPolygon(XnaVec2.Zero, radius: 34f, sides: 3, thickness: 4f, rotation: 0f, XnaColor.Gold);
            builder.AddPolygon(XnaVec2.Zero, radius: 34f, sides: 3, thickness: 4f, rotation: MathF.PI, XnaColor.Cyan);
            builder.AddRing(XnaVec2.Zero, radius: 42f, thickness: 2.5f, segments: 24, XnaColor.White);

            _staticBakedStar = builder.BakeToStatic();
            _staticBakedStarHandle = RenderManager.Meshes.Register(_staticBakedStar);
            _staticStarInstance = new StaticMeshInstance(_staticBakedStar);
            _staticStarInstanceHandle = RenderManager.Meshes.Register(_staticStarInstance);
        }

        private void EnsureInstancedSwarmInitialized()
        {
            if (_instancedSwarmHandle.IsValid)
                return;

            // 构建单位菱形作为第 2 个静态 BaseMesh，与 UnitQuad2D 共同验证多分组硬件实例化
            UnifiedVertex[] diamondVerts =
            {
                new(new XnaVec3(0f, -0.7f, 0f), new XnaVec3(0f, 0f, -1f), XnaColor.White, new XnaVec2(0.5f, 0f), XnaVec4.Zero),
                new(new XnaVec3(0.55f, 0f, 0f), new XnaVec3(0f, 0f, -1f), XnaColor.White, new XnaVec2(1f, 0.5f), XnaVec4.Zero),
                new(new XnaVec3(0f, 0.7f, 0f), new XnaVec3(0f, 0f, -1f), XnaColor.White, new XnaVec2(0.5f, 1f), XnaVec4.Zero),
                new(new XnaVec3(-0.55f, 0f, 0f), new XnaVec3(0f, 0f, -1f), XnaColor.White, new XnaVec2(0f, 0.5f), XnaVec4.Zero),
            };
            int[] diamondIndices = { 0, 1, 2, 0, 2, 3 };
            _instancedDiamondBase = RenderManager.Meshes.RegisterStatic(diamondVerts, diamondIndices);

            _instancedSwarm = new MultiGroupInstancedMesh(
                initialCapacityPerGroup: 1024,
                RenderManager.Meshes.UnitQuad2D,
                _instancedDiamondBase);
            _instancedSwarmHandle = RenderManager.Meshes.Register(_instancedSwarm);
        }

        private void EnsureOffscreenDemoInitialized()
        {
            if (!_offscreenChannelRegistered)
            {
                RenderTargetDescriptor desc = RenderTargetDescriptor.ScreenRelative(scale: 1f);
                _offscreenChannelHandle = RenderManager.Targets.RegisterPooledChannel(OffscreenDemoChannelName, RenderTiming.PreDrawOffscreen, in desc);
                _offscreenChannelRegistered = true;
            }

            if (!_offscreenRingHandle.IsValid)
                _offscreenRingHandle = RenderManager.Meshes.AcquireDynamic(512, 1536);

            if (!_offscreenCompositeHooked)
            {
                On_Main.DrawDust += Hook_DrawDustCompositeOffscreenDemo;
                _offscreenCompositeHooked = true;
            }
        }

        private void Hook_DrawDustCompositeOffscreenDemo(On_Main.orig_DrawDust orig, Main self)
        {
            orig(self);

            if (_unloaded || RenderManager.IsUnloading || !_demoOffscreenChannel || Main.dedServ || Main.gameMenu || !_offscreenChannelRegistered)
                return;

            GraphicsDevice? gd = Main.graphics?.GraphicsDevice;
            if (gd is null || gd.IsDisposed)
                return;

            if (!RenderManager.Targets.TryGetChannel(_offscreenChannelHandle, out PooledFrameRenderTarget? channel))
                return;

            RenderTarget2D? offscreenTex = channel.CurrentTexture;
            if (offscreenTex is null || offscreenTex.IsDisposed)
                return;

            Viewport vp = gd.Viewport;
            int destW = vp.Width > 0 ? vp.Width : Main.screenWidth;
            int destH = vp.Height > 0 ? vp.Height : Main.screenHeight;

            // 在 SpriteBatch 已关闭的间隙将离屏通道按当前活动视口 1:1 叠加合成
            Main.spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.Additive, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            try
            {
                Main.spriteBatch.Draw(offscreenTex, new XnaRect(0, 0, destW, destH), XnaColor.White);
            }
            finally
            {
                Main.spriteBatch.End();
            }
        }
    }
}
#endif
