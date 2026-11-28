#nullable enable

using System;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Fragments;
using Friflo.Engine.ECS;

namespace Apotheosis.Runtime.Particles.Emitters.Modules
{
    /// <summary>粒子生命周期初始化模块（提供并初始化 <see cref="ParticleLife"/>）。</summary>
    [ProvidesComponents(typeof(ParticleLife))]
    public sealed class InitLifeModule : IParticleSpawnModule
    {
        private static readonly ComponentTypes Provided = ComponentTypes.Get<ParticleLife>();

        /// <summary>存活时长范围（秒）。</summary>
        public FloatRange Lifetime { get; }

        public ParticleSpawnPhase Phase => ParticleSpawnPhase.Core;
        public ComponentTypes ProvidedComponents => Provided;

        public InitLifeModule(FloatRange lifetime) => Lifetime = lifetime;

        public InitLifeModule(float minSeconds, float maxSeconds) => Lifetime = new FloatRange(minSeconds, maxSeconds);

        public void Initialize(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng)
        {
            Span<ParticleLife> lives = batch.GetSpan<ParticleLife>();
            FloatRange range = Lifetime;
            for (int i = 0; i < lives.Length; i++)
            {
                float maxAge = range.Sample(ref rng);
                lives[i] = new ParticleLife(float.IsFinite(maxAge) ? maxAge : 1e-4f);
            }
        }
    }
}
