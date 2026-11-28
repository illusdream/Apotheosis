#if DEBUG
#nullable enable

using System;
using Apotheosis.DebugTool.Core;
using Apotheosis.DebugTool.Theme;
using Apotheosis.Runtime.Rendering;
using Apotheosis.Runtime.Rendering.PostProcess;
using Hexa.NET.ImGui;
using Terraria.ModLoader;
using SysVec3 = System.Numerics.Vector3;
using XnaColor = Microsoft.Xna.Framework.Color;

namespace Apotheosis.DebugTool.Panels
{
    /// <summary>后处理系统遥测与实时调参面板。</summary>
    [NoJIT]
    internal sealed class PostProcessPanel : IDebugPanel
    {
        public string Id => "post_process";
        public string DisplayName => "后处理";
        public int Order => 15;

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
        }

        private static SysVec3 ToVec3(XnaColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

        private static XnaColor ToXnaColor(SysVec3 v)
            => new((byte)Math.Clamp((int)MathF.Round(v.X * 255f), 0, 255),
                   (byte)Math.Clamp((int)MathF.Round(v.Y * 255f), 0, 255),
                   (byte)Math.Clamp((int)MathF.Round(v.Z * 255f), 0, 255), 255);
    }
}
#endif
