#if DEBUG
#nullable enable

using System;
using System.Numerics;
using Apotheosis.DebugTool.Core;
using Apotheosis.DebugTool.Theme;
using Apotheosis.Materials;
using Apotheosis.Runtime.Rendering;
using Apotheosis.Runtime.Rendering.Geometry;
using Apotheosis.Runtime.Rendering.Materials;
using Apotheosis.Runtime.Rendering.PostProcess;
using Hexa.NET.ImGui;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaVec2 = Microsoft.Xna.Framework.Vector2;
using SysVec3 = System.Numerics.Vector3;

namespace Apotheosis.DebugTool.Panels
{
    /// <summary>后处理系统调参与实测面板（仅 Development 模式自动注册）。</summary>
    [NoJIT]
    internal sealed class PostProcessPanel : IDebugPanel
    {
        private readonly object _demoLock = new();
        private bool _demoBloom;
        private bool _demoDistortion;
        private bool _preDrawHooked;
        private float _elapsed;
        private volatile bool _unloaded;

        private MeshHandle _bloomMeshHandle;
        private MeshHandle _distortionMeshHandle;

        public string Id => "post_process";
        public string DisplayName => "后处理";
        public int Order => 15;

        public void OnUpdate(float deltaSeconds)
        {
            if (_unloaded || RenderManager.IsUnloading || Main.dedServ)
                return;

            // 退回主菜单时立即回收 Demo 动态网格，避免闲置常驻
            if (Main.gameMenu)
            {
                ReleaseBloomMesh();
                ReleaseDistortionMesh();
                return;
            }

            if (!Main.gamePaused)
                _elapsed += deltaSeconds;
        }

        public void OnDraw()
        {
            PostProcessModule pp = RenderManager.PostProcess;
            PostProcessStats stats = pp.GetLastStats();

            CyberpunkWidgets.SectionHeader("POST_PROCESS_TELEMETRY", stats.ExecutedLastFrame ? "ACTIVE" : "IDLE");

            float availW = ImGui.GetContentRegionAvail().X;
            float cardW = Math.Max(105f, (availW - 8f) * 0.5f);

            CyberpunkWidgets.MetricCard("STATUS", stats.ExecutedLastFrame ? "ACTIVE" : "IDLE",
                stats.ExecutedLastFrame ? CyberpunkTheme.NeonGreen : CyberpunkTheme.TextMuted, cardW);
            ImGui.SameLine(0f, 8f);
            CyberpunkWidgets.MetricCard("PASSES", $"{stats.PassCount}", CyberpunkTheme.NeonCyan, cardW);

            CyberpunkWidgets.MetricCard("BLOOM_MIPS", $"{stats.BloomMipLevelsUsed}", CyberpunkTheme.AcidYellow, cardW);
            ImGui.SameLine(0f, 8f);
            CyberpunkWidgets.MetricCard("HDR_FORMAT", stats.EffectiveHdrFormat.ToString(), CyberpunkTheme.NeonMagenta, cardW);

            ImGui.TextUnformatted(
                $"Bloom: {(stats.BloomExecuted ? "ON" : "-")}  |  " +
                $"Distort: {(stats.DistortionExecuted ? "ON" : "-")}  |  " +
                $"ColorAdj: {(stats.ColorAdjustmentsExecuted ? "ON" : "-")}  |  " +
                $"SplitTone: {(stats.SplitToningExecuted ? "ON" : "-")}");

            if (ImGui.Button("全部重置为默认"))
                pp.ResetAllSettings();

            // 1. Selective Pyramid Bloom
            CyberpunkWidgets.SectionHeader("1_SELECTIVE_BLOOM", pp.Bloom.Enabled ? "ON" : "OFF");
            bool bloomEnabled = pp.Bloom.Enabled;
            if (ImGui.Checkbox("启用 Bloom##BloomEn", ref bloomEnabled)) pp.Bloom.Enabled = bloomEnabled;
            ImGui.SameLine();
            if (ImGui.SmallButton("重置##BloomRst")) pp.Bloom.Reset();

            float bloomIntensity = pp.Bloom.Intensity;
            if (ImGui.SliderFloat("强度 (Intensity)", ref bloomIntensity, 0f, 4f, "%.2f")) pp.Bloom.Intensity = bloomIntensity;

            float bloomScatter = pp.Bloom.Scatter;
            if (ImGui.SliderFloat("扩散 (Scatter)", ref bloomScatter, 0.05f, 0.98f, "%.2f")) pp.Bloom.Scatter = bloomScatter;

            int mipCount = pp.Bloom.MipCount;
            if (ImGui.SliderInt("金字塔级数 (Mips)", ref mipCount, 2, 6)) pp.Bloom.MipCount = mipCount;

            float clampMax = pp.Bloom.ClampMax;
            if (ImGui.SliderFloat("HDR 钳位上限", ref clampMax, 1f, 32f, "%.1f")) pp.Bloom.ClampMax = clampMax;

            SysVec3 bloomTint = ToVec3(pp.Bloom.Tint);
            if (ImGui.ColorEdit3("光晕染色 (Tint)", ref bloomTint)) pp.Bloom.Tint = ToXnaColor(bloomTint);

            // 2. Distortion
            CyberpunkWidgets.SectionHeader("2_DISTORTION_FP16", !pp.IsDistortionSupported ? "UNSUPPORTED" : (pp.Distortion.Enabled ? "ON" : "OFF"));
            if (!pp.IsDistortionSupported)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.TextMuted);
                ImGui.TextUnformatted("当前显卡不支持有符号浮点 RT，扭曲通道已自动禁用。");
                ImGui.PopStyleColor();
            }

