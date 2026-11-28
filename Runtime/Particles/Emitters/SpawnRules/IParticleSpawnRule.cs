#nullable enable

using Apotheosis.Runtime.Particles.Core;

namespace Apotheosis.Runtime.Particles.Emitters.SpawnRules
{
    /// <summary>粒子生成规则单步求值上下文（栈上传递，零堆分配）。</summary>
    public readonly ref struct ParticleSpawnRuleContext
    {
        /// <summary>本次时间窗起点（秒，相对当前周期起点）。</summary>
        public readonly float PrevTime;

        /// <summary>本次时间窗终点（秒，相对当前周期起点）。</summary>
        public readonly float NextTime;

        /// <summary>本次时间窗有效时长（秒）。</summary>
        public readonly float StepDeltaTime;

        /// <summary>是否包含起点边界（首帧或循环回绕首步为 <c>true</c>）。</summary>
        public readonly bool IncludeStart;

        /// <summary>当前已挂载发射器的独立发射速率倍率。</summary>
        public readonly float EmissionRateScale;

        /// <summary>本步允许生成的最大粒子数上限。</summary>
        public readonly long EmissionCap;

        /// <summary>当前宿主原点世界变换快照（供距离发射等空间规则使用）。</summary>
        public readonly ParticleOriginSnapshot Origin;

        public ParticleSpawnRuleContext(float prevTime, float nextTime, float stepDeltaTime, bool includeStart, float emissionRateScale, long emissionCap, scoped in ParticleOriginSnapshot origin)
        {
            PrevTime = prevTime;
            NextTime = nextTime;
            StepDeltaTime = stepDeltaTime;
            IncludeStart = includeStart;
            EmissionRateScale = emissionRateScale;
            EmissionCap = emissionCap;
            Origin = origin;
        }
    }

    /// <summary>可插拔粒子生成数量调度规则接口（蓝图规则本身不可变，每实例游标状态由 <see cref="CreateState"/> 独立持有）。</summary>
    public interface IParticleSpawnRule
    {
        /// <summary>为单个挂载播放态创建独立状态对象（无状态规则返回 <c>null</c>）。</summary>
        object? CreateState() => null;

        /// <summary>重置单实例状态对象。</summary>
        void ResetState(object? state) { }

        /// <summary>计算当前时间窗内本规则应生成的粒子数量（保证返回非负值且不超过 <see cref="ParticleSpawnRuleContext.EmissionCap"/>）。</summary>
        long Evaluate(in ParticleSpawnRuleContext ctx, object? state, ref ParticleRandom rng);
    }
}
