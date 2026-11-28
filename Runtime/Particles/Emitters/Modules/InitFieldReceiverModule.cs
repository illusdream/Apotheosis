#nullable enable

using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Core;
using Apotheosis.Runtime.Particles.Fragments;
using Friflo.Engine.ECS;

namespace Apotheosis.Runtime.Particles.Emitters.Modules
{
    /// <summary>力场接收器初始化模块（提供并初始化 <see cref="ParticleFieldReceiver"/>）。</summary>
    [ProvidesComponents(typeof(ParticleFieldReceiver))]
    public sealed class InitFieldReceiverModule : IParticleSpawnModule
    {
        private static readonly ComponentTypes Provided = ComponentTypes.Get<ParticleFieldReceiver>();

        /// <summary>力场接收器组件值。</summary>
        public ParticleFieldReceiver FieldReceiver { get; }

        public ParticleSpawnPhase Phase => ParticleSpawnPhase.Behavior;
        public ComponentTypes ProvidedComponents => Provided;

        public InitFieldReceiverModule(ParticleFieldReceiver receiver) => FieldReceiver = receiver;
        public InitFieldReceiverModule(ParticleFieldMask mask = ParticleFieldMask.All, float forceScale = 1f)
            => FieldReceiver = new ParticleFieldReceiver(mask, forceScale);

        public void Initialize(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng)
            => batch.Fill(FieldReceiver);
    }
}