            bool distortEnabled = pp.Distortion.Enabled;
            if (ImGui.Checkbox("启用扭曲##DistEn", ref distortEnabled)) pp.Distortion.Enabled = distortEnabled;
            ImGui.SameLine();
            if (ImGui.SmallButton("重置##DistRst")) pp.Distortion.Reset();

            float distStrength = pp.Distortion.Strength;
            if (ImGui.SliderFloat("扭曲强度 (Strength)", ref distStrength, -0.25f, 0.25f, "%.3f")) pp.Distortion.Strength = distStrength;

            float distMax = pp.Distortion.MaxOffset;
            if (ImGui.SliderFloat("最大偏移钳位", ref distMax, 0f, 0.35f, "%.3f")) pp.Distortion.MaxOffset = distMax;

            float distChroma = pp.Distortion.ChromaticAberration;
            if (ImGui.SliderFloat("局部色散 (Chromatic)", ref distChroma, 0f, 2f, "%.2f")) pp.Distortion.ChromaticAberration = distChroma;

            // 3. Color Adjustments
            CyberpunkWidgets.SectionHeader("3_COLOR_ADJUSTMENTS", pp.ColorAdjustments.IsActive ? "ACTIVE" : "NEUTRAL");
            bool colorEnabled = pp.ColorAdjustments.Enabled;
            if (ImGui.Checkbox("启用颜色调整##ColEn", ref colorEnabled)) pp.ColorAdjustments.Enabled = colorEnabled;
            ImGui.SameLine();
            if (ImGui.SmallButton("重置##ColRst")) pp.ColorAdjustments.Reset();

            float exposure = pp.ColorAdjustments.PostExposure;
            if (ImGui.SliderFloat("曝光 (Exposure EV)", ref exposure, -4f, 4f, "%.2f")) pp.ColorAdjustments.PostExposure = exposure;

            float contrast = pp.ColorAdjustments.Contrast;
            if (ImGui.SliderFloat("对比度 (Contrast)", ref contrast, -100f, 100f, "%.0f")) pp.ColorAdjustments.Contrast = contrast;

            float saturation = pp.ColorAdjustments.Saturation;
            if (ImGui.SliderFloat("饱和度 (Saturation)", ref saturation, -100f, 100f, "%.0f")) pp.ColorAdjustments.Saturation = saturation;

            float hueShift = pp.ColorAdjustments.HueShift;
            if (ImGui.SliderFloat("色相偏移 (Hue)", ref hueShift, -180f, 180f, "%.0f")) pp.ColorAdjustments.HueShift = hueShift;

            SysVec3 colorFilter = ToVec3(pp.ColorAdjustments.ColorFilter);
            if (ImGui.ColorEdit3("滤镜染色 (Filter)", ref colorFilter)) pp.ColorAdjustments.ColorFilter = ToXnaColor(colorFilter);

