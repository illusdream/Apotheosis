#nullable enable

using System;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Core;

namespace Apotheosis.Runtime.Particles.Systems
{
    /// <summary>指数阻尼系统：稳定衰减线速度及 2D/3D 角速度，避免大步长或高阻尼下速度反向。</summary>
    public sealed class ParticleDragSystem : IParticleSimSystem
    {
        private static readonly ParticleQuerySlot LinearSlot = ParticleQuerySlot.Allocate();
        private static readonly ParticleQuerySlot Angular2DSlot = ParticleQuerySlot.Allocate();
        private static readonly ParticleQuerySlot Angular3DSlot = ParticleQuerySlot.Allocate();

        public void Execute(ParticleStoreSlot slot, in ParticleSimContext ctx)
        {
            float dt = ctx.DeltaTime;
            if (!(dt > 0f) || slot.EntityCount == 0)
                return;

            var linearQuery = slot.GetOrCreateQuery(LinearSlot, static s => s.Query<ParticleSpatial, ParticleDrag>());
            foreach (var (spatialChunk, dragChunk, _) in linearQuery.Chunks)
            {
                Span<ParticleSpatial> spatials = spatialChunk.Span;
                ReadOnlySpan<ParticleDrag> drags = dragChunk.Span;
                for (int i = 0; i < spatials.Length; i++)
                {
                    float d = drags[i].LinearDrag;
                    if (d > 0f)
                        spatials[i].Velocity *= MathF.Exp(-d * dt);
                }
            }

            var ang2DQuery = slot.GetOrCreateQuery(Angular2DSlot, static s => s.Query<ParticlePose2D, ParticleDrag>());
            foreach (var (poseChunk, dragChunk, _) in ang2DQuery.Chunks)
            {
                Span<ParticlePose2D> poses = poseChunk.Span;
                ReadOnlySpan<ParticleDrag> drags = dragChunk.Span;
                for (int i = 0; i < poses.Length; i++)
                {
                    float d = drags[i].AngularDrag;
                    if (d > 0f)
                        poses[i].AngularVelocity *= MathF.Exp(-d * dt);
                }
            }

            var ang3DQuery = slot.GetOrCreateQuery(Angular3DSlot, static s => s.Query<ParticlePose3D, ParticleDrag>());
            foreach (var (poseChunk, dragChunk, _) in ang3DQuery.Chunks)
            {
                Span<ParticlePose3D> poses = poseChunk.Span;
                ReadOnlySpan<ParticleDrag> drags = dragChunk.Span;
                for (int i = 0; i < poses.Length; i++)
                {
                    float d = drags[i].AngularDrag;
                    if (d > 0f)
                        poses[i].AngularVelocity *= MathF.Exp(-d * dt);
                }
            }
        }
    }
}
