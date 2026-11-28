#if DEBUG
#nullable enable

using System;
using System.Numerics;
using Apotheosis.DebugTool.Core;
using Apotheosis.DebugTool.Theme;
using Apotheosis.Runtime.Foundation.Transform;
using Apotheosis.Runtime.Particles;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Core;
using Apotheosis.Runtime.Particles.Emitters;
using Apotheosis.Runtime.Particles.Emitters.Modules;
using Apotheosis.Runtime.Particles.Fragments;
using Apotheosis.Runtime.Rendering;
using Apotheosis.Runtime.Rendering.Commands;
using Apotheosis.Runtime.Rendering.Materials;
using Apotheosis.Runtime.Rendering.Targets;
using Apotheosis.Runtime.Utils;
using Hexa.NET.ImGui;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaQuat = Microsoft.Xna.Framework.Quaternion;
using XnaVec2 = Microsoft.Xna.Framework.Vector2;
using XnaVec3 = Microsoft.Xna.Framework.Vector3;
using XnaVec4 = Microsoft.Xna.Framework.Vector4;

namespace Apotheosis.DebugTool.Panels
{
    /// <summary>ECS 多线程粒子系统诊断遥测与可视化测试面板（仅 Development 模式自动发现注册）。</summary>
    [NoJIT]
    internal sealed class ParticlePanel : IDebugPanel
    {
        // 底层手写 Span 直写逃生舱模板（由 ParticleTemplateAnalyzer 编译期校验）
        private static readonly ParticleTemplate CustomSpiralTemplate = ParticleTemplate
            .Create("Debug.CustomSpiral")
            .Add(BaseParticleFragments.Basic2DQuad)
            .Add(BaseParticleFragments.OverLifeCurves)
            .With<ParticleDrag>()
            .Bake();

        // ==================== 生命周期同步锁与程序化测试资源 ====================

        private readonly object _lifecycleLock = new();
        private Texture2D? _softGlowTex;
        private Texture2D? _petalTex;
        private Texture2D? _sheetAtlasTex;
        private MaterialHandle _softGlowAdditiveMat;
        private MaterialHandle _petalAdditiveMat;
        private MaterialHandle _sheetAdditiveMat;
        private int _cachedMaterialEpoch = -1;

        private ParticleEmitter? _sparkBurstEmitter;
        private ParticleEmitter? _stress10kEmitter;
        private ParticleEmitter? _flash1FrameEmitter;
        private ParticleEmitter? _pulseWaveEmitter;
        private ParticleEmitter? _fountainPrimaryEmitter;
        private ParticleEmitter? _fountainAccentEmitter;

        // ==================== UI 状态与参数 ====================

        private bool _spawnAtMouse;
        private int _burstCount = 400;
        private int _sheetModeIndex; // 0=固定帧率循环, 1=随寿命播完, 2=静态锁定随机帧

        private bool _demoFountain;
        private float _fountainRate = 3000f;
        private bool _fountainAccentEnabled = true;
        private float _fountainTimeScale = 1f;
        private ParticleSystemHandle _fountainHandle;
        private ParticleEmitterHandle _fountainPrimaryHandle;
        private ParticleEmitterHandle _fountainAccentHandle;
        private float _appliedFountainRate = -1f;
        private bool _appliedFountainAccentEnabled = true;
        private float _appliedFountainTimeScale = 1f;

        private bool _demo3DPetals;
        private bool _demo3DBillboard;
        private float _demo3DPerspective = 0.0025f;
        private ParticleSystemHandle _petal3DHandle;
        private bool _applied3DBillboard;
        private float _applied3DPerspective = -1f;

        private bool _demoTransformFollower;
        private bool _followerLocalSpace = true;
        private int _followerLostPolicyIndex; // 0=StopAndFade, 1=ClearImmediate
        private TransformNode? _followerNode;
        private ParticleSystemHandle _followerHandle;
        private bool _appliedFollowerLocalSpace = true;
        private int _appliedFollowerLostPolicyIndex = -1;

        private bool _demoBloomParticles;
        private float _bloomHdrIntensity = 2.6f;
        private ParticleSystemHandle _bloomMainHandle;
        private ParticleSystemHandle _bloomMaskHandle;
        private float _appliedBloomHdr = -1f;

        private float _elapsed;
        private double _avgStepMs;
        private double _peakStepMs;
        private volatile bool _unloaded;

        public string Id => "particles";
        public string DisplayName => "粒子系统";
        public int Order => 12;

        public void OnUpdate(float deltaSeconds)
        {
            if (_unloaded || Main.dedServ)
                return;

            lock (_lifecycleLock)
            {
                if (_unloaded)
                    return;

                // 返回主菜单时自动清理所有测试子系统句柄与临时 TransformNode
                if (Main.gameMenu || Main.LocalPlayer is not { active: true } player)
                {
                    CleanupWorldDemoHandlesLocked(destroyInstances: false);
                    return;
                }

                if (!Main.gamePaused)
                    _elapsed += deltaSeconds;

                ParticleRuntimeStats stats = ParticleManager.GetStats();
                _avgStepMs = _avgStepMs <= 0.0 ? stats.LastStepMs : _avgStepMs * 0.92 + stats.LastStepMs * 0.08;
                if (stats.LastStepMs > _peakStepMs)
                    _peakStepMs = stats.LastStepMs;

                EnsureAssetsReadyLocked();

                // 1. 持续 2D 高压喷泉（单实例挂载 2 个独立发射器，验证 ParticleEmitterHandle 精准调速/启停/爆发）
                UpdateFountainDemo();

                // 2. 3D 翻滚花瓣 / Billboard 晶尘子系统
                Update3DPetalDemo();

                // 3. TransformHandle 轨道节点跟随子系统（验证 LocalSpace / WorldSpace 与宿主失活策略）
                UpdateTransformFollowerDemo(player.Center);

                // 4. 选择性 Bloom HDR 粒子子系统（同一不可变蓝图无锁复用至主屏 + BloomMask）
                UpdateBloomDemo();
            }
        }

        public void OnDraw()
        {
            if (_unloaded || Main.dedServ)
                return;

            lock (_lifecycleLock)
            {
                if (_unloaded)
                    return;

                DrawPanelContentLocked();
            }
        }

