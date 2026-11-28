#nullable enable

using System;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Core;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Systems
{
    /// <summary>速度方向自动对齐与速度拉伸系统（须排在 <see cref="ParticleScaleOverLifeSystem"/> 之后执行）。</summary>
    public sealed class ParticleVelocityAlignSystem : IParticleSimSystem
    {
        private static readonly ParticleQuerySlot WithScaleCurveSlot = ParticleQuerySlot.Allocate();
        private static readonly ParticleQuerySlot WithoutScaleCurveSlot = ParticleQuerySlot.Allocate();

        public void Execute(ParticleStoreSlot slot, in ParticleSimContext ctx)
        {
            if (slot.EntityCount == 0)
                return;

            // 分支 1：同时挂载了 ParticleScaleOverLife，当帧基础 Scale 刚被重置，直接乘以本帧速度拉伸倍率
            var withCurve = slot.GetOrCreateQuery(WithScaleCurveSlot, static s =>
                s.Query<ParticleSpatial, ParticlePose2D, ParticleVelocityAlign>().AllComponents(ComponentTypes.Get<ParticleScaleOverLife>()));
            foreach (var (spatialChunk, poseChunk, alignChunk, _) in withCurve.Chunks)
            {
                ReadOnlySpan<ParticleSpatial> spatials = spatialChunk.Span;
                Span<ParticlePose2D> poses = poseChunk.Span;
                ReadOnlySpan<ParticleVelocityAlign> aligns = alignChunk.Span;
                for (int i = 0; i < spatials.Length; i++)
                {
                    Vector3 v = spatials[i].Velocity;
                    float speedSq = v.X * v.X + v.Y * v.Y;
                    ref readonly ParticleVelocityAlign align = ref aligns[i];
                    float minSq = align.MinSpeedSq > 1e-6f ? align.MinSpeedSq : 1e-6f;
                    if (!(speedSq >= minSq))
                        continue;

                    poses[i].Rotation = MathF.Atan2(v.Y, v.X) + align.RotationOffset;
                    if (align.LengthStretchFactor > 0f)
                    {
                        float stretch = 1f + MathF.Sqrt(speedSq) * align.LengthStretchFactor;
                        if (float.IsFinite(stretch))
                            poses[i].Scale.X *= stretch;
                    }
                }
            }

            // 分支 2：未挂载 ParticleScaleOverLife，通过 AppliedStretch 撤销上帧拉伸再应用新拉伸，彻底避免每帧连乘爆炸
            var withoutCurve = slot.GetOrCreateQuery(WithoutScaleCurveSlot, static s =>
                s.Query<ParticleSpatial, ParticlePose2D, ParticleVelocityAlign>().WithoutAnyComponents(ComponentTypes.Get<ParticleScaleOverLife>()));
            foreach (var (spatialChunk, poseChunk, alignChunk, _) in withoutCurve.Chunks)
            {
                ReadOnlySpan<ParticleSpatial> spatials = spatialChunk.Span;
                Span<ParticlePose2D> poses = poseChunk.Span;
                Span<ParticleVelocityAlign> aligns = alignChunk.Span;
                for (int i = 0; i < spatials.Length; i++)
                {
                    Vector3 v = spatials[i].Velocity;
                    float speedSq = v.X * v.X + v.Y * v.Y;
                    ref ParticleVelocityAlign align = ref aligns[i];
                    float prevStretch = align.AppliedStretch > 1e-4f && float.IsFinite(align.AppliedStretch) ? align.AppliedStretch : 1f;
                    float minSq = align.MinSpeedSq > 1e-6f ? align.MinSpeedSq : 1e-6f;

                    float nextStretch = 1f;
                    if (speedSq >= minSq)
                    {
                        poses[i].Rotation = MathF.Atan2(v.Y, v.X) + align.RotationOffset;
                        if (align.LengthStretchFactor > 0f)
                        {
                            float candidate = 1f + MathF.Sqrt(speedSq) * align.LengthStretchFactor;
                            if (float.IsFinite(candidate))
                                nextStretch = candidate;
                        }
                    }

                    if (nextStretch != prevStretch)
                    {
                        poses[i].Scale.X = (poses[i].Scale.X / prevStretch) * nextStretch;
                        align.AppliedStretch = nextStretch;
                    }
                }
            }
        }
    }
}
