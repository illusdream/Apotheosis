#nullable enable

using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Fragments;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Emitters.Modules
{
    /// <summary>重力/恒定加速度初始化模块（提供并初始化 <see cref="ParticleGravity"/>）。</summary>
    [ProvidesComponents(typeof(ParticleGravity))]
    public sealed class InitGravityModule : IParticleSpawnModule
    {
        private static readonly ComponentTypes Provided = ComponentTypes.Get<ParticleGravity>();

        /// <summary>恒定加速度组件值。</summary>
        public ParticleGravity Gravity { get; }

        public ParticleSpawnPhase Phase => ParticleSpawnPhase.Behavior;
        public ComponentTypes ProvidedComponents => Provided;

        public InitGravityModule(ParticleGravity gravity) => Gravity = gravity;
        public InitGravityModule(Vector3 acceleration) => Gravity = new ParticleGravity(acceleration);
        public InitGravityModule(Vector2 acceleration2D) => Gravity = new ParticleGravity(acceleration2D);
        public InitGravityModule(float gravityY) => Gravity = new ParticleGravity(gravityY);

        public void Initialize(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng)
            => batch.Fill(Gravity);
    }
}