        private void DrawPanelContentLocked()
        {
            ParticleRuntimeStats stats = ParticleManager.GetStats();
            RenderTimingStats renderStats = RenderManager.Commands.GetLastFrameTotalStats();

            // ==================== 1. 遥测卡片 ====================
            CyberpunkWidgets.SectionHeader("PARTICLE_TELEMETRY", ParticleManager.IsAvailable ? "WORKER_ONLINE" : "OFFLINE");

            float availW = ImGui.GetContentRegionAvail().X;
            float cardW = Math.Max(105f, (availW - 8f) * 0.5f);

            CyberpunkWidgets.MetricCard("TOTAL_PARTICLES", $"{stats.TotalParticles:N0}", CyberpunkTheme.NeonCyan, cardW);
            ImGui.SameLine(0f, 8f);
            Vector4 stepColor = stats.LastStepMs < 2.0 ? CyberpunkTheme.NeonGreen : (stats.LastStepMs < 6.0 ? CyberpunkTheme.AcidYellow : CyberpunkTheme.NeonMagenta);
            CyberpunkWidgets.MetricCard("WORKER_STEP_MS", $"{stats.LastStepMs:F3} ms", stepColor, cardW);

            CyberpunkWidgets.MetricCard("GLOBAL / INST", $"{stats.GlobalParticles:N0} / {stats.InstanceParticles:N0}", CyberpunkTheme.AcidYellow, cardW);
            ImGui.SameLine(0f, 8f);
            CyberpunkWidgets.MetricCard("ACTIVE / POOL", $"{stats.ActiveInstances} / {stats.PooledStoreSlots}", CyberpunkTheme.NeonMagenta, cardW);

            ImGui.TextUnformatted($"Avg: {_avgStepMs:F3} ms  |  Peak: {_peakStepMs:F3} ms  |  Steps: {stats.WorkerSteps}");
            ImGui.TextUnformatted($"Render Frame: DC={renderStats.DrawCallsIssued}  |  Instances={renderStats.InstancesDrawn:N0}");
            if (ImGui.SmallButton("重置峰值计时"))
            {
                _avgStepMs = 0.0;
                _peakStepMs = 0.0;
            }

            // ==================== 2. 通用发射原点与全局操作 ====================
            CyberpunkWidgets.SectionHeader("SPAWN_CONTROLS", Main.gameMenu ? "ENTER_WORLD" : "READY");
            if (Main.gameMenu)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.TextMuted);
                ImGui.TextUnformatted("进入世界后可触发下方各项单次爆发与持续发射测试案例。");
                ImGui.PopStyleColor();
                return;
            }

            ImGui.Checkbox("以鼠标世界坐标作为发射原点（取消则跟随玩家中心）", ref _spawnAtMouse);

            if (ImGui.Button("停止全部持续测试"))
                StopAllContinuousDemos(ParticleHostLostPolicy.StopAndFade);
            ImGui.SameLine();
            if (ImGui.Button("立即清空全部测试实例"))
                StopAllContinuousDemos(ParticleHostLostPolicy.ClearImmediate);

            // ==================== 3. 单次爆发测试案例 ====================
            CyberpunkWidgets.SectionHeader("ONE_SHOT_BURSTS", "EMIT_GLOBAL");

            ImGui.SliderInt("爆发粒子数", ref _burstCount, 50, 5000);

            if (ImGui.Button("1. 2D 速度拉伸火花爆发"))
                Trigger2DSparkBurst(_burstCount);
            ImGui.SameLine();
            if (ImGui.Button("2. 10,000 粒子瞬间压测"))
                Trigger10kStressBurst();

            if (ImGui.Button("3. 单帧闪光测试 (1/60s)"))
                TriggerOneFrameFlashBurst();
            ImGui.SameLine();
            if (ImGui.Button("4. 自定义 Span 螺旋爆发"))
                TriggerCustomSpiralBurst();

            ImGui.Spacing();
            string[] sheetModes = { "固定帧率循环 (8 FPS)", "随寿命播完 4 帧 (Rate=0)", "静态锁定随机帧 (Rate=-1)" };
            ImGui.SetNextItemWidth(220f);
            ImGui.Combo("序列帧模式", ref _sheetModeIndex, sheetModes, sheetModes.Length);
            ImGui.SameLine();
            if (ImGui.Button("5. 触发序列帧爆发"))
                TriggerSheetAnimBurst();

            if (ImGui.Button("6. PlayInstance 定时 3 连冲击波 (AutoStop 回收验证)"))
                TriggerAutoStopPulseInstance();

            // ==================== 4. 持续运行与多 Store 测试案例 ====================
            CyberpunkWidgets.SectionHeader("CONTINUOUS_DEMOS", "MULTI_STORE");

            if (ImGui.Checkbox("Demo A: 持续 2D 双发射器喷泉 (ParticleEmitterHandle 实时调速)", ref _demoFountain) && !_demoFountain)
                CleanupFountainDemo(ParticleHostLostPolicy.StopAndFade);
            if (_demoFountain)
            {
                ImGui.Indent(16f);
                ImGui.SliderFloat("主喷泉发射速率 (/秒，实时无断流)", ref _fountainRate, 0f, 15000f, "%.0f");
                ImGui.Checkbox("副发射器：金色星环火花（独立启停）", ref _fountainAccentEnabled);
                ImGui.SameLine();
                if (ImGui.SmallButton("副发射器追加爆发 +80") && _fountainAccentHandle.IsValid)
                    ParticleManager.TriggerEmitterBurst(_fountainAccentHandle, 80);
                ImGui.SliderFloat("整套子系统时间倍率 (TimeScale)", ref _fountainTimeScale, 0f, 2.5f, "%.2f");
                ImGui.Unindent(16f);
            }

            if (ImGui.Checkbox("Demo B: 3D 翻滚花瓣 / 轨道晶尘 (ParticlePose3D + 伪透视)", ref _demo3DPetals) && !_demo3DPetals)
                DestroyHandle(ref _petal3DHandle, ParticleHostLostPolicy.StopAndFade);
            if (_demo3DPetals)
            {
                ImGui.Indent(16f);
                ImGui.Checkbox("CameraFacing Billboard 模式（取消为全 3D 四元数翻滚）", ref _demo3DBillboard);
                ImGui.SliderFloat("伪透视强度 (PerspectiveFactor)", ref _demo3DPerspective, 0f, 0.006f, "%.4f");
                ImGui.Unindent(16f);
            }

