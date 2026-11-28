#if DEBUG
#nullable enable

using System;
using System.Numerics;
using Apotheosis.DebugTool.Core;
using Apotheosis.DebugTool.Theme;
using Apotheosis.Runtime.Rendering;
using Apotheosis.Runtime.Rendering.Commands;
using Apotheosis.Runtime.Rendering.Targets;
using Hexa.NET.ImGui;
using Terraria.ModLoader;

namespace Apotheosis.DebugTool.Panels
{
    /// <summary>渲染管线与 RT 池诊断遥测面板。</summary>
    [NoJIT]
    internal sealed class RenderPipelinePanel : IDebugPanel
    {
        private int _lastManualEvictCount;

        public string Id => "render_pipeline";
        public string DisplayName => "渲染管线";
        public int Order => 10;

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
        }
    }
}
#endif
