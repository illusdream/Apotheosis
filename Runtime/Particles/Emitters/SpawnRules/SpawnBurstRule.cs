#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace Apotheosis.Runtime.Particles.Emitters.SpawnRules
{
    /// <summary>定时多周期爆发生成规则（完全无状态，零每实例状态内存开销）。</summary>
    public sealed class SpawnBurstRule : IParticleSpawnRule
    {
        private const int MaxBurstTriggersPerTick = 1024;

        /// <summary>爆发配置项。</summary>
        public ParticleEmitterBurst Burst { get; }

        public SpawnBurstRule(ParticleEmitterBurst burst) => Burst = burst;

        public SpawnBurstRule(float time, int count) => Burst = new ParticleEmitterBurst(time, count);

        public SpawnBurstRule(float time, int minCount, int maxCount, int cycles = 1, float interval = 0f)
            => Burst = new ParticleEmitterBurst(time, minCount, maxCount, cycles, interval);

        public long Evaluate(in ParticleSpawnRuleContext ctx, object? state, ref ParticleRandom rng)
        {
            ParticleEmitterBurst burst = Burst;
            if (ctx.EmissionCap <= 0 || burst.MaxCount <= 0)
                return 0;

            double prev = ctx.PrevTime;
            double next = ctx.NextTime;
            bool includeStart = ctx.IncludeStart;
            int cycles = burst.Cycles <= 1 || !(burst.Interval > 0f) ? 1 : burst.Cycles;

            if (cycles == 1)
            {
                double t = burst.Time;
                bool inWindow = includeStart ? t >= prev && t <= next : t > prev && t <= next;
                if (!inWindow)
                    return 0;

                long count = SampleBurstCount(in burst, ref rng);
                return count >= ctx.EmissionCap ? ctx.EmissionCap : count;
            }

            // 末次触发提前剪枝 + BitIncrement 跳过 ULP 吸收段 + 有限性校验防强转溢出 + 总迭代硬上限防极端极小间隔忙循环
            double interval = burst.Interval;
            double lastTrigger = burst.Time + (cycles - 1L) * interval;
            if (includeStart ? lastTrigger < prev : lastTrigger <= prev)
                return 0;

            double minTarget = includeStart ? prev : Math.BitIncrement(prev);
            double rawStart = minTarget > burst.Time ? Math.Floor((minTarget - burst.Time) / interval) : 0.0;
            if (!double.IsFinite(rawStart) || rawStart >= cycles)
                return 0;

            long cStart = rawStart > 0.0 ? (long)rawStart : 0L;
            long total = 0;

            for (long c = cStart, iterations = 0; c < cycles && iterations < MaxBurstTriggersPerTick; c++, iterations++)
            {
                double triggerTime = burst.Time + c * interval;
                if (triggerTime > next)
                    break;

                bool afterStart = includeStart ? triggerTime >= prev : triggerTime > prev;
                if (!afterStart)
                    continue;

                total += SampleBurstCount(in burst, ref rng);
                if (total >= ctx.EmissionCap)
                    return ctx.EmissionCap;
            }

            return total;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int SampleBurstCount(in ParticleEmitterBurst burst, ref ParticleRandom rng)
            => burst.MinCount == burst.MaxCount ? burst.MinCount : rng.NextIntInclusive(burst.MinCount, burst.MaxCount);
    }
}