            if (ImGui.Checkbox("Demo C: TransformHandle 轨道节点跟随子系统", ref _demoTransformFollower) && !_demoTransformFollower)
                CleanupFollowerDemo(ParticleHostLostPolicy.StopAndFade);
            if (_demoTransformFollower)
            {
                ImGui.Indent(16f);
                ImGui.Checkbox("LocalSpace 局部空间（取消为 WorldSpace 彗星尾迹）", ref _followerLocalSpace);
                string[] lostPolicies = { "StopAndFade (自然淡出)", "ClearImmediate (立即清空)" };
                ImGui.SetNextItemWidth(220f);
                ImGui.Combo("宿主失活策略", ref _followerLostPolicyIndex, lostPolicies, lostPolicies.Length);

                bool nodeAlive = _followerNode is { IsDisposed: false };
                bool instAlive = ParticleManager.IsInstanceAlive(_followerHandle);
                ImGui.TextUnformatted($"TransformNode: {(nodeAlive ? "ALIVE" : "DEAD")}  |  ParticleInstance: {(instAlive ? "ALIVE" : "RECYCLED")}");

                if (ImGui.SmallButton("模拟销毁 TransformNode") && _followerNode is { IsDisposed: false })
                    _followerNode.Cleanup(TransformCleanupReason.Manual);
                ImGui.SameLine();
                if (ImGui.SmallButton("重建轨道节点与子系统"))
                {
                    CleanupFollowerDemo(ParticleHostLostPolicy.ClearImmediate);
                    SpawnFollowerDemo();
                }
                ImGui.Unindent(16f);
            }

