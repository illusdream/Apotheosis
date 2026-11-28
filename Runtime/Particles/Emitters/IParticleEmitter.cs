#nullable enable

using System;
using Apotheosis.Runtime.Particles.Core;
using Apotheosis.Runtime.Particles.Fragments;

namespace Apotheosis.Runtime.Particles.Emitters
{
    /// <summary>粒子发射上下文（栈上传递、仅在工作线程构造），为发射器与模块提供模板批量分配、空间坐标与发射速率倍率信息。</summary>
    public readonly ref struct ParticleEmitContext
    {
        private readonly ParticleStoreSlot _slot;

        /// <summary>目标粒子存储是否处于局部空间模式。</summary>
        public readonly bool IsLocalSpace;

        /// <summary>本次发射关联的世界原点快照（局部空间下为宿主原点；世界空间或全局发射下为发射基准变换）。</summary>
        public readonly ParticleOriginSnapshot Origin;

        /// <summary>当前已挂载发射器的独立发射速率倍率（单次爆发时固定为 <c>1.0</c>）。</summary>
        public readonly float EmissionRateScale;

        /// <summary>目标存储当前已存活的粒子实体数量。</summary>
        public int CurrentParticleCount => _slot.EntityCount;

        internal ParticleEmitContext(ParticleStoreSlot slot, bool isLocalSpace, scoped in ParticleOriginSnapshot origin, float emissionRateScale = 1f)
        {
            _slot = slot;
            IsLocalSpace = isLocalSpace;
            Origin = origin;
            EmissionRateScale = float.IsFinite(emissionRateScale) && emissionRateScale >= 0f ? emissionRateScale : 1f;
        }

        /// <summary>构造指向指定小型独立粒子子系统的发射上下文。</summary>
        internal static ParticleEmitContext ForInstance(ParticleInstance instance, float emissionRateScale = 1f)
        {
            ParticleStoreSlot slot = instance.StoreSlot ?? throw new InvalidOperationException("目标粒子子系统尚未绑定底层存储槽。");
            ParticleOriginSnapshot origin = instance.OriginSnapshot;
            return new ParticleEmitContext(slot, instance.IsLocalSpace, in origin, emissionRateScale);
        }

        /// <summary>按已烘焙模板在目标存储中一次性连续分配 <paramref name="count"/> 个粒子，返回原始组件切片供模块单遍初始化。</summary>
        public ParticleSpawnBatch Allocate(ParticleTemplate template, int count)
        {
            ArgumentNullException.ThrowIfNull(template);
            return template.AllocateBatch(_slot.Store, count);
        }
    }

    /// <summary>粒子批次自定义初始化委托（在粒子工作线程执行，严禁访问非线程安全的 tML 主线程对象）。</summary>
    public delegate void ParticleBatchInitializer(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng);

    /// <summary>底层粒子发射器运行态接口（所有方法均仅在粒子工作线程调用；日常业务推荐使用不可变蓝图 <see cref="ParticleEmitter"/>）。</summary>
    public interface IParticleEmitter
    {
        /// <summary>持续发射器是否已完成全部发射周期（单次爆发模式下无须读取）。</summary>
        bool IsCompleted { get; }

        /// <summary>工作线程按有效时间步推进持续发射与定时爆发逻辑。</summary>
        void Tick(in ParticleEmitContext ctx, float deltaTime, ref ParticleRandom rng);

        /// <summary>立即按当前发射器规则爆发指定数量的粒子（应保持对持续计时状态无副作用）。</summary>
        void EmitBurst(in ParticleEmitContext ctx, int count, ref ParticleRandom rng);

        /// <summary>重置持续发射内部计时与爆发游标，便于复用。</summary>
        void Reset();
    }
}
