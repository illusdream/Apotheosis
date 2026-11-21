#if DEBUG
#nullable enable

using System.Numerics;
using Apotheosis.DebugTool.Core;
using Apotheosis.DebugTool.Theme;
using Hexa.NET.ImGui;
using Terraria.ModLoader;

namespace Apotheosis.DebugTool.Panels
{
    /// <summary>
    /// 面板设置窗口：调节外观透明度、字体缩放、布局锁定/重置、Dear ImGui 内置诊断工具及键鼠隔离状态监测。
    /// </summary>
    [NoJIT]
    internal sealed class SettingsPanel : IDebugPanel
    {
        public string Id => "settings";
        public string DisplayName => "面板设置";
        public int Order => 900;

        public void OnDraw()
        {
            DebugThemeSettings settings = DebugToolRuntime.Settings;

            CyberpunkWidgets.SectionHeader("CYBERPUNK_APPEARANCE", "STYLE");

            ImGui.SetNextItemWidth(-1f);
            ImGui.SliderFloat("##Opacity", ref settings.WindowOpacity, 0.25f, 1.00f, "窗口不透明度: %.2f");

            ImGui.SetNextItemWidth(-1f);
            ImGui.SliderFloat("##FontScale", ref settings.FontScale, 0.75f, 1.50f, "全局字体缩放: %.2fx");

            ImGui.Spacing();
            ImGui.Checkbox("启用终端扫描线质感 (Scanlines)", ref settings.EnableScanlines);
            ImGui.Checkbox("锁定主窗口位置 (禁止拖动)", ref settings.LockWindowPosition);

            ImGui.Spacing();
            float btnH = ImGui.GetTextLineHeight() + 12f;
            if (ImGui.Button("重置面板尺寸与位置", new Vector2(210f, btnH)))
                settings.RequestResetLayout = true;

            ImGui.SameLine(0f, 8f);
            if (ImGui.Button("恢复默认外观", new Vector2(150f, btnH)))
            {
                settings.WindowOpacity = 0.92f;
                settings.FontScale = 1.0f;
                settings.EnableScanlines = true;
                settings.LockWindowPosition = false;
            }

            // Dear ImGui 内置开发辅助窗口
            CyberpunkWidgets.SectionHeader("IMGUI_DEV_TOOLS", "DEBUG");

            ImGui.Checkbox("打开 Dear ImGui Demo 窗口 (控件参考)", ref settings.ShowImGuiDemo);
            ImGui.Checkbox("打开 ImGui Metrics / Debugger 窗口", ref settings.ShowImGuiMetrics);

            // 输入隔离状态诊断
            CyberpunkWidgets.SectionHeader("INPUT_ISOLATION", "STATUS");

            var input = DebugToolRuntime.Input;
            if (input is not null)
            {
                DrawBoolBadgeRow("WantCaptureMouse", input.WantCaptureMouse, "鼠标已隔离，显示系统光标");
                DrawBoolBadgeRow("WantCaptureKeyboard", input.WantCaptureKeyboard, "键盘已隔离，打字不触发快捷键");
                DrawBoolBadgeRow("WantTextInput", input.WantTextInput, "FNA TextInputEXT 文本输入激活");

                ImGui.Spacing();
                ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.TextMuted);
                ImGui.TextUnformatted($"BackBuffer 鼠标坐标: ({input.LastMousePos.X:0}, {input.LastMousePos.Y:0})");
                ImGui.TextUnformatted("呼出/隐藏快捷键: Ctrl + ` (反引号)");
                ImGui.PopStyleColor();
            }
        }

        private static void DrawBoolBadgeRow(string label, bool active, string desc)
        {
            CyberpunkWidgets.StatusBadge(active ? "ACTIVE" : "IDLE", active ? CyberpunkTheme.NeonCyan : CyberpunkTheme.TextMuted);
            ImGui.SameLine(0f, 8f);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);
            ImGui.SameLine(0f, 8f);
            ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.TextMuted);
            ImGui.TextUnformatted($"- {desc}");
            ImGui.PopStyleColor();
        }
    }
}
#endif
