#if DEBUG
#nullable enable

using System;
using System.IO;
using System.Numerics;
using Hexa.NET.ImGui;
using Terraria.ModLoader;

namespace Apotheosis.DebugTool.Theme
{
    /// <summary>
    /// 调试面板运行时可调外观与诊断配置。
    /// </summary>
    [NoJIT]
    internal sealed class DebugThemeSettings
    {
        public float WindowOpacity = 0.92f;
        public float FontScale = 1.0f;
        public bool EnableScanlines = true;
        public bool LockWindowPosition = false;
        public bool ShowImGuiDemo = false;
        public bool ShowImGuiMetrics = false;
        public bool RequestResetLayout = false;
    }

    /// <summary>
    /// 赛博朋克（Night-City Terminal）视觉主题与中英文混合字体配置。
    /// </summary>
    [NoJIT]
    internal static class CyberpunkTheme
    {
        // ==================== 核心霓虹色板 ====================

        /// <summary>深空暗夜底色 (#0A0D14)</summary>
        public static readonly Vector4 BgObsidian = new(0.039f, 0.051f, 0.078f, 1.00f);

        /// <summary>侧栏/子容器底色 (#0F1420)</summary>
        public static readonly Vector4 BgSidebar = new(0.059f, 0.078f, 0.125f, 0.96f);

        /// <summary>卡片/控件基础底色 (#141C2E)</summary>
        public static readonly Vector4 BgCard = new(0.078f, 0.110f, 0.180f, 0.92f);

        /// <summary>悬停抬升底色 (#1C2942)</summary>
        public static readonly Vector4 BgElevated = new(0.110f, 0.161f, 0.259f, 0.95f);

        /// <summary>主霓虹色：电光青 (#00F0FF)</summary>
        public static readonly Vector4 NeonCyan = new(0.000f, 0.941f, 1.000f, 1.00f);

        /// <summary>暗电光青 (#009EB0)</summary>
        public static readonly Vector4 NeonCyanDim = new(0.000f, 0.620f, 0.690f, 1.00f);

        /// <summary>辅霓虹色：赛博洋红 (#FF2A85)</summary>
        public static readonly Vector4 NeonMagenta = new(1.000f, 0.165f, 0.522f, 1.00f);

        /// <summary>遥测/警告高亮：酸性黄 (#FCEE0A)</summary>
        public static readonly Vector4 AcidYellow = new(0.988f, 0.933f, 0.039f, 1.00f);

        /// <summary>状态正常绿 (#2BF59D)</summary>
        public static readonly Vector4 NeonGreen = new(0.169f, 0.961f, 0.616f, 1.00f);

        /// <summary>主文字色：冷冰白 (#E6EFFA)</summary>
        public static readonly Vector4 TextPrimary = new(0.902f, 0.937f, 0.980f, 1.00f);

        /// <summary>次级文字色：钢蓝灰 (#7D8FA8)</summary>
        public static readonly Vector4 TextMuted = new(0.490f, 0.561f, 0.659f, 1.00f);

        /// <summary>冷青边框线</summary>
        public static readonly Vector4 BorderTech = new(0.082f, 0.450f, 0.540f, 0.55f);

        public static string LoadedFontSummary { get; private set; } = "Default";

