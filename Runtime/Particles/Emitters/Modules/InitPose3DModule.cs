#nullable enable

using System;
using System.Runtime.CompilerServices;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Fragments;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Emitters.Modules
{
    /// <summary>3D 姿态初始化模块（提供并初始化 <see cref="ParticlePose3D"/>，支持完整四元数翻滚、朝向屏幕与伪透视景深缩放）。</summary>
    [ProvidesComponents(typeof(ParticlePose3D))]
    public sealed class InitPose3DModule : IParticleSpawnModule
    {
        private static readonly ComponentTypes Provided = ComponentTypes.Get<ParticlePose3D>();

        private readonly Quaternion _normalizedInitialRotation;

        /// <summary>等比初始缩放范围。</summary>
        public FloatRange Scale { get; }

        /// <summary>非等比 3D 缩放乘数。</summary>
        public Vector3 ScaleMultiplier { get; }

        /// <summary>是否按 SO(3) 均匀随机初始化 3D 旋转四元数。</summary>
        public bool RandomRotation { get; }

        /// <summary>固定 3D 初始旋转四元数（<see cref="RandomRotation"/> 为假时生效）。</summary>
        public Quaternion InitialRotation => _normalizedInitialRotation;

        /// <summary>局部三轴角速度采样下界（弧度/秒）。</summary>
        public Vector3 AngularVelocityMin { get; }

        /// <summary>局部三轴角速度采样上界（弧度/秒）。</summary>
        public Vector3 AngularVelocityMax { get; }

        /// <summary>3D 面片朝向模式。</summary>
        public ParticleBillboardMode Billboard { get; }

        /// <summary>伪透视景深缩放强度。</summary>
        public float PerspectiveFactor { get; }

        public ParticleSpawnPhase Phase => ParticleSpawnPhase.Pose;
        public ComponentTypes ProvidedComponents => Provided;

        public InitPose3DModule(FloatRange? scale = null, Vector3? scaleMultiplier = null, bool randomRotation = true,
            Quaternion? initialRotation = null, Vector3 angularVelocityMin = default, Vector3 angularVelocityMax = default,
            ParticleBillboardMode billboard = ParticleBillboardMode.None, float perspectiveFactor = 0f)
        {
            Scale = scale ?? new FloatRange(1f);
            ScaleMultiplier = scaleMultiplier ?? Vector3.One;
            RandomRotation = randomRotation;
            _normalizedInitialRotation = NormalizeSafe(initialRotation ?? Quaternion.Identity);
            AngularVelocityMin = angularVelocityMin;
            AngularVelocityMax = angularVelocityMax;
            Billboard = billboard;
            PerspectiveFactor = perspectiveFactor;
        }

        public void Initialize(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng)
        {
            Span<ParticlePose3D> poses = batch.GetSpan<ParticlePose3D>();
            FloatRange scale = Scale;
            Vector3 mul = ScaleMultiplier;
            bool randomRot = RandomRotation;
            Quaternion fixedRot = _normalizedInitialRotation;
            Vector3 angMin = AngularVelocityMin;
            Vector3 angMax = AngularVelocityMax;
            bool hasAngRange = angMin != angMax;
            ParticleBillboardMode billboard = Billboard;
            float persp = PerspectiveFactor;

            for (int i = 0; i < poses.Length; i++)
            {
                float s = scale.Sample(ref rng);
                Quaternion rot = randomRot ? rng.NextQuaternion() : fixedRot;
                Vector3 angVel = hasAngRange ? rng.NextVector3(angMin, angMax) : angMin;
                poses[i] = new ParticlePose3D(rot, angVel, new Vector3(s * mul.X, s * mul.Y, s * mul.Z), billboard, persp);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Quaternion NormalizeSafe(Quaternion q)
        {
            float lenSq = q.LengthSquared();
            if (!(lenSq > 1e-12f) || !float.IsFinite(lenSq))
                return Quaternion.Identity;

            float invLen = 1f / MathF.Sqrt(lenSq);
            return new Quaternion(q.X * invLen, q.Y * invLen, q.Z * invLen, q.W * invLen);
        }
    }
}
