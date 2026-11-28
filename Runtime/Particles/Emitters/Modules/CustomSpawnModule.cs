#nullable enable

using System;
using Apotheosis.Runtime.Particles.Fragments;
using Friflo.Engine.ECS;

namespace Apotheosis.Runtime.Particles.Emitters.Modules
{
    /// <summary>通用委托自定义初始化模块（免造子类即可在任意阶段执行自定义批次逻辑或贡献额外组件）。</summary>
    public sealed class CustomSpawnModule : IParticleSpawnModule
    {
        private readonly ParticleBatchInitializer _initializer;

        public ParticleSpawnPhase Phase { get; }
        public ComponentTypes ProvidedComponents { get; }

        public CustomSpawnModule(ParticleBatchInitializer initializer, ParticleSpawnPhase phase = ParticleSpawnPhase.PostInit, ParticleFragment extraComponents = default)
        {
            ArgumentNullException.ThrowIfNull(initializer);
            _initializer = initializer;
            Phase = phase;
            ProvidedComponents = extraComponents.Types;
        }

        public void Initialize(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng)
            => _initializer(in batch, in ctx, ref rng);
    }
}
