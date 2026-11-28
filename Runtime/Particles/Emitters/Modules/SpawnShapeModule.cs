#nullable enable

using System;
using System.Runtime.CompilerServices;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Fragments;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Emitters.Modules
{
    /// <summary>形状位置与初速度初始化模块（提供并初始化 <see cref="ParticleSpatial"/>）。</summary>
    [ProvidesComponents(typeof(ParticleSpatial))]
    public sealed class SpawnShapeModule : IParticleSpawnModule
    {
        private static readonly ComponentTypes Provided = ComponentTypes.Get<ParticleSpatial>();

        /// <summary>采样几何形状与基础方向分布。</summary>
        public EmitterShape Shape { get; }

        /// <summary>沿采样方向的初始速率范围（像素/秒）。</summary>
        public FloatRange Speed { get; }

        /// <summary>叠加在每个粒子初速度上的恒定速度偏移（像素/秒）。</summary>
        public Vector3 ConstantVelocityOffset { get; }

        /// <summary>附加的景深 Z 坐标随机偏移范围。</summary>
        public FloatRange DepthZ { get; }

        /// <summary>世界空间发射时是否按宿主原点的归一化旋转轴向同步旋转初始速度方向。</summary>
        public bool InheritOriginRotation { get; }

        /// <summary>点/盒/线段形状在 2D 模板下是否仍按 3D 球面方向采样（3D 姿态模板自动启用）。</summary>
        public bool Prefer3DDirection { get; }

        public ParticleSpawnPhase Phase => ParticleSpawnPhase.Core;
        public ComponentTypes ProvidedComponents => Provided;

        public SpawnShapeModule(EmitterShape shape, FloatRange speed = default, Vector3 constantVelocityOffset = default,
            FloatRange depthZ = default, bool inheritOriginRotation = false, bool prefer3DDirection = false)
        {
            Shape = shape;
            Speed = speed;
            ConstantVelocityOffset = constantVelocityOffset;
            DepthZ = depthZ;
            InheritOriginRotation = inheritOriginRotation;
            Prefer3DDirection = prefer3DDirection;
        }

        public void Initialize(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng)
        {
            Span<ParticleSpatial> spatials = batch.GetSpan<ParticleSpatial>();
            bool transformToWorld = !ctx.IsLocalSpace;
            Matrix originWorld = ctx.Origin.WorldMatrix;
            bool rotateVelocity = transformToWorld && InheritOriginRotation;

            Vector3 basisX = Vector3.UnitX;
            Vector3 basisY = Vector3.UnitY;
            Vector3 basisZ = Vector3.UnitZ;
            if (rotateVelocity)
                ExtractNormalizedAxes(in originWorld, out basisX, out basisY, out basisZ);

            bool prefer3DDir = Prefer3DDirection || batch.Has<ParticlePose3D>();
            bool hasDepthZ = DepthZ.Min != 0f || DepthZ.Max != 0f;
            EmitterShape shape = Shape;
            FloatRange speed = Speed;
            FloatRange depthZ = DepthZ;
            Vector3 velOffset = ConstantVelocityOffset;

            for (int i = 0; i < spatials.Length; i++)
            {
                shape.Sample(ref rng, prefer3DDir, out Vector3 localPos, out Vector3 localDir);
                if (hasDepthZ)
                    localPos.Z += depthZ.Sample(ref rng);

                Vector3 vel = localDir * speed.Sample(ref rng) + velOffset;
                if (transformToWorld)
                {
                    localPos = Vector3.Transform(localPos, originWorld);
                    if (rotateVelocity)
                        vel = basisX * vel.X + basisY * vel.Y + basisZ * vel.Z;
                }

                spatials[i] = new ParticleSpatial(localPos, vel);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ExtractNormalizedAxes(in Matrix m, out Vector3 right, out Vector3 up, out Vector3 forward)
        {
            // FNA 行向量矩阵前三行分别对应局部 X/Y/Z 轴在世界空间的方向；分别归一化以消除宿主缩放并保留负缩放镜像手性
            right = new Vector3(m.M11, m.M12, m.M13);
            up = new Vector3(m.M21, m.M22, m.M23);
            forward = new Vector3(m.M31, m.M32, m.M33);

            float lenX = right.LengthSquared();
            right = float.IsFinite(lenX) && lenX > 1e-12f ? right / MathF.Sqrt(lenX) : Vector3.UnitX;
            float lenY = up.LengthSquared();
            up = float.IsFinite(lenY) && lenY > 1e-12f ? up / MathF.Sqrt(lenY) : Vector3.UnitY;
            float lenZ = forward.LengthSquared();
            forward = float.IsFinite(lenZ) && lenZ > 1e-12f ? forward / MathF.Sqrt(lenZ) : Vector3.UnitZ;
        }
    }
}