        /// <summary>
        /// 配置中英文混合字体（利用 Dear ImGui 1.92 动态字体图集，按需光栅化汉字，启动零卡顿）。
        /// </summary>
        public static unsafe void ConfigureFonts(ImGuiIOPtr io)
        {
            var fonts = io.Fonts;
            fonts.TexDesiredFormat = ImTextureFormat.Rgba32;

            string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            const float baseSize = 19.5f;

            string? primaryFont = FindFirstExisting(
                fontsDir,
                "CascadiaMono.ttf",
                "CascadiaCode.ttf",
                "consola.ttf",
                "segoeui.ttf");

            string? cjkFont = FindFirstExisting(
                fontsDir,
                "msyh.ttc",
                "msyhbd.ttc",
                "simhei.ttf",
                "Deng.ttf");

            bool loadedPrimary = false;
            if (primaryFont is not null)
            {
                var primaryCfg = ImGui.ImFontConfig();
                primaryCfg.OversampleH = 2;
                primaryCfg.OversampleV = 1;
                primaryCfg.PixelSnapH = true;

                var font = fonts.AddFontFromFileTTF(primaryFont, baseSize, primaryCfg);
                if (font != null)
                {
                    loadedPrimary = true;
                    LoadedFontSummary = Path.GetFileName(primaryFont);
                }
            }

            if (!loadedPrimary)
            {
                fonts.AddFontDefault();
                LoadedFontSummary = "ProggyClean (Default)";
            }

            if (cjkFont is not null)
            {
                var cjkCfg = ImGui.ImFontConfig();
                cjkCfg.MergeMode = true;
                cjkCfg.OversampleH = 1;
                cjkCfg.OversampleV = 1;
                cjkCfg.PixelSnapH = true;
                cjkCfg.GlyphOffset = new Vector2(0f, 0.5f);

                // Dear ImGui 1.92+ 启用 RendererHasTextures 后自动按需光栅化所有 Unicode/中文字形
                var merged = fonts.AddFontFromFileTTF(cjkFont, baseSize + 1.0f, cjkCfg);
                if (merged != null)
                    LoadedFontSummary += " + " + Path.GetFileName(cjkFont);
            }
        }

        /// <summary>
        /// 应用赛博朋克几何与配色风格。
        /// </summary>
        public static void ApplyStyle(DebugThemeSettings settings)
        {
            var style = ImGui.GetStyle();

            // 锐利硬核终端几何
            style.WindowPadding = new Vector2(10f, 10f);
            style.FramePadding = new Vector2(8f, 5f);
            style.CellPadding = new Vector2(6f, 4f);
            style.ItemSpacing = new Vector2(8f, 6f);
            style.ItemInnerSpacing = new Vector2(6f, 4f);
            style.IndentSpacing = 14f;
            style.ScrollbarSize = 10f;
            style.GrabMinSize = 8f;

            style.WindowBorderSize = 1.0f;
            style.ChildBorderSize = 1.0f;
            style.PopupBorderSize = 1.0f;
            style.FrameBorderSize = 1.0f;
            style.TabBorderSize = 0.0f;

            style.WindowRounding = 2.0f;
            style.ChildRounding = 2.0f;
            style.FrameRounding = 2.0f;
            style.PopupRounding = 2.0f;
            style.ScrollbarRounding = 1.0f;
            style.GrabRounding = 1.0f;
            style.TabRounding = 1.0f;
            style.SeparatorTextBorderSize = 1.0f;

            style.FontScaleMain = Math.Clamp(settings.FontScale, 0.75f, 1.50f);

            float alpha = Math.Clamp(settings.WindowOpacity, 0.25f, 1.0f);
            var colors = style.Colors;

            colors[(int)ImGuiCol.Text] = TextPrimary;
            colors[(int)ImGuiCol.TextDisabled] = TextMuted;

            colors[(int)ImGuiCol.WindowBg] = new Vector4(BgObsidian.X, BgObsidian.Y, BgObsidian.Z, alpha);
            colors[(int)ImGuiCol.ChildBg] = new Vector4(BgSidebar.X, BgSidebar.Y, BgSidebar.Z, alpha * 0.92f);
            colors[(int)ImGuiCol.PopupBg] = new Vector4(0.055f, 0.075f, 0.120f, 0.97f);

            colors[(int)ImGuiCol.Border] = BorderTech;
            colors[(int)ImGuiCol.BorderShadow] = new Vector4(0f, 0f, 0f, 0f);

            colors[(int)ImGuiCol.FrameBg] = BgCard;
            colors[(int)ImGuiCol.FrameBgHovered] = BgElevated;
            colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.00f, 0.34f, 0.44f, 0.90f);

