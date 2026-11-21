#if DEBUG
#nullable enable

using System;
using System.Numerics;
using Hexa.NET.ImGui;
using Terraria.ModLoader;

namespace Apotheosis.DebugTool.Theme
{
    /// <summary>
    /// 赛博朋克风格自绘组件与 DrawList 辅助方法。
    /// </summary>
    [NoJIT]
    internal static class CyberpunkWidgets
    {
        public static uint ToU32(Vector4 color) => ImGui.ColorConvertFloat4ToU32(color);

        public static uint ToU32(Vector4 color, float alphaOverride) =>
            ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, alphaOverride));

        /// <summary>
        /// 在窗口顶部绘制电光青 → 赛博洋红的霓虹渐变细线。
        /// </summary>
        public static void DrawNeonGradientBar(ImDrawListPtr drawList, Vector2 min, float width, float height)
        {
            uint colLeft = ToU32(CyberpunkTheme.NeonCyan);
            uint colRight = ToU32(CyberpunkTheme.NeonMagenta);

            drawList.AddRectFilledMultiColor(
                min,
                new Vector2(min.X + width, min.Y + height),
                colLeft,
                colRight,
                colRight,
                colLeft);
        }

        /// <summary>
        /// 绘制极低不透明度的终端扫描线质感层（不影响文字可读性）。
        /// </summary>
        public static void DrawScanlines(ImDrawListPtr drawList, Vector2 min, Vector2 max)
        {
            uint lineCol = ToU32(new Vector4(0f, 0f, 0f, 1f), 0.08f);
            for (float y = min.Y; y < max.Y; y += 4f)
                drawList.AddLine(new Vector2(min.X, y), new Vector2(max.X, y), lineCol, 1f);
        }

        /// <summary>
        /// 左侧竖向导航栏的选项按钮：仅显示中文窗口名称，选中时带左侧电光青指示条。
        /// </summary>
        public static bool NavItem(string id, string label, bool isSelected, float width)
        {
            float lineHeight = ImGui.GetTextLineHeight();
            float height = Math.Max(36f, lineHeight + 16f);
            Vector2 pos = ImGui.GetCursorScreenPos();
            Vector2 size = new(width, height);

            bool clicked = ImGui.InvisibleButton($"##nav_{id}", size);
            bool hovered = ImGui.IsItemHovered();

            var dl = ImGui.GetWindowDrawList();
            Vector2 max = pos + size;

            if (isSelected)
            {
                uint bgLeft = ToU32(CyberpunkTheme.NeonCyan, 0.22f);
                uint bgRight = ToU32(CyberpunkTheme.NeonCyan, 0.04f);
                dl.AddRectFilledMultiColor(pos, max, bgLeft, bgRight, bgRight, bgLeft);
                dl.AddRectFilled(pos, new Vector2(pos.X + 3f, max.Y), ToU32(CyberpunkTheme.NeonCyan));
                dl.AddRect(pos, max, ToU32(CyberpunkTheme.NeonCyan, 0.45f), 1.5f);
            }
            else if (hovered)
            {
                dl.AddRectFilled(pos, max, ToU32(CyberpunkTheme.BgElevated, 0.70f), 1.5f);
                dl.AddRectFilled(pos, new Vector2(pos.X + 2f, max.Y), ToU32(CyberpunkTheme.NeonMagenta, 0.85f));
            }
            else
            {
                dl.AddRectFilled(pos, max, ToU32(CyberpunkTheme.BgCard, 0.35f), 1.5f);
            }

            float textY = pos.Y + (height - lineHeight) * 0.5f;
            uint labelColor = isSelected
                ? ToU32(CyberpunkTheme.NeonCyan)
                : ToU32(CyberpunkTheme.TextPrimary, hovered ? 0.96f : 0.84f);

            dl.AddText(new Vector2(pos.X + 12f, textY), labelColor, label);

            return clicked;
        }

        /// <summary>
        /// 右侧功能区的赛博朋克分区标题（`// TITLE` + 渐变分割线）。
        /// </summary>
        public static void SectionHeader(string title, string? rightHint = null)
        {
            ImGui.Spacing();
            Vector2 cursor = ImGui.GetCursorScreenPos();
            float availW = ImGui.GetContentRegionAvail().X;
            var dl = ImGui.GetWindowDrawList();

            ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.NeonCyan);
            ImGui.TextUnformatted("//");
            ImGui.PopStyleColor();
            ImGui.SameLine(0f, 6f);
            ImGui.TextUnformatted(title);

            if (!string.IsNullOrEmpty(rightHint))
            {
                Vector2 hintSize = ImGui.CalcTextSize(rightHint);
                float rightX = cursor.X + availW - hintSize.X;
                if (rightX > ImGui.GetItemRectMax().X + 10f)
                {
                    dl.AddText(new Vector2(rightX, cursor.Y), ToU32(CyberpunkTheme.TextMuted), rightHint);
                }
            }

            float lineY = ImGui.GetCursorScreenPos().Y - 2f;
            dl.AddRectFilledMultiColor(
                new Vector2(cursor.X, lineY),
                new Vector2(cursor.X + availW, lineY + 1.5f),
                ToU32(CyberpunkTheme.NeonCyan, 0.85f),
                ToU32(CyberpunkTheme.NeonMagenta, 0.15f),
                ToU32(CyberpunkTheme.NeonMagenta, 0.15f),
                ToU32(CyberpunkTheme.NeonCyan, 0.85f));

            ImGui.Dummy(new Vector2(0f, 4f));
        }

        /// <summary>
        /// 紧凑型遥测数据卡片（自动适配字体高度）。
        /// </summary>
        public static void MetricCard(string label, string value, Vector4 accentColor, float width)
        {
            float lineH = ImGui.GetTextLineHeight();
            float height = lineH * 2f + 16f;
            Vector2 pos = ImGui.GetCursorScreenPos();
            Vector2 max = new(pos.X + width, pos.Y + height);
            var dl = ImGui.GetWindowDrawList();

            dl.AddRectFilled(pos, max, ToU32(CyberpunkTheme.BgCard, 0.85f), 2f);
            dl.AddRectFilled(pos, new Vector2(pos.X + 3f, max.Y), ToU32(accentColor));
            dl.AddRect(pos, max, ToU32(accentColor, 0.30f), 2f);

            dl.AddText(new Vector2(pos.X + 10f, pos.Y + 6f), ToU32(CyberpunkTheme.TextMuted), label);
            dl.AddText(new Vector2(pos.X + 10f, pos.Y + 8f + lineH), ToU32(accentColor), value);

            ImGui.Dummy(new Vector2(width, height));
        }

        /// <summary>
        /// 紧凑型霓虹状态徽章。
        /// </summary>
        public static void StatusBadge(string text, Vector4 accentColor)
        {
            Vector2 textSize = ImGui.CalcTextSize(text);
            Vector2 pad = new(7f, 2f);
            Vector2 size = textSize + pad * 2f;
            Vector2 pos = ImGui.GetCursorScreenPos();
            Vector2 max = pos + size;
            var dl = ImGui.GetWindowDrawList();

            dl.AddRectFilled(pos, max, ToU32(accentColor, 0.16f), 2f);
            dl.AddRect(pos, max, ToU32(accentColor, 0.70f), 2f);
            dl.AddText(pos + pad, ToU32(accentColor), text);

            ImGui.Dummy(size);
        }

        /// <summary>
        /// 带霓虹填充色的容量进度条（如 NPC / Projectile 活跃占比）。
        /// </summary>
        public static void NeonProgressBar(string label, int current, int max, Vector4 fillColor)
        {
            float ratio = max > 0 ? Math.Clamp((float)current / max, 0f, 1f) : 0f;
            string overlay = $"{current} / {max}";

            ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.TextMuted);
            ImGui.TextUnformatted(label);
            ImGui.PopStyleColor();

            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, fillColor);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, CyberpunkTheme.BgCard);
            ImGui.ProgressBar(ratio, new Vector2(-1f, ImGui.GetTextLineHeight() + 4f), overlay);
            ImGui.PopStyleColor(2);
        }
    }
}
#endif
