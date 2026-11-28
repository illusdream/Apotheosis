#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace Apotheosis.Runtime.Particles.Emitters
{
    /// <summary>单精度闭区间数值范围（支持由常量 <see cref="float"/> 隐式构造）。</summary>
    public readonly struct FloatRange
    {
        /// <summary>最小值。</summary>
        public readonly float Min;

        /// <summary>最大值。</summary>
        public readonly float Max;

        public FloatRange(float value)
        {
            Min = value;
            Max = value;
        }

        public FloatRange(float min, float max)
        {
            if (min <= max)
            {
                Min = min;
                Max = max;
            }
            else
            {
                Min = max;
                Max = min;
            }
        }

        public static implicit operator FloatRange(float value) => new(value);

        /// <summary>在区间内均匀采样一个随机数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Sample(ref ParticleRandom rng) => Min == Max ? Min : rng.NextFloat(Min, Max);
    }

    /// <summary>定时爆发配置项（在指定时刻一次性或按周期重复爆发指定数量范围的粒子）。</summary>
    public readonly struct ParticleEmitterBurst
    {
        /// <summary>首次触发时刻（秒，相对单次发射周期起点）。</summary>
        public readonly float Time;

        /// <summary>单次爆发最小粒子数。</summary>
        public readonly int MinCount;

        /// <summary>单次爆发最大粒子数。</summary>
        public readonly int MaxCount;

        /// <summary>本爆发项在一个周期内重复触发的次数（最小为 1）。</summary>
        public readonly int Cycles;

        /// <summary>重复触发的时间间隔（秒，仅 <see cref="Cycles"/> &gt; 1 时生效）。</summary>
        public readonly float Interval;

        public ParticleEmitterBurst(float time, int count) : this(time, count, count, 1, 0f) { }

        public ParticleEmitterBurst(float time, int minCount, int maxCount, int cycles = 1, float interval = 0f)
        {
            Time = float.IsFinite(time) && time > 0f ? time : 0f;
            int lo = Math.Max(0, Math.Min(minCount, maxCount));
            int hi = Math.Max(0, Math.Max(minCount, maxCount));
            MinCount = lo;
            MaxCount = hi;
            Interval = float.IsFinite(interval) && interval > 0f ? interval : 0f;
            Cycles = Interval > 0f && cycles > 1 ? cycles : 1;
        }
    }
}
