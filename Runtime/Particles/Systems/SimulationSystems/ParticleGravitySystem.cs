#nullable enable

using System;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Core;

namespace Apotheosis.Runtime.Particles.Systems
{
    /// <summary>重力/恒定加速度系统。</summary>
    public sealed class ParticleGravitySystem : IParticleSimSystem
    {
        private static readonly ParticleQuerySlot QuerySlot = ParticleQuerySlot.Allocate();

        public void Execute(ParticleStoreSlot slot, in ParticleSimContext ctx)
        {
            float dt = ctx.DeltaTime;
            if (!(dt > 0f) || slot.EntityCount == 0)
                return;

            var query = slot.GetOrCreateQuery(QuerySlot, static s => s.Query<ParticleSpatial, ParticleGravity>());
            foreach (var (spatialChunk, gravChunk, _) in query.Chunks)
            {
                Span<ParticleSpatial> spatials = spatialChunk.Span;
                ReadOnlySpan<ParticleGravity> gravs = gravChunk.Span;
                for (int i = 0; i < spatials.Length; i++)
                    spatials[i].Velocity += gravs[i].Acceleration * dt;
            }
        }
    }
}