            // 4. Split Toning
            CyberpunkWidgets.SectionHeader("4_SPLIT_TONING", pp.SplitToning.IsActive ? "ACTIVE" : "NEUTRAL");
            bool splitEnabled = pp.SplitToning.Enabled;
            if (ImGui.Checkbox("启用分离色调##SplitEn", ref splitEnabled)) pp.SplitToning.Enabled = splitEnabled;
            ImGui.SameLine();
            if (ImGui.SmallButton("重置##SplitRst")) pp.SplitToning.Reset();

            SysVec3 shadows = ToVec3(pp.SplitToning.Shadows);
            if (ImGui.ColorEdit3("暗部色调 (Shadows)", ref shadows)) pp.SplitToning.Shadows = ToXnaColor(shadows);

            SysVec3 highlights = ToVec3(pp.SplitToning.Highlights);
            if (ImGui.ColorEdit3("亮部色调 (Highlights)", ref highlights)) pp.SplitToning.Highlights = ToXnaColor(highlights);

            float balance = pp.SplitToning.Balance;
            if (ImGui.SliderFloat("暗亮平衡 (Balance)", ref balance, -1f, 1f, "%.2f")) pp.SplitToning.Balance = balance;

            // 5. 实测 Demo
            CyberpunkWidgets.SectionHeader("VERIFICATION_DEMOS", Main.gameMenu ? "ENTER_WORLD" : "READY");
            if (Main.gameMenu)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.TextMuted);
                ImGui.TextUnformatted("进入世界后可勾选下方开关验证选择性 Bloom 与 FP16 冲击波扭曲。");
                ImGui.PopStyleColor();
                return;
            }

            if (ImGui.Checkbox("Demo A: 选择性 Bloom 魔法阵 (仅自家特效发光)", ref _demoBloom))
            {
                if (!_demoBloom)
                    ReleaseBloomMesh();
                SyncPreDrawHook();
            }

            if (ImGui.Checkbox("Demo B: 双波 FP16 径向冲击波扭曲 (同步扭曲 Bloom)", ref _demoDistortion))
            {
                if (!_demoDistortion)
                    ReleaseDistortionMesh();
                SyncPreDrawHook();
            }
        }

        public void OnUnload()
        {
            lock (_demoLock)
            {
                _unloaded = true;
                _demoBloom = false;
                _demoDistortion = false;

                if (_preDrawHooked)
                {
                    Main.OnPreDraw -= OnPreDrawSubmitDemos;
                    _preDrawHooked = false;
                }

                ReleaseBloomMeshLocked();
                ReleaseDistortionMeshLocked();
            }
        }

        private void SyncPreDrawHook()
        {
            lock (_demoLock)
            {
                bool needHook = !_unloaded && (_demoBloom || _demoDistortion);
                if (needHook && !_preDrawHooked)
                {
                    // 挂在 Main.OnPreDraw 按实际渲染帧提交 Demo，避免 >60 FPS 刷新率下出现隔帧闪烁
                    Main.OnPreDraw += OnPreDrawSubmitDemos;
                    _preDrawHooked = true;
                }
                else if (!needHook && _preDrawHooked)
                {
                    Main.OnPreDraw -= OnPreDrawSubmitDemos;
                    _preDrawHooked = false;
                }
            }
        }

        private void OnPreDrawSubmitDemos(GameTime gameTime)
        {
            if (_unloaded || RenderManager.IsUnloading || Main.dedServ || Main.gameMenu || Main.LocalPlayer is not { active: true } player)
                return;

            XnaVec2 center = player.Center;

            lock (_demoLock)
            {
                if (_unloaded || RenderManager.IsUnloading)
                    return;

                // Demo A：Selective Bloom 魔法阵（同时提交主屏本体 + BloomMask）
                if (_demoBloom)
                {
                    if (!_bloomMeshHandle.IsValid)
                        _bloomMeshHandle = RenderManager.Meshes.AcquireDynamic(512, 1536);

                    if (RenderManager.Meshes.TryGet(_bloomMeshHandle, out IMesh? rawMesh) && rawMesh is DynamicMesh mesh)
                    {
                        mesh.Clear();

                        float pulse = 1f + 0.06f * MathF.Sin(_elapsed * 3.0f);
                        var cyan = new XnaColor(60, 235, 255, 255);
                        var magenta = new XnaColor(255, 85, 220, 255);
                        var gold = new XnaColor(255, 225, 105, 255);

                        mesh.AddRing(center, 128f * pulse, thickness: 3.0f, segments: 64, cyan);
                        mesh.AddRing(center, 92f * pulse, thickness: 2.4f, segments: 48, magenta);
                        mesh.AddPolygon(center, 92f * pulse, sides: 3, thickness: 2.6f, rotation: _elapsed * 1.1f, gold);
                        mesh.AddPolygon(center, 92f * pulse, sides: 3, thickness: 2.6f, rotation: _elapsed * 1.1f + MathF.PI, gold);

                        for (int i = 0; i < 6; i++)
                        {
                            float ang = _elapsed * 2.1f + i * (MathF.Tau / 6f);
                            XnaVec2 orb = center + new XnaVec2(MathF.Cos(ang) * 128f * pulse, MathF.Sin(ang) * 128f * pulse);
                            mesh.AddRing(orb, 9f, thickness: 4.0f, segments: 16, i % 2 == 0 ? cyan : gold);
                        }

                        MaterialHandle mat = RenderManager.Materials.DefaultAdditive2D;
                        RenderManager.Commands.Submit(RenderManager.Targets.AfterProjectiles, mat, _bloomMeshHandle, subLayer: 10, sortOrder: 0);
                        RenderManager.Commands.Submit(RenderManager.PostProcess.BloomMask, mat, _bloomMeshHandle, subLayer: 0, sortOrder: 0);
                    }
                }

                // Demo B：双错峰扩散径向冲击波写入 FP16 DistortionMask（仅在 GPU 支持有符号浮点扭曲时提交）
                if (_demoDistortion && RenderManager.PostProcess.IsDistortionSupported)
                {
                    if (!_distortionMeshHandle.IsValid)
                        _distortionMeshHandle = RenderManager.Meshes.AcquireDynamic(32, 48);

                    if (RenderManager.Meshes.TryGet(_distortionMeshHandle, out IMesh? rawDist) && rawDist is DynamicMesh mesh)
                    {
                        mesh.Clear();

                        for (int wave = 0; wave < 2; wave++)
                        {
                            float phase = (_elapsed * 0.65f + wave * 0.5f) % 1.0f;
                            float size = 90f + phase * 420f;
                            float fade = MathF.Sin(phase * MathF.PI);
                            byte alpha = (byte)Math.Clamp((int)(fade * 255f), 0, 255);
                            XnaVec2 offsetCenter = center + (wave == 0 ? XnaVec2.Zero : new XnaVec2(55f, -35f));

                            mesh.AddSpriteQuad(offsetCenter, new XnaVec2(size, size), new XnaColor(255, 255, 255, alpha),
                                rotation: 0f, origin: new XnaVec2(size * 0.5f, size * 0.5f));
                        }

                        RenderManager.Commands.Submit(RenderManager.PostProcess.DistortionMask, DistortionMaterials.RadialRing,
                            _distortionMeshHandle, subLayer: 0, sortOrder: 0);
                    }
                }
            }
        }

        private void ReleaseBloomMesh()
        {
            lock (_demoLock)
            {
                ReleaseBloomMeshLocked();
            }
        }

        private void ReleaseDistortionMesh()
        {
            lock (_demoLock)
            {
                ReleaseDistortionMeshLocked();
            }
        }

        private void ReleaseBloomMeshLocked()
        {
            if (_bloomMeshHandle.IsValid)
            {
                RenderManager.Meshes.ReleaseDynamic(_bloomMeshHandle);
                _bloomMeshHandle = default;
            }
        }

        private void ReleaseDistortionMeshLocked()
        {
            if (_distortionMeshHandle.IsValid)
            {
                RenderManager.Meshes.ReleaseDynamic(_distortionMeshHandle);
                _distortionMeshHandle = default;
            }
        }

        private static SysVec3 ToVec3(XnaColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

        private static XnaColor ToXnaColor(SysVec3 v)
            => new((byte)Math.Clamp((int)MathF.Round(v.X * 255f), 0, 255),
                   (byte)Math.Clamp((int)MathF.Round(v.Y * 255f), 0, 255),
                   (byte)Math.Clamp((int)MathF.Round(v.Z * 255f), 0, 255), 255);
    }
}
#endif
