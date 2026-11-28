#if DEBUG
#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using Apotheosis.DebugTool.Core;
using Apotheosis.DebugTool.Gizmos;
using Apotheosis.DebugTool.Theme;
using Hexa.NET.ImGui;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis.DebugTool.Panels
{
    /// <summary>
    /// 概览监控面板：实时帧率数值、GC 堆内存、场景活跃实体负载、FNA 视口与 Apotheosis 运行环境诊断。
    /// </summary>
    [NoJIT]
    internal sealed class OverviewPanel : IDebugPanel
    {
        private static readonly List<GizmoChannelSnapshot> _channelSnapshotScratch = new();

        public string Id => "overview";
        public string DisplayName => "概览监控";
        public int Order => 0;

        public void OnDraw()
        {
            var io = ImGui.GetIO();
            float fps = io.Framerate;
            float frameMs = fps > 0.01f ? 1000f / fps : 0f;
            float gcMb = GC.GetTotalMemory(false) / (1024f * 1024f);
            int drawCalls = DebugToolRuntime.Renderer?.LastDrawCalls ?? 0;

            CyberpunkWidgets.SectionHeader("TELEMETRY_STREAM", "REALTIME");

            float availW = ImGui.GetContentRegionAvail().X;
            float cardW = Math.Max(110f, (availW - 8f) * 0.5f);

            Vector4 fpsColor = fps >= 55f
                ? CyberpunkTheme.NeonGreen
                : fps >= 30f
                    ? CyberpunkTheme.AcidYellow
                    : CyberpunkTheme.NeonMagenta;

            CyberpunkWidgets.MetricCard("FRAME_RATE", $"{fps:0.0} FPS", fpsColor, cardW);
            ImGui.SameLine(0f, 8f);
            CyberpunkWidgets.MetricCard("FRAME_TIME", $"{frameMs:0.00} ms", CyberpunkTheme.NeonCyan, cardW);

            CyberpunkWidgets.MetricCard("GC_MANAGED", $"{gcMb:0.0} MB", CyberpunkTheme.AcidYellow, cardW);
            ImGui.SameLine(0f, 8f);
            CyberpunkWidgets.MetricCard("IMGUI_CALLS", $"{drawCalls} draws", CyberpunkTheme.NeonMagenta, cardW);

            // 场景活跃实体负载（便于排查模组 Boss / 弹幕 / 粒子峰值开销）
            CyberpunkWidgets.SectionHeader("ENTITY_LOAD", Main.gameMenu ? "MENU" : "IN_WORLD");

            if (Main.gameMenu)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.TextMuted);
                ImGui.TextUnformatted("当前位于主菜单，进入世界后将实时显示 NPC / 弹幕 / 粒子 / 掉落物负载。");
                ImGui.PopStyleColor();
            }
            else
            {
                int activeNpc = CountActiveNpcs();
                int activeProj = CountActiveProjectiles();
                int activeDust = CountActiveDust();
                int activeItems = CountActiveItems();

                CyberpunkWidgets.NeonProgressBar("NPC_ACTIVE", activeNpc, Main.maxNPCs, CyberpunkTheme.NeonCyan);
                CyberpunkWidgets.NeonProgressBar("PROJECTILE_ACTIVE", activeProj, Main.maxProjectiles, CyberpunkTheme.NeonMagenta);
                CyberpunkWidgets.NeonProgressBar("DUST_ACTIVE", activeDust, Main.maxDustToDraw, CyberpunkTheme.AcidYellow);
                CyberpunkWidgets.NeonProgressBar("WORLD_ITEM_ACTIVE", activeItems, Main.maxItems, CyberpunkTheme.NeonGreen);
            }

            // 世界空间 Gizmos 调试绘制控制区
            DrawWorldGizmosSection();

            // 运行环境与 FNA 渲染诊断
            CyberpunkWidgets.SectionHeader("RUNTIME_DIAGNOSTICS", "FNA");

            if (ImGui.BeginTable("##RuntimeDiag", 2, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthFixed, 165f);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

                DrawDiagRow("Mod", $"Apotheosis v{ModContent.GetInstance<Apotheosis>().Version} (Development)");
                DrawDiagRow(".NET Runtime", Environment.Version.ToString());

                var gd = Main.graphics?.GraphicsDevice;
                if (gd is not null && !gd.IsDisposed)
                {
                    var pp = gd.PresentationParameters;
                    DrawDiagRow("BackBuffer", $"{pp.BackBufferWidth} x {pp.BackBufferHeight}");
                }

                if (Main.instance is not null)
                {
                    var bounds = Main.instance.Window.ClientBounds;
                    DrawDiagRow("Window Client", $"{bounds.Width} x {bounds.Height}");
                }

                DrawDiagRow("Terraria UIScale", $"{Main.UIScale:0.00}x  |  Zoom: {Main.GameViewMatrix.Zoom.X:0.00}x");
                DrawDiagRow("ImGui Font", CyberpunkTheme.LoadedFontSummary);

                var renderer = DebugToolRuntime.Renderer;
                if (renderer is not null)
                    DrawDiagRow("ImGui Mesh", $"{renderer.LastVertexCount} vtx / {renderer.LastIndexCount} idx / {renderer.BoundTextureCount} tex");

                ImGui.EndTable();
            }
        }

        private static void DrawDiagRow(string key, string value)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.TextMuted);
            ImGui.TextUnformatted(key);
            ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(1);
            ImGui.TextWrapped(value);
        }

        private static void DrawWorldGizmosSection()
        {
            GizmoSettings settings = DebugToolRuntime.GizmoSettings;
            GizmoCommandBuffer? buffer = DebugToolRuntime.Gizmos;

            bool overflow = buffer is { LastFrameOverflowed: true } or { TimedOverflowed: true };
            string rightHint = overflow
                ? "OVERFLOW"
                : settings.Enabled
                    ? (DebugToolRuntime.IsGizmoOverlayActive ? "ACTIVE" : "STANDBY")
                    : "OFF";

            CyberpunkWidgets.SectionHeader("WORLD_GIZMOS", rightHint);

            ImGui.Checkbox("显示世界 Gizmos (收起面板保持可见)", ref settings.Enabled);
            ImGui.SameLine(0f, 10f);

            if (overflow)
                CyberpunkWidgets.StatusBadge("OVERFLOW", CyberpunkTheme.NeonMagenta);
            else if (settings.Enabled && DebugToolRuntime.IsGizmoOverlayActive)
                CyberpunkWidgets.StatusBadge("ACTIVE", CyberpunkTheme.NeonCyan);
            else if (settings.Enabled)
                CyberpunkWidgets.StatusBadge("STANDBY", CyberpunkTheme.AcidYellow);
            else
                CyberpunkWidgets.StatusBadge("OFF", CyberpunkTheme.TextMuted);

            if (!settings.Enabled || buffer is null)
                return;

            // 实时绘制统计
            ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.TextMuted);
            ImGui.TextUnformatted(
                $"本帧绘制: {buffer.LastRenderedCount}  |  剔除: {buffer.LastCulledCount}  |  延时: {buffer.TimedCount}  |  Provider: {buffer.ProviderCount}");
            ImGui.PopStyleColor();

            float btnH = ImGui.GetTextLineHeight() + 8f;
            if (ImGui.Button("清空延时图元", new Vector2(118f, btnH)))
                buffer.ClearTimed();

            // 动态频道过滤
            buffer.FillChannelSnapshots(_channelSnapshotScratch);
            List<GizmoChannelSnapshot> channels = _channelSnapshotScratch;
            if (channels.Count > 0)
            {
                ImGui.Spacing();
                ImGui.PushStyleColor(ImGuiCol.Text, CyberpunkTheme.TextMuted);
                ImGui.TextUnformatted("频道过滤:");
                ImGui.PopStyleColor();

                ImGui.SameLine(0f, 8f);
                if (ImGui.SmallButton("全选##gizmo_all"))
                    settings.SetAllChannels(true);

                ImGui.SameLine(0f, 6f);
                if (ImGui.SmallButton("全隐##gizmo_none"))
                    settings.SetAllChannels(false);

                float availW = ImGui.GetContentRegionAvail().X;
                float cursorX = 0f;
                for (int i = 0; i < channels.Count; i++)
                {
                    GizmoChannelSnapshot ch = channels[i];
                    int total = ch.LastFrameCount + ch.TimedCount;
                    string label = $"{ch.Name} ({total})##gizmo_ch_{ch.Name}";
                    float estWidth = ImGui.CalcTextSize($"{ch.Name} ({total})").X + 34f;

                    if (i > 0 && cursorX + estWidth <= availW)
                    {
                        ImGui.SameLine(0f, 10f);
                    }
                    else
                    {
                        cursorX = 0f;
                    }

                    bool enabled = settings.IsChannelEnabled(ch.Name);
                    if (ImGui.Checkbox(label, ref enabled))
                        settings.SetChannelEnabled(ch.Name, enabled);

                    cursorX += estWidth + 10f;
                }
            }

            // 可折叠外观与上限配置（默认折叠保持面板精简）
            ImGui.Spacing();
            if (ImGui.TreeNode("Gizmos 外观与上限设置##gizmo_adv"))
            {
                ImGui.SetNextItemWidth(-1f);
                ImGui.SliderFloat("##GizmoOpacity", ref settings.GlobalOpacity, 0.10f, 1.00f, "全局不透明度: %.2f");

                ImGui.SetNextItemWidth(-1f);
                ImGui.SliderFloat("##GizmoThickness", ref settings.ThicknessScale, 0.50f, 3.00f, "全局线宽倍率: %.2fx");

                ImGui.SetNextItemWidth(-1f);
                ImGui.SliderInt("##GizmoMaxCmd", ref settings.MaxCommandsPerFrame, 256, 16384, "单帧图元上限: %d");

                ImGui.Checkbox("显示文本标签", ref settings.ShowLabels);
                ImGui.SameLine(0f, 12f);
                ImGui.Checkbox("延时图元末尾淡出", ref settings.FadeTimedGizmos);

                ImGui.TreePop();
            }
        }

        private static int CountActiveNpcs()
        {
            int count = 0;
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                if (Main.npc[i]?.active == true)
                    count++;
            }

            return count;
        }

        private static int CountActiveProjectiles()
        {
            int count = 0;
            for (int i = 0; i < Main.maxProjectiles; i++)
            {
                if (Main.projectile[i]?.active == true)
                    count++;
            }

            return count;
        }

        private static int CountActiveDust()
        {
            int count = 0;
            int limit = Math.Min(Main.dust.Length, Main.maxDustToDraw);
            for (int i = 0; i < limit; i++)
            {
                if (Main.dust[i]?.active == true)
                    count++;
            }

            return count;
        }

        private static int CountActiveItems()
        {
            int count = 0;
            for (int i = 0; i < Main.maxItems; i++)
            {
                if (Main.item[i]?.active == true)
                    count++;
            }

            return count;
        }
    }
}
#endif
