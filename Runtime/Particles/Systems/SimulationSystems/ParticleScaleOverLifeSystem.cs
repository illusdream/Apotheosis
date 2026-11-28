#nullable enable

using System;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Core;
using Apotheosis.Runtime.Utils;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Systems
{
    /// <summary>随生命周期渐变缩放系统（同步覆盖 2D 与 3D 姿态）。</summary>
    public sealed class ParticleScaleOverLifeSystem : IParticleSimSystem
    {
        private static readonly ParticleQuerySlot Query2DSlot = ParticleQuerySlot.Allocate();
        private static readonly ParticleQuerySlot Query3DSlot = ParticleQuerySlot.Allocate();

        public void Execute(ParticleStoreSlot slot, in ParticleSimContext ctx)
        {
            if (slot.EntityCount == 0)
                return;

            var query2D = slot.GetOrCreateQuery(Query2DSlot, static s => s.Query<ParticleLife, ParticleScaleOverLife, ParticlePose2D>());
            foreach (var (lifeChunk, curveChunk, poseChunk, _) in query2D.Chunks)
            {
                ReadOnlySpan<ParticleLife> lives = lifeChunk.Span;
                ReadOnlySpan<ParticleScaleOverLife> curves = curveChunk.Span;
                Span<ParticlePose2D> poses = poseChunk.Span;
                for (int i = 0; i < lives.Length; i++)
                {
                    ref readonly ParticleScaleOverLife c = ref curves[i];
                    float k = MathUtils.EvaluateEase(c.Ease, lives[i].NormalizedAge);
                    Vector3 s = Vector3.Lerp(c.StartScale, c.EndScale, k);
                    poses[i].Scale = new Vector2(s.X, s.Y);
                }
            }

            var query3D = slot.GetOrCreateQuery(Query3DSlot, static s => s.Query<ParticleLife, ParticleScaleOverLife, ParticlePose3D>());
            foreach (var (lifeChunk, curveChunk, poseChunk, _) in query3D.Chunks)
            {
                ReadOnlySpan<ParticleLife> lives = lifeChunk.Span;
                ReadOnlySpan<ParticleScaleOverLife> curves = curveChunk.Span;
                Span<ParticlePose3D> poses = poseChunk.Span;
                for (int i = 0; i < lives.Length; i++)
                {
                    ref readonly ParticleScaleOverLife c = ref curves[i];
                    float k = MathUtils.EvaluateEase(c.Ease, lives[i].NormalizedAge);
                    poses[i].Scale = Vector3.Lerp(c.StartScale, c.EndScale, k);
                }
            }
        }
    }
}
