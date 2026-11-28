#nullable enable

using System;

namespace Apotheosis.Runtime.Particles.Emitters.SpawnRules
{
    /// <summary>恒定速率粒子生成规则（支持乘以挂载发射器独立的 <see cref="ParticleSpawnRuleContext.EmissionRateScale"/> 实时调速）。</summary>
    public sealed class SpawnRateRule : IParticleSpawnRule
    {
        private sealed class RateState
        {
            public double Accumulator;
        }

        /// <summary>基准发射速率（粒子/秒）。</summary>
        public float RateOverTime { get; }

        public SpawnRateRule(float rateOverTime)
        {
            RateOverTime = float.IsFinite(rateOverTime) && rateOverTime > 0f ? rateOverTime : 0f;
        }

        public object CreateState() => new RateState();

        public void ResetState(object? state)
        {
            if (state is RateState rs)
                rs.Accumulator = 0.0;
        }

        public long Evaluate(in ParticleSpawnRuleContext ctx, object? state, ref ParticleRandom rng)
        {
            if (state is not RateState rs || ctx.EmissionCap <= 0 || !(RateOverTime > 0f) || !(ctx.StepDeltaTime > 0f))
                return 0;

            float scale = float.IsFinite(ctx.EmissionRateScale) && ctx.EmissionRateScale >= 0f ? ctx.EmissionRateScale : 1f;
            if (!(scale > 0f))
                return 0;

            // 使用 double 累加并按单步上限安全截断，杜绝 float -> int 溢出成负数导致卡死
            double nextAcc = rs.Accumulator + (double)RateOverTime * scale * ctx.StepDeltaTime;
            if (!double.IsFinite(nextAcc) || nextAcc <= 0.0)
            {
                rs.Accumulator = 0.0;
                return 0;
            }

            if (nextAcc >= ctx.EmissionCap)
            {
                rs.Accumulator = 0.0;
                return ctx.EmissionCap;
            }

            long emitted = (long)Math.Floor(nextAcc);
            rs.Accumulator = nextAcc - emitted;
            return emitted;
        }
    }
}
