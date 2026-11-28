#nullable enable

using System;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Fragments;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Emitters.Modules
{
    /// <summary>2D 速度朝向自动对齐与首帧拉伸初始化模块（提供 <see cref="ParticleVelocityAlign"/>，于后置阶段读取初速度并对齐姿态）。</summary>
    [ProvidesComponents(typeof(ParticleVelocityAlign))]
    [RequiresComponents(typeof(ParticleSpatial), typeof(ParticlePose2D))]
    public sealed class InitVelocityAlignModule : IParticleSpawnModule
    {
        private static readonly ComponentTypes Provided = ComponentTypes.Get<ParticleVelocityAlign>();

        /// <summary>速度对齐参数模板。</summary>
        public ParticleVelocityAlign VelocityAlign { get; }

        public ParticleSpawnPhase Phase => ParticleSpawnPhase.PostInit;
        public ComponentTypes ProvidedComponents => Provided;

        public InitVelocityAlignModule(ParticleVelocityAlign align)
        {
            align.AppliedStretch = 1f;
            VelocityAlign = align;
        }

        public InitVelocityAlignModule(float rotationOffset = 0f, float lengthStretchFactor = 0f, float minSpeedSq = 1f)
            => VelocityAlign = new ParticleVelocityAlign(rotationOffset, lengthStretchFactor, minSpeedSq);

        public void Initialize(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng)
        {
            Span<ParticleVelocityAlign> aligns = batch.GetSpan<ParticleVelocityAlign>();
            if (!batch.Has<ParticleSpatial>() || !batch.Has<ParticlePose2D>())
            {
                batch.Fill(VelocityAlign);
                return;
            }

            ReadOnlySpan<ParticleSpatial> spatials = batch.GetSpan<ParticleSpatial>();
            Span<ParticlePose2D> poses = batch.GetSpan<ParticlePose2D>();
            bool hasScaleOverLife = batch.Has<ParticleScaleOverLife>();
            ParticleVelocityAlign tpl = VelocityAlign;
            tpl.AppliedStretch = 1f;

            for (int i = 0; i < aligns.Length; i++)
            {
                ParticleVelocityAlign align = tpl;
                Vector3 vel = spatials[i].Velocity;
                float speedSq = vel.X * vel.X + vel.Y * vel.Y;

                if (speedSq >= align.MinSpeedSq && speedSq > 1e-6f && float.IsFinite(speedSq))
                {
                    ref ParticlePose2D pose = ref poses[i];
                    pose.Rotation = MathF.Atan2(vel.Y, vel.X) + align.RotationOffset;

                    if (align.LengthStretchFactor > 0f && float.IsFinite(align.LengthStretchFactor))
                    {
                        float stretch = 1f + MathF.Sqrt(speedSq) * align.LengthStretchFactor;
                        if (float.IsFinite(stretch))
                        {
                            pose.Scale.X *= stretch;
                            // 仅在无 ScaleOverLife 覆写时记录已施加拉伸，避免模拟首帧二次连乘
                            if (!hasScaleOverLife)
                                align.AppliedStretch = stretch;
                        }
                    }
                }

                aligns[i] = align;
            }
        }
    }
}
