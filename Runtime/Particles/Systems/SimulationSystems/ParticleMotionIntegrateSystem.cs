#nullable enable

using System;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Core;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Systems
{
    /// <summary>空间与姿态积分系统：推进 3D 位置、2D 旋转角以及 3D 四元数旋转。</summary>
    public sealed class ParticleMotionIntegrateSystem : IParticleSimSystem
    {
        private static readonly ParticleQuerySlot SpatialSlot = ParticleQuerySlot.Allocate();
        private static readonly ParticleQuerySlot Pose2DSlot = ParticleQuerySlot.Allocate();
        private static readonly ParticleQuerySlot Pose3DSlot = ParticleQuerySlot.Allocate();

        public void Execute(ParticleStoreSlot slot, in ParticleSimContext ctx)
        {
            float dt = ctx.DeltaTime;
            if (!(dt > 0f) || slot.EntityCount == 0)
                return;

            var spatialQuery = slot.GetOrCreateQuery(SpatialSlot, static s => s.Query<ParticleSpatial>());
            foreach (var (spatialChunk, _) in spatialQuery.Chunks)
            {
                Span<ParticleSpatial> spatials = spatialChunk.Span;
                for (int i = 0; i < spatials.Length; i++)
                {
                    ref ParticleSpatial sp = ref spatials[i];
                    sp.Position += sp.Velocity * dt;
                }
            }

            var pose2DQuery = slot.GetOrCreateQuery(Pose2DSlot, static s => s.Query<ParticlePose2D>());
            foreach (var (poseChunk, _) in pose2DQuery.Chunks)
            {
                Span<ParticlePose2D> poses = poseChunk.Span;
                for (int i = 0; i < poses.Length; i++)
                {
                    ref ParticlePose2D p = ref poses[i];
                    if (p.AngularVelocity != 0f)
                        p.Rotation += p.AngularVelocity * dt;
                }
            }

            var pose3DQuery = slot.GetOrCreateQuery(Pose3DSlot, static s => s.Query<ParticlePose3D>());
            foreach (var (poseChunk, _) in pose3DQuery.Chunks)
            {
                Span<ParticlePose3D> poses = poseChunk.Span;
                for (int i = 0; i < poses.Length; i++)
                {
                    ref ParticlePose3D p = ref poses[i];
                    Vector3 w = p.AngularVelocity;
                    float lenSq = w.LengthSquared();
                    if (!(lenSq > 1e-10f))
                        continue;

                    // 精确轴角指数映射；XNA/FNA 行向量约定下 M(cur * delta) = M(delta) * M(cur) 表示绕局部轴自转
                    float speed = MathF.Sqrt(lenSq);
                    (float sin, float cos) = MathF.SinCos(speed * dt * 0.5f);
                    float scale = sin / speed;
                    var delta = new Quaternion(w.X * scale, w.Y * scale, w.Z * scale, cos);

                    Quaternion cur = p.Rotation.LengthSquared() > 1e-12f ? p.Rotation : Quaternion.Identity;
                    p.Rotation = Quaternion.Normalize(cur * delta);
                }
            }
        }
    }
}
