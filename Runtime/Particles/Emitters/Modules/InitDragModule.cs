#nullable enable

using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Fragments;
using Friflo.Engine.ECS;

namespace Apotheosis.Runtime.Particles.Emitters.Modules
{
    /// <summary>线性与角速度阻尼初始化模块（提供并初始化 <see cref="ParticleDrag"/>）。</summary>
    [ProvidesComponents(typeof(ParticleDrag))]
    public sealed class InitDragModule : IParticleSpawnModule
    {
        private static readonly ComponentTypes Provided = ComponentTypes.Get<ParticleDrag>();

        /// <summary>阻尼组件值。</summary>
        public ParticleDrag Drag { get; }

        public ParticleSpawnPhase Phase => ParticleSpawnPhase.Behavior;
        public ComponentTypes ProvidedComponents => Provided;

        public InitDragModule(ParticleDrag drag) => Drag = drag;
        public InitDragModule(float linearDrag, float angularDrag = 0f) => Drag = new ParticleDrag(linearDrag, angularDrag);

        public void Initialize(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng)
            => batch.Fill(Drag);
    }
}
