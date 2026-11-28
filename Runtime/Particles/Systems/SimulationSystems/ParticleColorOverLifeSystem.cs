#nullable enable

using System;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Core;
using Apotheosis.Runtime.Utils;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Systems
{
    /// <summary>随生命周期渐变 HDR 颜色系统。</summary>
    public sealed class ParticleColorOverLifeSystem : IParticleSimSystem
    {
        private static readonly ParticleQuerySlot QuerySlot = ParticleQuerySlot.Allocate();

        public void Execute(ParticleStoreSlot slot, in ParticleSimContext ctx)
        {
            if (slot.EntityCount == 0)
                return;

            var query = slot.GetOrCreateQuery(QuerySlot, static s => s.Query<ParticleLife, ParticleColorOverLife, ParticleRenderBase>());
            foreach (var (lifeChunk, curveChunk, renderChunk, _) in query.Chunks)
            {
                ReadOnlySpan<ParticleLife> lives = lifeChunk.Span;
                ReadOnlySpan<ParticleColorOverLife> curves = curveChunk.Span;
                Span<ParticleRenderBase> renders = renderChunk.Span;
                for (int i = 0; i < lives.Length; i++)
                {
                    ref readonly ParticleColorOverLife c = ref curves[i];
                    float k = MathUtils.EvaluateEase(c.Ease, lives[i].NormalizedAge);
                    renders[i].Color = Vector4.Lerp(c.StartColor, c.EndColor, k);
                }
            }
        }
    }
}