            if (ImGui.Checkbox("Demo D: 选择性 Bloom HDR 霓虹粒子 (BloomMask + AfterDust)", ref _demoBloomParticles) && !_demoBloomParticles)
            {
                DestroyHandle(ref _bloomMainHandle, ParticleHostLostPolicy.StopAndFade);
                DestroyHandle(ref _bloomMaskHandle, ParticleHostLostPolicy.StopAndFade);
            }
            if (_demoBloomParticles)
            {
                ImGui.Indent(16f);
                ImGui.SliderFloat("HDR 辉光亮度倍率", ref _bloomHdrIntensity, 1.0f, 5.0f, "%.2f");
                if (!RenderManager.PostProcess.Bloom.Enabled)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.AcidYellow);
                    ImGui.TextUnformatted("当前后处理 Bloom 已关闭，点击下方可一键启用观察辉光：");
                    ImGui.PopStyleColor();
                    if (ImGui.SmallButton("启用 Selective Bloom"))
                        RenderManager.PostProcess.Bloom.Enabled = true;
                }
                ImGui.Unindent(16f);
            }
        }

        public void OnUnload()
        {
            Texture2D? glow;
            Texture2D? petal;
            Texture2D? sheet;
            TransformNode? followerNode;

            lock (_lifecycleLock)
            {
                _unloaded = true;
                _demoFountain = false;
                _demo3DPetals = false;
                _demoTransformFollower = false;
                _demoBloomParticles = false;

                CleanupFountainDemo(ParticleHostLostPolicy.ClearImmediate);
                DestroyHandle(ref _petal3DHandle, ParticleHostLostPolicy.ClearImmediate);
                DestroyHandle(ref _bloomMainHandle, ParticleHostLostPolicy.ClearImmediate);
                DestroyHandle(ref _bloomMaskHandle, ParticleHostLostPolicy.ClearImmediate);
                DestroyHandle(ref _followerHandle, ParticleHostLostPolicy.ClearImmediate);

                followerNode = _followerNode;
                _followerNode = null;

                glow = _softGlowTex;
                petal = _petalTex;
                sheet = _sheetAtlasTex;
                _softGlowTex = null;
                _petalTex = null;
                _sheetAtlasTex = null;
                _softGlowAdditiveMat = MaterialHandle.Invalid;
                _petalAdditiveMat = MaterialHandle.Invalid;
                _sheetAdditiveMat = MaterialHandle.Invalid;
                _cachedMaterialEpoch = -1;
                ClearCachedBlueprints();
            }

            // GPU 贴图与非线程安全 TransformNode 清理统一派发至主线程执行，遵守 FNA 卸载铁律
            if (glow is not null || petal is not null || sheet is not null || followerNode is not null)
            {
                Main.QueueMainThreadAction(() =>
                {
                    if (followerNode is { IsDisposed: false }) followerNode.Cleanup(TransformCleanupReason.Manual);
                    if (glow is { IsDisposed: false }) glow.Dispose();
                    if (petal is { IsDisposed: false }) petal.Dispose();
                    if (sheet is { IsDisposed: false }) sheet.Dispose();
                });
            }
        }

        // ==================== 单次爆发测试实现 ====================

        private void Trigger2DSparkBurst(int count)
        {
            EnsureAssetsReadyLocked();
            if (_sparkBurstEmitter is not null)
                ParticleManager.EmitGlobal(_sparkBurstEmitter, count, GetSpawnOrigin2D());
        }

        private void Trigger10kStressBurst()
        {
            EnsureAssetsReadyLocked();
            if (_stress10kEmitter is not null)
                ParticleManager.EmitGlobal(_stress10kEmitter, 10000, GetSpawnOrigin2D());
        }

        private void TriggerOneFrameFlashBurst()
        {
            EnsureAssetsReadyLocked();
            if (_flash1FrameEmitter is not null)
                ParticleManager.EmitGlobal(_flash1FrameEmitter, 48, GetSpawnOrigin2D());
        }

        private void TriggerCustomSpiralBurst()
        {
            EnsureAssetsReadyLocked();
            MaterialHandle mat = _softGlowAdditiveMat;
            if (!mat.IsValid)
                return;

            XnaVec2 origin = GetSpawnOrigin2D();
            const int count = 360;

            ParticleManager.EmitGlobal(CustomSpiralTemplate, count, origin, (in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng) =>
            {
                Span<ParticleLife> lives = batch.GetSpan<ParticleLife>();
                Span<ParticleSpatial> spatials = batch.GetSpan<ParticleSpatial>();
                Span<ParticlePose2D> poses = batch.GetSpan<ParticlePose2D>();
                Span<ParticleRenderBase> renders = batch.GetSpan<ParticleRenderBase>();
                Span<ParticleColorOverLife> colors = batch.GetSpan<ParticleColorOverLife>();
                Span<ParticleScaleOverLife> scales = batch.GetSpan<ParticleScaleOverLife>();

                batch.Fill(new QuadAtlasRenderData());
                batch.Fill(new ParticleDrag(1.6f));

                XnaVec3 center = ctx.Origin.Position;
                for (int i = 0; i < batch.Count; i++)
                {
                    float t = i / (float)batch.Count;
                    float angle = i * 2.3999632f; // 黄金角分布
                    (float sin, float cos) = MathF.SinCos(angle);
                    float speed = 90f + t * 380f;

                    lives[i] = new ParticleLife(0.9f + t * 0.9f);
                    spatials[i] = new ParticleSpatial(center + new XnaVec3(cos * 12f, sin * 12f, 0f), new XnaVec3(cos * speed, sin * speed, 0f));
                    poses[i] = new ParticlePose2D(angle, rng.NextFloat(-6f, 6f), 1.15f);

                    XnaVec4 startCol = new(0.5f + 0.5f * MathF.Sin(t * MathF.Tau), 0.5f + 0.5f * MathF.Sin(t * MathF.Tau + 2.094f), 0.5f + 0.5f * MathF.Sin(t * MathF.Tau + 4.188f), 1f);
                    XnaVec4 endCol = new(startCol.X, startCol.Y, startCol.Z, 0f);
                    renders[i] = new ParticleRenderBase(BuiltInRenderTarget.AfterDust, mat, new XnaVec2(16f, 16f), startCol);
                    colors[i] = new ParticleColorOverLife(startCol, endCol, EaseType.OutQuad);
                    scales[i] = new ParticleScaleOverLife(1.15f, 0.1f, EaseType.Bell);
                }
            });
        }

        private void TriggerSheetAnimBurst()
        {
            EnsureAssetsReadyLocked();
            if (!_sheetAdditiveMat.IsValid)
                return;

            float frameRate = _sheetModeIndex switch
            {
                0 => 8f,
                1 => 0f,
                _ => -1f,
            };

            ParticleEmitter emitter = ParticleEmitter.Create("Debug.SheetAnim2D")
                .AddModule(new InitLifeModule(1.4f, 2.2f))
                .AddModule(new SpawnShapeModule(EmitterShape.Circle(42f), speed: new FloatRange(65f, 180f)))
                .AddModule(new InitPose2DModule(scale: new FloatRange(0.9f, 1.5f), randomRotation: true, angularVelocity: new FloatRange(-3f, 3f)))
                .AddModule(new InitQuadRenderModule(BuiltInRenderTarget.AfterDust, _sheetAdditiveMat, new XnaVec2(24f, 24f),
                    new XnaVec4(0.45f, 1.0f, 1.1f, 1f), new XnaVec4(1.1f, 0.55f, 1.0f, 1f)))
                .AddModule(new InitColorOverLifeModule(new XnaVec4(0.3f, 0.6f, 1.0f, 0f), EaseType.InQuad))
                .AddModule(new InitScaleOverLifeModule(new XnaVec3(0.3f, 0.3f, 1f), EaseType.SmoothStep))
                .AddModule(new InitDragModule(1.2f))
                .AddModule(new InitSheetAnimModule(columns: 2, rows: 2, frameCount: 4, frameRate: frameRate, randomStartFrame: _sheetModeIndex != 1))
                .Bake();

            ParticleManager.EmitGlobal(emitter, 120, GetSpawnOrigin2D());
        }

        private void TriggerAutoStopPulseInstance()
        {
            EnsureAssetsReadyLocked();
            if (_pulseWaveEmitter is null)
                return;

            var cfg = new ParticleInstanceConfig
            {
                Origin = ParticleOriginBinding.FromPosition2D(GetSpawnOrigin2D()),
                SimulationSpace = ParticleSimulationSpace.World,
                HostLostPolicy = ParticleHostLostPolicy.StopAndFade,
            };

            // 同一份不可变蓝图直接多次 PlayInstance，无需 Clone
            ParticleManager.PlayInstance(in cfg, _pulseWaveEmitter, autoStopWhenComplete: true);
        }

        // ==================== 持续运行子系统更新 ====================

        private void UpdateFountainDemo()
        {
            if (!_demoFountain)
                return;

            if (!ParticleManager.IsInstanceAlive(_fountainHandle))
            {
                if (_fountainPrimaryEmitter is null || _fountainAccentEmitter is null)
                    return;

                var cfg = new ParticleInstanceConfig
                {
                    Origin = ParticleOriginBinding.FromCustom(SampleDynamicOrigin),
                    SimulationSpace = ParticleSimulationSpace.World,
                    HostLostPolicy = ParticleHostLostPolicy.StopAndFade,
                    TimeScale = _fountainTimeScale,
                };

                _fountainHandle = ParticleManager.CreateInstance(in cfg);
                _fountainPrimaryHandle = ParticleManager.AttachEmitter(_fountainHandle, _fountainPrimaryEmitter, autoStopWhenComplete: false);
                _fountainAccentHandle = ParticleManager.AttachEmitter(_fountainHandle, _fountainAccentEmitter, autoStopWhenComplete: false);
                _appliedFountainRate = -1f;
                _appliedFountainAccentEnabled = !_fountainAccentEnabled;
                _appliedFountainTimeScale = _fountainTimeScale;
            }

            // 通过单个发射器句柄实时平滑调节速率、独立启停与时间倍率，无需销毁重建子系统
            if (MathF.Abs(_appliedFountainRate - _fountainRate) > 1f)
            {
                _appliedFountainRate = _fountainRate;
                ParticleManager.SetEmitterRateScale(_fountainPrimaryHandle, _fountainRate / 1000f);
            }

            if (_appliedFountainAccentEnabled != _fountainAccentEnabled)
            {
                _appliedFountainAccentEnabled = _fountainAccentEnabled;
                ParticleManager.SetEmitterEnabled(_fountainAccentHandle, _fountainAccentEnabled);
            }

            if (MathF.Abs(_appliedFountainTimeScale - _fountainTimeScale) > 0.005f)
            {
                _appliedFountainTimeScale = _fountainTimeScale;
                ParticleManager.SetInstanceTimeScale(_fountainHandle, _fountainTimeScale);
            }
        }

        private void Update3DPetalDemo()
        {
            if (!_demo3DPetals)
                return;

            bool needRecreate = !ParticleManager.IsInstanceAlive(_petal3DHandle)
                || _applied3DBillboard != _demo3DBillboard
                || MathF.Abs(_applied3DPerspective - _demo3DPerspective) > 1e-5f;
            if (!needRecreate || !_petalAdditiveMat.IsValid || !_softGlowAdditiveMat.IsValid)
                return;

            DestroyHandle(ref _petal3DHandle, ParticleHostLostPolicy.StopAndFade);
            _applied3DBillboard = _demo3DBillboard;
            _applied3DPerspective = _demo3DPerspective;

            var cfg = new ParticleInstanceConfig
            {
                Origin = ParticleOriginBinding.FromCustom(SampleDynamicOrigin),
                SimulationSpace = ParticleSimulationSpace.World,
                HostLostPolicy = ParticleHostLostPolicy.StopAndFade,
            };

            ParticleEmitter emitter = ParticleEmitter.Create("Debug.Petal3D")
                .WithDuration(2f, loop: true)
                .WithRate(240f)
                .AddModule(new InitLifeModule(1.6f, 2.8f))
                .AddModule(new SpawnShapeModule(EmitterShape.Sphere(96f, emitFromSurface: true), speed: new FloatRange(35f, 110f), constantVelocityOffset: new XnaVec3(0f, -25f, 0f)))
                .AddModule(new InitPose3DModule(scale: new FloatRange(0.8f, 1.55f), scaleMultiplier: _demo3DBillboard ? XnaVec3.One : new XnaVec3(1.1f, 1.6f, 1f),
                    randomRotation: true, angularVelocityMin: new XnaVec3(-4.5f, -4.5f, -4.5f), angularVelocityMax: new XnaVec3(4.5f, 4.5f, 4.5f),
                    billboard: _demo3DBillboard ? ParticleBillboardMode.CameraFacing : ParticleBillboardMode.None, perspectiveFactor: _demo3DPerspective))
                .AddModule(new InitQuadRenderModule(BuiltInRenderTarget.AfterDust, _demo3DBillboard ? _softGlowAdditiveMat : _petalAdditiveMat,
                    _demo3DBillboard ? new XnaVec2(18f, 18f) : new XnaVec2(22f, 22f),
                    _demo3DBillboard ? new XnaVec4(0.35f, 1.25f, 1.4f, 0.95f) : new XnaVec4(1.15f, 0.55f, 0.85f, 0.95f),
                    _demo3DBillboard ? new XnaVec4(1.3f, 0.9f, 0.35f, 0.95f) : new XnaVec4(1.2f, 0.85f, 1.0f, 0.95f)))
                .AddModule(new InitColorOverLifeModule(new XnaVec4(0.8f, 0.25f, 0.9f, 0f), EaseType.OutQuad))
                .AddModule(new InitScaleOverLifeModule(new XnaVec3(0.2f), EaseType.SmoothStep))
                .AddModule(new InitDragModule(0.55f, 0.25f))
                .Bake();

            _petal3DHandle = ParticleManager.PlayInstance(in cfg, emitter, autoStopWhenComplete: false);
        }

        private void UpdateTransformFollowerDemo(XnaVec2 playerCenter)
        {
            if (!_demoTransformFollower)
                return;

            // 参数切换时重建子系统（若节点已被用户手动销毁则保持失活态供观察自动回收，直到点击“重建”）
            if (_followerNode is null)
            {
                SpawnFollowerDemo();
            }
            else if (!_followerNode.IsDisposed && (!ParticleManager.IsInstanceAlive(_followerHandle) || _appliedFollowerLocalSpace != _followerLocalSpace || _appliedFollowerLostPolicyIndex != _followerLostPolicyIndex))
            {
                DestroyHandle(ref _followerHandle, ParticleHostLostPolicy.ClearImmediate);
                AttachFollowerInstanceToNode(_followerNode);
            }

            if (_followerNode is { IsDisposed: false })
            {
                float orbitAng = _elapsed * 2.4f;
                XnaVec3 orbitPos = new(playerCenter.X + MathF.Cos(orbitAng) * 110f, playerCenter.Y + MathF.Sin(orbitAng) * 72f, 0f);
                XnaQuat spin = XnaQuat.CreateFromAxisAngle(XnaVec3.UnitZ, _elapsed * 3.0f);
                _followerNode.LocalTransform = new Transform3D(orbitPos, spin, XnaVec3.One);
            }
        }

        private void SpawnFollowerDemo()
        {
            if (!_softGlowAdditiveMat.IsValid)
                return;

            XnaVec2 center = GetSpawnOrigin2D();
            _followerNode = new TransformNode(new Transform3D(new XnaVec3(center, 0f)), "Debug.ParticleOrbitNode");
            AttachFollowerInstanceToNode(_followerNode);
        }

        private void AttachFollowerInstanceToNode(TransformNode node)
        {
            _appliedFollowerLocalSpace = _followerLocalSpace;
            _appliedFollowerLostPolicyIndex = _followerLostPolicyIndex;
            ParticleHostLostPolicy policy = _followerLostPolicyIndex == 0 ? ParticleHostLostPolicy.StopAndFade : ParticleHostLostPolicy.ClearImmediate;

            var cfg = new ParticleInstanceConfig
            {
                Origin = ParticleOriginBinding.FromTransform(node.GetHandle()),
                SimulationSpace = _followerLocalSpace ? ParticleSimulationSpace.Local : ParticleSimulationSpace.World,
                HostLostPolicy = policy,
            };

            ParticleEmitter emitter = ParticleEmitter.Create("Debug.OrbitFollower")
                .WithDuration(2f, loop: true)
                .WithRate(260f)
                .AddModule(new InitLifeModule(0.75f, 1.35f))
                .AddModule(new SpawnShapeModule(
                    _followerLocalSpace ? EmitterShape.Circle(34f, emitFromEdge: true) : EmitterShape.Circle(8f),
                    speed: _followerLocalSpace ? new FloatRange(18f, 46f) : new FloatRange(20f, 60f),
                    inheritOriginRotation: !_followerLocalSpace))
                .AddModule(new InitPose2DModule(scale: new FloatRange(0.8f, 1.3f)))
                .AddModule(new InitQuadRenderModule(BuiltInRenderTarget.AfterDust, _softGlowAdditiveMat, new XnaVec2(16f, 16f),
                    _followerLocalSpace ? new XnaVec4(0.25f, 1.35f, 0.85f, 1f) : new XnaVec4(1.35f, 0.65f, 0.25f, 1f)))
                .AddModule(new InitColorOverLifeModule(new XnaVec4(0.15f, 0.45f, 1.0f, 0f), EaseType.OutQuad))
                .AddModule(new InitScaleOverLifeModule(XnaVec3.Zero, EaseType.OutQuad))
                .AddModule(new InitDragModule(1.8f))
                .Bake();

            _followerHandle = ParticleManager.PlayInstance(in cfg, emitter, autoStopWhenComplete: false);
        }

        private void UpdateBloomDemo()
        {
            if (!_demoBloomParticles)
                return;

            bool needRecreate = !ParticleManager.IsInstanceAlive(_bloomMainHandle)
                || !ParticleManager.IsInstanceAlive(_bloomMaskHandle)
                || MathF.Abs(_appliedBloomHdr - _bloomHdrIntensity) > 0.01f;
            if (!needRecreate || !_softGlowAdditiveMat.IsValid)
                return;

            DestroyHandle(ref _bloomMainHandle, ParticleHostLostPolicy.StopAndFade);
            DestroyHandle(ref _bloomMaskHandle, ParticleHostLostPolicy.StopAndFade);
            _appliedBloomHdr = _bloomHdrIntensity;

            float hdr = _bloomHdrIntensity;
            ParticleEmitter bloomEmitter = ParticleEmitter.Create("Debug.BloomRing")
                .WithDuration(2f, loop: true)
                .WithRate(180f)
                .AddModule(new InitLifeModule(0.8f, 1.4f))
                .AddModule(new SpawnShapeModule(EmitterShape.Ring(72f, 96f), speed: new FloatRange(24f, 64f)))
                .AddModule(new InitPose2DModule(scale: new FloatRange(0.8f, 1.4f)))
                .AddModule(new InitQuadRenderModule(BuiltInRenderTarget.AfterDust, _softGlowAdditiveMat, new XnaVec2(22f, 22f),
                    new XnaVec4(0.28f * hdr, 1.05f * hdr, 1.35f * hdr, 1f), new XnaVec4(1.35f * hdr, 0.30f * hdr, 1.15f * hdr, 1f)))
                .AddModule(new InitColorOverLifeModule(new XnaVec4(0.2f * hdr, 0.6f * hdr, 1.1f * hdr, 0f), EaseType.OutQuad))
                .AddModule(new InitScaleOverLifeModule(XnaVec3.Zero, EaseType.Bell))
                .AddModule(new InitDragModule(1.5f))
                .Bake();

            var mainCfg = new ParticleInstanceConfig
            {
                Origin = ParticleOriginBinding.FromCustom(SampleDynamicOrigin),
                SimulationSpace = ParticleSimulationSpace.World,
                HostLostPolicy = ParticleHostLostPolicy.StopAndFade,
            };

            // 同一份不可变蓝图零克隆并发挂载至主屏通道与 Selective BloomMask 通道
            var bloomCfg = mainCfg;
            bloomCfg.TargetOverride = RenderManager.PostProcess.BloomMask;

            _bloomMainHandle = ParticleManager.PlayInstance(in mainCfg, bloomEmitter, autoStopWhenComplete: false);
            _bloomMaskHandle = ParticleManager.PlayInstance(in bloomCfg, bloomEmitter, autoStopWhenComplete: false);
        }

        // ==================== 辅助方法、蓝图缓存与程序化贴图 ====================

        private XnaVec2 GetSpawnOrigin2D()
        {
            if (_spawnAtMouse)
                return Main.MouseWorld;
            return Main.LocalPlayer is { active: true } player ? player.Center : Main.screenPosition + new XnaVec2(Main.screenWidth * 0.5f, Main.screenHeight * 0.5f);
        }

        private ParticleOriginSnapshot SampleDynamicOrigin(ParticleOriginSnapshot previous)
        {
            if (Main.gameMenu || Main.LocalPlayer is not { active: true })
                return previous.AsDead();
            return ParticleOriginSnapshot.FromPosition(new XnaVec3(GetSpawnOrigin2D(), 0f));
        }

        private void StopAllContinuousDemos(ParticleHostLostPolicy policy)
        {
            _demoFountain = false;
            _demo3DPetals = false;
            _demoTransformFollower = false;
            _demoBloomParticles = false;

            CleanupFountainDemo(policy);
            DestroyHandle(ref _petal3DHandle, policy);
            CleanupFollowerDemo(policy);
            DestroyHandle(ref _bloomMainHandle, policy);
            DestroyHandle(ref _bloomMaskHandle, policy);
        }

        private void CleanupFountainDemo(ParticleHostLostPolicy policy)
        {
            DestroyHandle(ref _fountainHandle, policy);
            _fountainPrimaryHandle = ParticleEmitterHandle.Invalid;
            _fountainAccentHandle = ParticleEmitterHandle.Invalid;
        }

        private void CleanupFollowerDemo(ParticleHostLostPolicy policy)
        {
            DestroyHandle(ref _followerHandle, policy);
            if (_followerNode is { IsDisposed: false })
                _followerNode.Cleanup(TransformCleanupReason.Manual);
            _followerNode = null;
        }

        private void CleanupWorldDemoHandlesLocked(bool destroyInstances)
        {
            if (destroyInstances)
            {
                CleanupFountainDemo(ParticleHostLostPolicy.ClearImmediate);
                DestroyHandle(ref _petal3DHandle, ParticleHostLostPolicy.ClearImmediate);
                DestroyHandle(ref _bloomMainHandle, ParticleHostLostPolicy.ClearImmediate);
                DestroyHandle(ref _bloomMaskHandle, ParticleHostLostPolicy.ClearImmediate);
            }
            else
            {
                _fountainHandle = ParticleSystemHandle.Invalid;
                _fountainPrimaryHandle = ParticleEmitterHandle.Invalid;
                _fountainAccentHandle = ParticleEmitterHandle.Invalid;
                _petal3DHandle = ParticleSystemHandle.Invalid;
                _bloomMainHandle = ParticleSystemHandle.Invalid;
                _bloomMaskHandle = ParticleSystemHandle.Invalid;
            }

            if (_followerNode is { IsDisposed: false })
                _followerNode.Cleanup(TransformCleanupReason.Manual);
            _followerNode = null;
            if (destroyInstances)
                DestroyHandle(ref _followerHandle, ParticleHostLostPolicy.ClearImmediate);
            else
                _followerHandle = ParticleSystemHandle.Invalid;
        }

        private static void DestroyHandle(ref ParticleSystemHandle handle, ParticleHostLostPolicy policy)
        {
            if (handle.IsValid)
            {
                ParticleManager.DestroyInstance(handle, policy);
                handle = ParticleSystemHandle.Invalid;
            }
        }

        private void EnsureAssetsReadyLocked()
        {
            GraphicsDevice? gd = Main.graphics?.GraphicsDevice;
            if (gd is null || gd.IsDisposed || RenderManager.IsUnloading)
                return;

            bool texturesRecreated = false;
            if (_softGlowTex is null || _softGlowTex.IsDisposed)
            {
                _softGlowTex = CreateSoftGlowTexture(gd, 64);
                texturesRecreated = true;
            }

            if (_petalTex is null || _petalTex.IsDisposed)
            {
                _petalTex = CreateDiamondPetalTexture(gd, 64);
                texturesRecreated = true;
            }

            if (_sheetAtlasTex is null || _sheetAtlasTex.IsDisposed)
            {
                _sheetAtlasTex = CreateSheetAtlas2x2Texture(gd, 128);
                texturesRecreated = true;
            }

            int epoch = RenderManager.Materials.Epoch;
            if (texturesRecreated || _cachedMaterialEpoch != epoch || !_softGlowAdditiveMat.IsValid)
            {
                _softGlowAdditiveMat = RenderManager.Materials.GetOrCreateInstancedMaterial(_softGlowTex, BlendState.Additive, SamplerState.LinearClamp);
                _petalAdditiveMat = RenderManager.Materials.GetOrCreateInstancedMaterial(_petalTex, BlendState.Additive, SamplerState.LinearClamp);
                _sheetAdditiveMat = RenderManager.Materials.GetOrCreateInstancedMaterial(_sheetAtlasTex, BlendState.Additive, SamplerState.LinearClamp);
                _cachedMaterialEpoch = epoch;
                RebuildCachedBlueprints();

                // 材质 Epoch 变更或贴图重建时失效既有持续 Demo 句柄，下步自动使用新 MaterialHandle 重建
                CleanupFountainDemo(ParticleHostLostPolicy.ClearImmediate);
                DestroyHandle(ref _petal3DHandle, ParticleHostLostPolicy.ClearImmediate);
                DestroyHandle(ref _followerHandle, ParticleHostLostPolicy.ClearImmediate);
                DestroyHandle(ref _bloomMainHandle, ParticleHostLostPolicy.ClearImmediate);
                DestroyHandle(ref _bloomMaskHandle, ParticleHostLostPolicy.ClearImmediate);
            }
        }

        private void RebuildCachedBlueprints()
        {
            if (!_softGlowAdditiveMat.IsValid || !_petalAdditiveMat.IsValid)
            {
                ClearCachedBlueprints();
                return;
            }

            _sparkBurstEmitter = ParticleEmitter.Create("Debug.Spark2D")
                .AddModule(new InitLifeModule(0.45f, 1.15f))
                .AddModule(new SpawnShapeModule(EmitterShape.Circle(14f), speed: new FloatRange(160f, 520f), constantVelocityOffset: new XnaVec3(0f, -110f, 0f)))
                .AddModule(new InitPose2DModule(scale: new FloatRange(0.75f, 1.45f), scaleMultiplier: new XnaVec2(1.35f, 0.55f)))
                .AddModule(new InitQuadRenderModule(BuiltInRenderTarget.AfterDust, _petalAdditiveMat, new XnaVec2(14f, 14f),
                    new XnaVec4(1.4f, 0.95f, 0.25f, 1f), new XnaVec4(1.5f, 0.45f, 0.15f, 1f)))
                .AddModule(new InitGravityModule(620f))
                .AddModule(new InitDragModule(1.8f))
                .AddModule(new InitColorOverLifeModule(new XnaVec4(0.9f, 0.12f, 0.05f, 0f), EaseType.InQuad))
                .AddModule(new InitScaleOverLifeModule(new XnaVec3(0.15f, 0.15f, 1f), EaseType.OutQuad))
                .AddModule(new InitVelocityAlignModule(0f, 0.0045f, 25f))
                .Bake();

            _stress10kEmitter = ParticleEmitter.Create("Debug.Stress10k")
                .AddModule(new InitLifeModule(1.2f, 2.6f))
                .AddModule(new SpawnShapeModule(EmitterShape.Ring(18f, 64f), speed: new FloatRange(90f, 460f)))
                .AddModule(new InitPose2DModule(scale: new FloatRange(0.6f, 1.4f)))
                .AddModule(new InitQuadRenderModule(BuiltInRenderTarget.AfterDust, _softGlowAdditiveMat, new XnaVec2(10f, 10f),
                    new XnaVec4(0.25f, 0.95f, 1.25f, 0.95f), new XnaVec4(1.25f, 0.35f, 1.15f, 0.95f)))
                .AddModule(new InitColorOverLifeModule(new XnaVec4(0.15f, 0.45f, 1.0f, 0f), EaseType.InCubic))
                .AddModule(new InitScaleOverLifeModule(XnaVec3.Zero, EaseType.InQuad))
                .AddModule(new InitDragModule(1.4f))
                .Bake();

            _flash1FrameEmitter = ParticleEmitter.Create("Debug.OneFrameFlash")
                .AddModule(new InitLifeModule(1f / 60f))
                .AddModule(new SpawnShapeModule(EmitterShape.Circle(84f, emitFromEdge: true)))
                .AddModule(new InitPose2DModule())
                .AddModule(new InitQuadRenderModule(BuiltInRenderTarget.AfterDust, _softGlowAdditiveMat, new XnaVec2(32f, 32f), new XnaVec4(1.8f, 1.8f, 0.9f, 1f)))
                .Bake();

            _pulseWaveEmitter = ParticleEmitter.Create("Debug.AutoStopPulse")
                .WithDuration(1.35f, loop: false)
                .WithBurst(0f, 90, 120, cycles: 3, interval: 0.45f)
                .AddModule(new InitLifeModule(0.55f, 0.85f))
                .AddModule(new SpawnShapeModule(EmitterShape.Circle(20f, emitFromEdge: true), speed: new FloatRange(220f, 320f)))
                .AddModule(new InitPose2DModule(scale: new FloatRange(0.85f, 1.35f)))
                .AddModule(new InitQuadRenderModule(BuiltInRenderTarget.AfterDust, _softGlowAdditiveMat, new XnaVec2(14f, 14f),
                    new XnaVec4(0.3f, 1.3f, 1.0f, 1f), new XnaVec4(1.2f, 0.9f, 0.3f, 1f)))
                .AddModule(new InitColorOverLifeModule(new XnaVec4(0.2f, 0.5f, 1.0f, 0f), EaseType.OutQuad))
                .AddModule(new InitScaleOverLifeModule(XnaVec3.Zero, EaseType.OutQuad))
                .AddModule(new InitDragModule(2.5f))
                .Bake();

            // 基准速率 1000/s：UI 滑条通过 SetEmitterRateScale 实时按倍率调速，无需重建子系统
            _fountainPrimaryEmitter = ParticleEmitter.Create("Debug.FountainPrimary")
                .WithSchedule(duration: 2f, loop: true, maxParticlesPerTick: 512)
                .WithRate(1000f)
                .AddModule(new InitLifeModule(0.9f, 1.6f))
                .AddModule(new SpawnShapeModule(EmitterShape.Cone2D(-MathF.PI * 0.5f, 0.42f, 10f), speed: new FloatRange(260f, 520f)))
                .AddModule(new InitPose2DModule(scale: new FloatRange(0.7f, 1.25f)))
                .AddModule(new InitQuadRenderModule(BuiltInRenderTarget.AfterDust, _softGlowAdditiveMat, new XnaVec2(11f, 11f),
                    new XnaVec4(0.25f, 1.15f, 1.4f, 0.9f), new XnaVec4(0.9f, 0.45f, 1.4f, 0.9f)))
                .AddModule(new InitGravityModule(440f))
                .AddModule(new InitDragModule(0.55f))
                .AddModule(new InitColorOverLifeModule(new XnaVec4(0.1f, 0.25f, 0.9f, 0f), EaseType.InQuad))
                .AddModule(new InitScaleOverLifeModule(new XnaVec3(0.15f, 0.15f, 1f), EaseType.InQuad))
                .AddModule(new InitVelocityAlignModule(0f, 0.003f, 25f))
                .Bake();

            _fountainAccentEmitter = ParticleEmitter.Create("Debug.FountainAccent")
                .WithDuration(2f, loop: true)
                .WithRate(90f)
                .AddModule(new InitLifeModule(0.55f, 0.95f))
                .AddModule(new SpawnShapeModule(EmitterShape.Circle(46f, emitFromEdge: true), speed: new FloatRange(40f, 95f)))
                .AddModule(new InitPose2DModule(scale: new FloatRange(0.9f, 1.4f)))
                .AddModule(new InitQuadRenderModule(BuiltInRenderTarget.AfterDust, _petalAdditiveMat, new XnaVec2(16f, 16f),
                    new XnaVec4(1.45f, 1.15f, 0.35f, 1f), new XnaVec4(1.5f, 0.65f, 0.2f, 1f)))
                .AddModule(new InitColorOverLifeModule(new XnaVec4(1.0f, 0.3f, 0.1f, 0f), EaseType.OutQuad))
                .AddModule(new InitScaleOverLifeModule(XnaVec3.Zero, EaseType.OutQuad))
                .AddModule(new InitDragModule(2.2f))
                .AddModule(new InitVelocityAlignModule(0f, 0.004f, 16f))
                .Bake();
        }

        private void ClearCachedBlueprints()
        {
            _sparkBurstEmitter = null;
            _stress10kEmitter = null;
            _flash1FrameEmitter = null;
            _pulseWaveEmitter = null;
            _fountainPrimaryEmitter = null;
            _fountainAccentEmitter = null;
        }

        private static Texture2D CreateSoftGlowTexture(GraphicsDevice gd, int size)
        {
            var tex = new Texture2D(gd, size, size, mipMap: false, SurfaceFormat.Color);
            var pixels = new XnaColor[size * size];
            float invHalf = 2f / size;

            for (int y = 0; y < size; y++)
            {
                float dy = (y + 0.5f) * invHalf - 1f;
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) * invHalf - 1f;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    float falloff = Math.Clamp(1f - dist, 0f, 1f);
                    float glow = falloff * falloff * (0.45f + 0.55f * falloff);
                    byte v = (byte)Math.Clamp((int)MathF.Round(glow * 255f), 0, 255);
                    pixels[y * size + x] = new XnaColor(v, v, v, v);
                }
            }

            tex.SetData(pixels);
            return tex;
        }

        private static Texture2D CreateDiamondPetalTexture(GraphicsDevice gd, int size)
        {
            var tex = new Texture2D(gd, size, size, mipMap: false, SurfaceFormat.Color);
            var pixels = new XnaColor[size * size];
            float invHalf = 2f / size;

            for (int y = 0; y < size; y++)
            {
                float dy = (y + 0.5f) * invHalf - 1f;
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) * invHalf - 1f;
                    // 细长菱形/花瓣轮廓（主轴沿 +X 适配 VelocityAlign 拉伸）
                    float diamond = MathF.Abs(dx) * 1.05f + MathF.Abs(dy) * 1.65f;
                    float edge = Math.Clamp((1f - diamond) * 3.2f, 0f, 1f);
                    float core = Math.Clamp(1f - diamond, 0f, 1f);
                    float alpha = edge * edge;
                    float lum = alpha * (0.65f + 0.35f * core);
                    byte c = (byte)Math.Clamp((int)MathF.Round(lum * 255f), 0, 255);
                    byte a = (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
                    pixels[y * size + x] = new XnaColor(c, c, c, a);
                }
            }

            tex.SetData(pixels);
            return tex;
        }

        private static Texture2D CreateSheetAtlas2x2Texture(GraphicsDevice gd, int size)
        {
            var tex = new Texture2D(gd, size, size, mipMap: false, SurfaceFormat.Color);
            var pixels = new XnaColor[size * size];
            int half = size / 2;
            float invQuarter = 2f / half;

            for (int y = 0; y < size; y++)
            {
                int row = y < half ? 0 : 1;
                float ly = (y - row * half + 0.5f) * invQuarter - 1f;
                for (int x = 0; x < size; x++)
                {
                    int col = x < half ? 0 : 1;
                    float lx = (x - col * half + 0.5f) * invQuarter - 1f;
                    int cell = row * 2 + col;

                    float r = MathF.Sqrt(lx * lx + ly * ly);
                    float mask = cell switch
                    {
                        0 => Math.Clamp((0.82f - r) * 3.5f, 0f, 1f),
                        1 => Math.Clamp((0.88f - (MathF.Sqrt(MathF.Abs(lx)) + MathF.Sqrt(MathF.Abs(ly))) * 0.65f) * 3.2f, 0f, 1f),
                        2 => Math.Clamp((0.20f - MathF.Abs(r - 0.62f)) * 5.0f, 0f, 1f),
                        _ => Math.Clamp((0.85f - (MathF.Abs(lx) + MathF.Abs(ly))) * 4.0f, 0f, 1f),
                    };

                    byte v = (byte)Math.Clamp((int)MathF.Round(mask * mask * 255f), 0, 255);
                    pixels[y * size + x] = new XnaColor(v, v, v, v);
                }
            }

            tex.SetData(pixels);
            return tex;
        }
    }
}
#endif
