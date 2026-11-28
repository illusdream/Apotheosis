#nullable enable

using System;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Fragments;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Emitters.Modules
{
    /// <summary>2D 姿态初始化模块（提供并初始化 <see cref="ParticlePose2D"/>）。</summary>
    [ProvidesComponents(typeof(ParticlePose2D))]
    public sealed class InitPose2DModule : IParticleSpawnModule
    {
        private static readonly ComponentTypes Provided = ComponentTypes.Get<ParticlePose2D>();

        /// <summary>等比初始缩放范围。</summary>
        public FloatRange Scale { get; }

        /// <summary>非等比缩放乘数（叠加于 <see cref="Scale"/> 采样结果之上）。</summary>
        public Vector2 ScaleMultiplier { get; }

        /// <summary>初始旋转弧度范围（<see cref="RandomRotation"/> 为真时忽略）。</summary>
        public FloatRange Rotation { get; }

        /// <summary>是否在 <c>[0, 2π)</c> 内完全随机化初始旋转角。</summary>
        public bool RandomRotation { get; }

        /// <summary>初始 2D 角速度范围（弧度/秒）。</summary>
        public FloatRange AngularVelocity { get; }

        public ParticleSpawnPhase Phase => ParticleSpawnPhase.Pose;
        public ComponentTypes ProvidedComponents => Provided;

        public InitPose2DModule(FloatRange? scale = null, Vector2? scaleMultiplier = null, FloatRange rotation = default,
            bool randomRotation = false, FloatRange angularVelocity = default)
        {
            Scale = scale ?? new FloatRange(1f);
            ScaleMultiplier = scaleMultiplier ?? Vector2.One;
            Rotation = rotation;
            RandomRotation = randomRotation;
            AngularVelocity = angularVelocity;
        }

        public void Initialize(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng)
        {
            Span<ParticlePose2D> poses = batch.GetSpan<ParticlePose2D>();
            FloatRange scale = Scale;
            Vector2 mul = ScaleMultiplier;
            FloatRange rotRange = Rotation;
            bool randomRot = RandomRotation;
            FloatRange angRange = AngularVelocity;

            for (int i = 0; i < poses.Length; i++)
            {
                float s = scale.Sample(ref rng);
                float rot = randomRot ? rng.NextAngle() : rotRange.Sample(ref rng);
                float angVel = angRange.Sample(ref rng);
                poses[i] = new ParticlePose2D(rot, angVel, new Vector2(s * mul.X, s * mul.Y));
            }
        }
    }
}
