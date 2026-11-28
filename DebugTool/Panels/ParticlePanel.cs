#if DEBUG
#nullable enable

using System;
using System.Numerics;
using Apotheosis.DebugTool.Core;
using Apotheosis.DebugTool.Theme;
using Apotheosis.Runtime.Particles;
using Apotheosis.Runtime.Particles.Core;
using Apotheosis.Runtime.Rendering;
using Apotheosis.Runtime.Rendering.Commands;
using Hexa.NET.ImGui;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis.DebugTool.Panels
{
    /// <summary>ECS 多线程粒子系统诊断遥测面板。</summary>
    [NoJIT]
    internal sealed class ParticlePanel : IDebugPanel
    {
        private double _avgStepMs;
        private double _peakStepMs;

        public string Id => "particles";
        public string DisplayName => "粒子系统";
        public int Order => 12;

        public void OnUpdate(float deltaSeconds)
        {
            if (Main.dedServ || Main.gameMenu)
                return;

            ParticleRuntimeStats stats = ParticleManager.GetStats();
            _avgStepMs = _avgStepMs <= 0.0 ? stats.LastStepMs : _avgStepMs * 0.92 + stats.LastStepMs * 0.08;
            if (stats.LastStepMs > _peakStepMs)
                _peakStepMs = stats.LastStepMs;
        }

        public void OnDraw()
        {
            if (Main.dedServ)
                return;

            ParticleRuntimeStats stats = ParticleManager.GetStats();
            RenderTimingStats renderStats = RenderManager.Commands.GetLastFrameTotalStats();

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
        }
    }
}
#endif
