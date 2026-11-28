#nullable enable

using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Fragments;
using Friflo.Engine.ECS;

namespace Apotheosis.Runtime.Particles.Emitters.Modules
{
    /// <summary>Terraria 物块地形碰撞初始化模块（提供并初始化 <see cref="ParticleTileCollision"/>）。</summary>
    [ProvidesComponents(typeof(ParticleTileCollision))]
    public sealed class InitTileCollisionModule : IParticleSpawnModule
    {
        private static readonly ComponentTypes Provided = ComponentTypes.Get<ParticleTileCollision>();

        /// <summary>物块碰撞配置值。</summary>
        public ParticleTileCollision TileCollision { get; }

        public ParticleSpawnPhase Phase => ParticleSpawnPhase.Behavior;
        public ComponentTypes ProvidedComponents => Provided;

        public InitTileCollisionModule(ParticleTileCollision collision) => TileCollision = collision;

        public InitTileCollisionModule(ParticleTileCollisionMode mode = ParticleTileCollisionMode.Bounce, float restitution = 0.5f,
            float friction = 0.2f, float radius = 2f, byte maxBounces = 0)
            => TileCollision = new ParticleTileCollision(mode, restitution, friction, radius, maxBounces);

        public void Initialize(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng)
            => batch.Fill(TileCollision);
    }
}