            colors[(int)ImGuiCol.TitleBg] = new Vector4(0.05f, 0.07f, 0.11f, 0.98f);
            colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.06f, 0.11f, 0.18f, 0.98f);
            colors[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(0.04f, 0.05f, 0.08f, 0.85f);
            colors[(int)ImGuiCol.MenuBarBg] = BgSidebar;

            colors[(int)ImGuiCol.ScrollbarBg] = new Vector4(0.03f, 0.04f, 0.07f, 0.65f);
            colors[(int)ImGuiCol.ScrollbarGrab] = new Vector4(0.00f, 0.60f, 0.72f, 0.55f);
            colors[(int)ImGuiCol.ScrollbarGrabHovered] = new Vector4(0.00f, 0.85f, 0.95f, 0.85f);
            colors[(int)ImGuiCol.ScrollbarGrabActive] = NeonCyan;

            colors[(int)ImGuiCol.CheckMark] = NeonCyan;
            colors[(int)ImGuiCol.SliderGrab] = NeonCyan;
            colors[(int)ImGuiCol.SliderGrabActive] = NeonMagenta;

            colors[(int)ImGuiCol.Button] = new Vector4(0.08f, 0.14f, 0.23f, 0.95f);
            colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.00f, 0.42f, 0.52f, 0.95f);
            colors[(int)ImGuiCol.ButtonActive] = new Vector4(0.85f, 0.14f, 0.44f, 0.95f);

            colors[(int)ImGuiCol.Header] = new Vector4(0.00f, 0.32f, 0.42f, 0.50f);
            colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.00f, 0.55f, 0.68f, 0.65f);
            colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.00f, 0.78f, 0.90f, 0.80f);

            colors[(int)ImGuiCol.Separator] = new Vector4(0.00f, 0.75f, 0.88f, 0.30f);
            colors[(int)ImGuiCol.SeparatorHovered] = NeonCyan;
            colors[(int)ImGuiCol.SeparatorActive] = NeonMagenta;

            colors[(int)ImGuiCol.ResizeGrip] = new Vector4(0.00f, 0.94f, 1.00f, 0.28f);
            colors[(int)ImGuiCol.ResizeGripHovered] = new Vector4(0.00f, 0.94f, 1.00f, 0.75f);
            colors[(int)ImGuiCol.ResizeGripActive] = NeonMagenta;

            colors[(int)ImGuiCol.Tab] = BgCard;
            colors[(int)ImGuiCol.TabHovered] = new Vector4(0.00f, 0.48f, 0.58f, 0.90f);
            colors[(int)ImGuiCol.TabSelected] = new Vector4(0.00f, 0.36f, 0.48f, 1.00f);
            colors[(int)ImGuiCol.TabSelectedOverline] = NeonCyan;

            colors[(int)ImGuiCol.PlotLines] = NeonCyan;
            colors[(int)ImGuiCol.PlotLinesHovered] = AcidYellow;
            colors[(int)ImGuiCol.PlotHistogram] = NeonMagenta;
            colors[(int)ImGuiCol.PlotHistogramHovered] = AcidYellow;

            colors[(int)ImGuiCol.TableHeaderBg] = new Vector4(0.07f, 0.13f, 0.21f, 1.00f);
            colors[(int)ImGuiCol.TableBorderStrong] = new Vector4(0.00f, 0.65f, 0.78f, 0.45f);
            colors[(int)ImGuiCol.TableBorderLight] = new Vector4(0.00f, 0.50f, 0.65f, 0.20f);
            colors[(int)ImGuiCol.TableRowBg] = new Vector4(0f, 0f, 0f, 0f);
            colors[(int)ImGuiCol.TableRowBgAlt] = new Vector4(0.00f, 0.80f, 1.00f, 0.03f);

            colors[(int)ImGuiCol.TextSelectedBg] = new Vector4(1.00f, 0.16f, 0.52f, 0.40f);
            colors[(int)ImGuiCol.NavCursor] = NeonCyan;
        }

        private static string? FindFirstExisting(string directory, params string[] fileNames)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return null;

            foreach (string name in fileNames)
            {
                string full = Path.Combine(directory, name);
                if (File.Exists(full))
                    return full;
            }

            return null;
        }
    }
}
#endif
