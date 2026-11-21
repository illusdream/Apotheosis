#if DEBUG
#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using Apotheosis.DebugTool.Core;
using Apotheosis.DebugTool.Theme;
using Hexa.NET.ImGui;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis.DebugTool.Layout
{
    /// <summary>
    /// 赛博朋克风格竖向调试终端主壳层：
    /// <para>霓虹顶栏 + 左侧竖向窗口选择栏 + 右侧实际功能区 + 底部状态条。</para>
    /// </summary>
    [NoJIT]
    internal sealed class DebugShellWindow
    {
        private const float DefaultWidth = 640f;
        private const float DefaultHeight = 800f;
        private const float MinWidth = 480f;
        private const float MinHeight = 520f;
        private const float SidebarWidth = 160f;
        private const float HeaderHeight = 40f;
        private const float FooterHeight = 28f;

        private string? _selectedPanelId;
        private bool _draggingHeader;
        private Vector2 _dragOffset;

        public void Draw(
            DebugPanelRegistry registry,
            DebugThemeSettings settings,
            DebugInputHandler input,
            ref bool isOpen)
        {
            if (!isOpen)
                return;

            var io = ImGui.GetIO();
            float maxW = Math.Max(MinWidth, io.DisplaySize.X - 32f);
            float maxH = Math.Max(MinHeight, io.DisplaySize.Y - 32f);

            float initW = Math.Min(DefaultWidth, maxW);
            float initH = Math.Min(DefaultHeight, maxH);

            if (settings.RequestResetLayout)
            {
                ImGui.SetNextWindowPos(new Vector2(28f, 28f), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(initW, initH), ImGuiCond.Always);
                settings.RequestResetLayout = false;
            }
            else
            {
                ImGui.SetNextWindowPos(new Vector2(28f, 28f), ImGuiCond.FirstUseEver);
                ImGui.SetNextWindowSize(new Vector2(initW, initH), ImGuiCond.FirstUseEver);
            }

            ImGui.SetNextWindowSizeConstraints(new Vector2(MinWidth, MinHeight), new Vector2(maxW, maxH));

            // 外壳背景置透明，由顶栏/左侧栏/右侧区/底栏统一按 WindowOpacity 绘制，避免双重叠色导致透明度失效
            ImGui.SetNextWindowBgAlpha(0f);

            ImGuiWindowFlags flags =
                ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;

            if (settings.LockWindowPosition)
                flags |= ImGuiWindowFlags.NoMove;

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            bool begun = ImGui.Begin("ApotheosisDebugShell##Main", ref isOpen, flags);
            ImGui.PopStyleVar();

            if (!begun)
            {
                ImGui.End();
                return;
            }

            float alpha = Math.Clamp(settings.WindowOpacity, 0.25f, 1.0f);
            Vector2 winPos = ImGui.GetWindowPos();
            Vector2 winSize = ImGui.GetWindowSize();
            var winDl = ImGui.GetWindowDrawList();

            // 1. 顶部赛博朋克标题栏（支持拖拽移动窗口）
            DrawHeaderBar(winPos, winSize, settings, alpha, ref isOpen);

            // 2. 主体区域：左侧竖向导航栏 + 右侧功能工作区
            float bodyHeight = Math.Max(140f, winSize.Y - HeaderHeight - FooterHeight);
            IDebugPanel? activePanel = ResolveActivePanel(registry);

            ImGui.SetCursorScreenPos(new Vector2(winPos.X, winPos.Y + HeaderHeight));
            DrawLeftNavRail(registry, activePanel, alpha, bodyHeight);

            // 竖向霓虹分割线
            float splitX = winPos.X + SidebarWidth;
            winDl.AddLine(
                new Vector2(splitX, winPos.Y + HeaderHeight),
                new Vector2(splitX, winPos.Y + HeaderHeight + bodyHeight),
                CyberpunkWidgets.ToU32(CyberpunkTheme.NeonCyan, 0.35f),
                1f);

            ImGui.SetCursorScreenPos(new Vector2(splitX + 1f, winPos.Y + HeaderHeight));
            DrawRightWorkspace(activePanel, winSize.X - SidebarWidth - 1f, bodyHeight, alpha);

            // 3. 底部状态条
            ImGui.SetCursorScreenPos(new Vector2(winPos.X, winPos.Y + HeaderHeight + bodyHeight));
            DrawFooterBar(winSize.X, input, alpha);

            // 扫描线叠层与顶部 3px 霓虹细线
            if (settings.EnableScanlines)
                CyberpunkWidgets.DrawScanlines(winDl, winPos, winPos + winSize);

            CyberpunkWidgets.DrawNeonGradientBar(winDl, winPos, winSize.X, 3f);

            ImGui.End();

            // 可选辅助诊断窗口（在 面板设置 中勾选开启）
            if (settings.ShowImGuiDemo)
                ImGui.ShowDemoWindow(ref settings.ShowImGuiDemo);

            if (settings.ShowImGuiMetrics)
                ImGui.ShowMetricsWindow(ref settings.ShowImGuiMetrics);
        }

        private void DrawHeaderBar(Vector2 winPos, Vector2 winSize, DebugThemeSettings settings, float alpha, ref bool isOpen)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 headerMin = new(winPos.X, winPos.Y + 3f);
            Vector2 headerMax = new(winPos.X + winSize.X, winPos.Y + HeaderHeight);

            dl.AddRectFilled(headerMin, headerMax, CyberpunkWidgets.ToU32(new Vector4(0.055f, 0.082f, 0.135f, alpha)));
            dl.AddLine(
                new Vector2(headerMin.X, headerMax.Y),
                new Vector2(headerMax.X, headerMax.Y),
                CyberpunkWidgets.ToU32(CyberpunkTheme.NeonCyan, 0.40f),
                1f);

            const float closeBtnW = 30f;
            float rightControlsW = closeBtnW + 14f;

            // 拖拽热区
            ImGui.SetCursorScreenPos(headerMin);
            ImGui.InvisibleButton("##header_drag", new Vector2(Math.Max(60f, winSize.X - rightControlsW), HeaderHeight - 3f));

            if (!settings.LockWindowPosition)
            {
                if (ImGui.IsItemActive() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    _draggingHeader = true;
                    _dragOffset = ImGui.GetMousePos() - winPos;
                }

                if (_draggingHeader)
                {
                    if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    {
                        Vector2 target = ImGui.GetMousePos() - _dragOffset;
                        var display = ImGui.GetIO().DisplaySize;
                        target.X = Math.Clamp(target.X, -winSize.X + 80f, display.X - 80f);
                        target.Y = Math.Clamp(target.Y, 0f, display.Y - HeaderHeight);
                        ImGui.SetWindowPos(target);
                    }
                    else
                    {
                        _draggingHeader = false;
                    }
                }
            }
            else
            {
                _draggingHeader = false;
            }

            // 标题文字与实时 FPS（垂直居中）
            float lineH = ImGui.GetTextLineHeight();
            float textY = headerMin.Y + (HeaderHeight - 3f - lineH) * 0.5f;
            dl.AddText(new Vector2(winPos.X + 12f, textY), CyberpunkWidgets.ToU32(CyberpunkTheme.NeonCyan), "APOTHEOSIS");
            float offset = ImGui.CalcTextSize("APOTHEOSIS").X;
            dl.AddText(new Vector2(winPos.X + 16f + offset, textY), CyberpunkWidgets.ToU32(CyberpunkTheme.TextMuted), "// DEV_CONSOLE");

            float fps = ImGui.GetIO().Framerate;
            string fpsTag = $"{fps:0} FPS";
            Vector2 fpsSize = ImGui.CalcTextSize(fpsTag);
            float fpsX = headerMax.X - rightControlsW - fpsSize.X - 12f;
            if (fpsX > winPos.X + 260f)
            {
                Vector4 fpsCol = fps >= 55f ? CyberpunkTheme.NeonGreen : fps >= 30f ? CyberpunkTheme.AcidYellow : CyberpunkTheme.NeonMagenta;
                dl.AddText(new Vector2(fpsX, textY), CyberpunkWidgets.ToU32(fpsCol), fpsTag);
            }

            // 关闭按钮
            float btnH = Math.Min(HeaderHeight - 10f, Math.Max(24f, lineH + 4f));
            float btnY = headerMin.Y + (HeaderHeight - 3f - btnH) * 0.5f;
            ImGui.SetCursorScreenPos(new Vector2(headerMax.X - closeBtnW - 8f, btnY));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, CyberpunkTheme.NeonMagenta);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, CyberpunkTheme.NeonMagenta);
            if (ImGui.Button("X", new Vector2(closeBtnW, btnH)))
                isOpen = false;
            ImGui.PopStyleColor(2);
        }

        private void DrawLeftNavRail(
            DebugPanelRegistry registry,
            IDebugPanel? activePanel,
            float alpha,
            float height)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 10f));
            ImGui.PushStyleColor(
                ImGuiCol.ChildBg,
                new Vector4(CyberpunkTheme.BgSidebar.X, CyberpunkTheme.BgSidebar.Y, CyberpunkTheme.BgSidebar.Z, alpha));

            if (ImGui.BeginChild("##LeftNavRail", new Vector2(SidebarWidth, height), ImGuiChildFlags.None))
            {
                float itemWidth = Math.Max(80f, ImGui.GetContentRegionAvail().X);

                foreach (IDebugPanel panel in registry.Panels)
                {
                    bool isSelected = activePanel is not null &&
                                      string.Equals(panel.Id, activePanel.Id, StringComparison.Ordinal);

                    if (CyberpunkWidgets.NavItem(panel.Id, panel.DisplayName, isSelected, itemWidth))
                        _selectedPanelId = panel.Id;

                    ImGui.Dummy(new Vector2(0f, 4f));
                }
            }

            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar();
        }

        private void DrawRightWorkspace(IDebugPanel? activePanel, float width, float height, float alpha)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f));
            ImGui.PushStyleColor(
                ImGuiCol.ChildBg,
                new Vector4(CyberpunkTheme.BgObsidian.X, CyberpunkTheme.BgObsidian.Y, CyberpunkTheme.BgObsidian.Z, alpha));

            if (ImGui.BeginChild("##RightWorkspace", new Vector2(Math.Max(160f, width), height), ImGuiChildFlags.None))
            {
                if (activePanel is null)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.TextMuted);
                    ImGui.TextUnformatted("未找到已注册的调试面板模块。");
                    ImGui.PopStyleColor();
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.NeonCyan);
                    ImGui.TextUnformatted(activePanel.DisplayName);
                    ImGui.PopStyleColor();

                    ImGui.Separator();
                    ImGui.Spacing();

                    try
                    {
                        activePanel.OnDraw();
                    }
                    catch (Exception ex)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.NeonMagenta);
                        ImGui.TextUnformatted($"[PANEL_FAULT] {activePanel.DisplayName} 绘制异常：");
                        ImGui.PopStyleColor();
                        ImGui.TextWrapped(ex.Message);
                        if (ImGui.CollapsingHeader("StackTrace"))
                            ImGui.TextWrapped(ex.ToString());
                    }
                }
            }

            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar();
        }

        private static void DrawFooterBar(float width, DebugInputHandler input, float alpha)
        {
            Vector2 pos = ImGui.GetCursorScreenPos();
            Vector2 max = new(pos.X + width, pos.Y + FooterHeight);
            var dl = ImGui.GetWindowDrawList();

            dl.AddRectFilled(pos, max, CyberpunkWidgets.ToU32(new Vector4(0.035f, 0.048f, 0.080f, alpha)));
            dl.AddLine(pos, new Vector2(max.X, pos.Y), CyberpunkWidgets.ToU32(CyberpunkTheme.NeonCyan, 0.30f), 1f);

            float textY = pos.Y + (FooterHeight - ImGui.GetTextLineHeight()) * 0.5f;
            string sceneTag = Main.gameMenu ? "MENU" : "IN_WORLD";
            string leftText = $"DEV // FNA :: {sceneTag}";
            dl.AddText(new Vector2(pos.X + 10f, textY), CyberpunkWidgets.ToU32(CyberpunkTheme.TextMuted), leftText);

            string rightText = input.WantCaptureKeyboard
                ? "CAPTURE: KB+MOUSE | CTRL+`"
                : input.WantCaptureMouse
                    ? "CAPTURE: MOUSE | CTRL+`"
                    : "CTRL+` TOGGLE";

            Vector2 rSize = ImGui.CalcTextSize(rightText);
            uint rCol = input.WantCaptureKeyboard
                ? CyberpunkWidgets.ToU32(CyberpunkTheme.NeonMagenta)
                : input.WantCaptureMouse
                    ? CyberpunkWidgets.ToU32(CyberpunkTheme.NeonCyan)
                    : CyberpunkWidgets.ToU32(CyberpunkTheme.TextMuted);

            dl.AddText(new Vector2(max.X - rSize.X - 10f, textY), rCol, rightText);
            ImGui.Dummy(new Vector2(width, FooterHeight));
        }

        private IDebugPanel? ResolveActivePanel(DebugPanelRegistry registry)
        {
            IReadOnlyList<IDebugPanel> panels = registry.Panels;
            if (panels.Count == 0)
                return null;

            if (!string.IsNullOrEmpty(_selectedPanelId))
            {
                for (int i = 0; i < panels.Count; i++)
                {
                    if (string.Equals(panels[i].Id, _selectedPanelId, StringComparison.Ordinal))
                        return panels[i];
                }
            }

            _selectedPanelId = panels[0].Id;
            return panels[0];
        }
    }
}
#endif
