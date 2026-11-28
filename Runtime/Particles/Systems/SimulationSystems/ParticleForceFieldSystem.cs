#nullable enable

using System;
using System.Runtime.CompilerServices;
using Apotheosis.Runtime.Foundation.Collections;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Core;
using Apotheosis.Runtime.Particles.Fields;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Systems
{
    /// <summary>全局与局部粒子力场模拟系统（局部空间下采用 O(Fields) 逆矩阵预变换，避免粒子内循环矩阵乘法）。</summary>
    public sealed class ParticleForceFieldSystem : IParticleSimSystem
    {
        private static readonly ParticleQuerySlot QuerySlot = ParticleQuerySlot.Allocate();

        private struct PreparedField
        {
            public ForceFieldKind Kind;
            public ForceFieldFalloff Falloff;
            public ForceFieldShape Shape;
            public bool HasBoxZ;
            public ParticleFieldMask Mask;
            public Vector3 Position;
            public Vector3 AxisOrDir;
            public Vector3 BoxDualX;
            public Vector3 BoxDualY;
            public Vector3 BoxDualZ;
            public float Strength;
            public float InwardStrength;
            public float Radius;
            public float RadiusSq;
            public float InvRadius;
            public float MinDistanceSq;
            public float Frequency;
        }

        // 工作线程按 Store 串行执行模拟，复用暂存缓冲实现零 GC
        private ValueBuffer<PreparedField> _prepared = new(16, 16);

        public void Execute(ParticleStoreSlot slot, in ParticleSimContext ctx)
        {
            float dt = ctx.DeltaTime;
            if (!float.IsFinite(dt) || !(dt > 0f) || slot.EntityCount == 0)
                return;

            ReadOnlySpan<ForceFieldData> globalFields = ctx.AffectByGlobalFields && ctx.GlobalFieldMask != ParticleFieldMask.None
                ? ctx.GlobalFields
                : ReadOnlySpan<ForceFieldData>.Empty;
            ReadOnlySpan<ForceFieldData> localFields = ctx.LocalFields;
            if (globalFields.IsEmpty && localFields.IsEmpty)
                return;

            _prepared.Clear(clearReferences: false);
            ParticleFieldMask combinedMask = ParticleFieldMask.None;

            // 1. 预处理全局世界力场：若当前存储处于 LocalSpace，先校验矩阵非奇异再按 O(Fields) 一次性反变换至局部空间
            if (!globalFields.IsEmpty)
            {
                bool transformToLocal = ctx.IsLocalSpace;
                bool canApplyGlobal = true;
                float invPlaneScale = 1f;
                bool mirrorDet = false;

                if (transformToLocal)
                {
                    float originDet = ctx.OriginWorldMatrix.Determinant();
                    if (!float.IsFinite(originDet) || !(MathF.Abs(originDet) > 1e-12f))
                    {
                        canApplyGlobal = false;
                    }
                    else
                    {
                        invPlaneScale = ComputeXYAxisScale(in ctx.InverseOriginMatrix);
                        // 旋转轴为赝矢量：宿主发生镜像（负行列式）时需翻转旋涡轴向以维持世界空间旋转手性
                        mirrorDet = originDet < 0f;
                    }
                }

                if (canApplyGlobal)
                {
                    ParticleFieldMask allowedGlobalMask = ctx.GlobalFieldMask;
                    for (int i = 0; i < globalFields.Length; i++)
                    {
                        ref readonly ForceFieldData src = ref globalFields[i];
                        if ((src.Mask & allowedGlobalMask) == 0)
                            continue;

                        if (TryPrepareField(in src, transformToLocal, in ctx.OriginWorldMatrix, in ctx.InverseOriginMatrix, invPlaneScale, mirrorDet, out PreparedField pf))
                        {
                            _prepared.Add(in pf);
                            combinedMask |= pf.Mask;
                        }
                    }
                }
            }

            // 2. 预处理子系统专属局部力场（已处于该子系统模拟空间，无需额外矩阵变换）
            for (int i = 0; i < localFields.Length; i++)
            {
                ref readonly ForceFieldData src = ref localFields[i];
                if (src.Mask == ParticleFieldMask.None)
                    continue;

                if (TryPrepareField(in src, transformToLocal: false, in ctx.OriginWorldMatrix, in ctx.InverseOriginMatrix, invPlaneScale: 1f, mirrorDet: false, out PreparedField pf))
                {
                    _prepared.Add(in pf);
                    combinedMask |= pf.Mask;
                }
            }

            if (_prepared.Count == 0 || combinedMask == ParticleFieldMask.None)
                return;

            ReadOnlySpan<PreparedField> fields = _prepared.AsReadOnlySpan();
            float simTime = float.IsFinite(ctx.SimulationTime) ? ctx.SimulationTime : 0f;
            var query = slot.GetOrCreateQuery(QuerySlot, static s => s.Query<ParticleSpatial, ParticleFieldReceiver>());

            foreach (var (spatialChunk, receiverChunk, _) in query.Chunks)
            {
                Span<ParticleSpatial> spatials = spatialChunk.Span;
                ReadOnlySpan<ParticleFieldReceiver> receivers = receiverChunk.Span;

                for (int i = 0; i < spatials.Length; i++)
                {
                    ref readonly ParticleFieldReceiver receiver = ref receivers[i];
                    if ((receiver.Mask & combinedMask) == 0 || receiver.ForceScale == 0f || !float.IsFinite(receiver.ForceScale))
                        continue;

                    ref ParticleSpatial spatial = ref spatials[i];
                    Vector3 pos = spatial.Position;
                    Vector3 totalAccel = Vector3.Zero;

                    for (int f = 0; f < fields.Length; f++)
                    {
                        ref readonly PreparedField field = ref fields[f];
                        if ((receiver.Mask & field.Mask) == 0)
                            continue;

                        EvaluateField(in field, pos, simTime, ref totalAccel);
                    }

                    Vector3 deltaV = totalAccel * (receiver.ForceScale * dt);
                    if (float.IsFinite(deltaV.X) && float.IsFinite(deltaV.Y) && float.IsFinite(deltaV.Z))
                        spatial.Velocity += deltaV;
                }
            }
        }

        private static bool TryPrepareField(in ForceFieldData src, bool transformToLocal, in Matrix originWorld, in Matrix invOrigin,
            float invPlaneScale, bool mirrorDet, out PreparedField pf)
        {
            pf = default;
            if (src.Mask == ParticleFieldMask.None)
                return false;

            if (src.Shape != ForceFieldShape.Sphere && src.Shape != ForceFieldShape.Box)
                return false;

            float strength = float.IsFinite(src.Strength) ? src.Strength : 0f;
            float inward = float.IsFinite(src.InwardStrength) ? src.InwardStrength : 0f;
            if (strength == 0f && (src.Kind != ForceFieldKind.Vortex || inward == 0f))
                return false;

            bool isBox = src.Shape == ForceFieldShape.Box;
            Vector3 pos = src.Position;
            float radius = !isBox && float.IsFinite(src.Radius) && src.Radius > 0f ? src.Radius : 0f;
            float minDist = float.IsFinite(src.MinDistance) && src.MinDistance > 1f ? src.MinDistance : 1f;
            float freq = float.IsFinite(src.Frequency) && src.Frequency > 0f ? src.Frequency : 0.02f;
            Vector3 axisOrDir = src.Direction;

            bool needsPos = isBox || radius > 0f || src.Kind == ForceFieldKind.PointRadial || src.Kind == ForceFieldKind.Vortex;
            if (needsPos && (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z)))
                return false;

            Vector3 boxDualX = default;
            Vector3 boxDualY = default;
            Vector3 boxDualZ = default;
            bool hasBoxZ = false;
            if (isBox)
            {
                float hx = src.BoxHalfExtents.X;
                float hy = src.BoxHalfExtents.Y;
                float hz = src.BoxHalfExtents.Z;
                if (!float.IsFinite(hx) || !(hx > 0f) || !float.IsFinite(hy) || !(hy > 0f))
                    return false;

                float rot = float.IsFinite(src.BoxRotation2D) ? src.BoxRotation2D : 0f;
                (float sin, float cos) = rot != 0f ? MathF.SinCos(rot) : (0f, 1f);
                Vector3 worldDualX = new(cos / hx, sin / hx, 0f);
                Vector3 worldDualY = new(-sin / hy, cos / hy, 0f);
                hasBoxZ = float.IsFinite(hz) && hz > 0f;
                Vector3 worldDualZ = hasBoxZ ? new Vector3(0f, 0f, 1f / hz) : Vector3.Zero;

                if (transformToLocal)
                {
                    // 行向量恒等式：dot((p_l - pos_l) * L, a) == dot(p_l - pos_l, (dot(R0, a), dot(R1, a), dot(R2, a)))
                    // 仅 O(Fields) 预计算对偶轴即可在任意 3D 旋转/非均匀缩放/镜像/剪切 LocalSpace 下实现精确矩形判定
                    boxDualX = TransformDualVectorToLocal(worldDualX, in originWorld);
                    boxDualY = TransformDualVectorToLocal(worldDualY, in originWorld);
                    if (hasBoxZ)
                        boxDualZ = TransformDualVectorToLocal(worldDualZ, in originWorld);
                }
                else
                {
                    boxDualX = worldDualX;
                    boxDualY = worldDualY;
                    boxDualZ = worldDualZ;
                }
            }

            if (transformToLocal)
            {
                if (needsPos)
                    pos = Vector3.Transform(pos, invOrigin);
                radius *= invPlaneScale;
                minDist = MathF.Max(1e-3f, minDist * invPlaneScale);
                if (invPlaneScale > 1e-6f)
                    freq /= invPlaneScale;
            }

            switch (src.Kind)
            {
                case ForceFieldKind.Directional:
                {
                    // 先在世界空间单位化方向，再经 invOrigin 线性反变换（不重归一化也不乘均尺），对任意非均匀缩放/镜像/剪切 100% 精确
                    axisOrDir = NormalizeOrFallback(axisOrDir, Vector3.UnitX);
                    if (transformToLocal)
                        axisOrDir = Vector3.TransformNormal(axisOrDir, invOrigin);
                    break;
                }

                case ForceFieldKind.PointRadial:
                {
                    if (transformToLocal)
                        strength *= invPlaneScale;
                    axisOrDir = Vector3.UnitZ;
                    break;
                }

                case ForceFieldKind.Vortex:
                {
                    // 先在世界空间兜底单位化旋转轴，确保 3D 倾斜 LocalSpace 下零轴向仍回退至世界 +Z 轴而非局部 +Z 轴
                    axisOrDir = NormalizeOrFallback(axisOrDir, Vector3.UnitZ);
                    if (transformToLocal)
                    {
                        axisOrDir = NormalizeOrFallback(Vector3.TransformNormal(axisOrDir, invOrigin), Vector3.UnitZ);
                        strength *= invPlaneScale;
                        inward *= invPlaneScale;
                    }

                    if (mirrorDet)
                        axisOrDir = -axisOrDir;
                    break;
                }

                case ForceFieldKind.Turbulence:
                {
                    // 湍流在局部空间下沿局部坐标轴采样近似；Direction 存储各轴幅值权重
                    float wx = float.IsFinite(axisOrDir.X) ? axisOrDir.X : 0f;
                    float wy = float.IsFinite(axisOrDir.Y) ? axisOrDir.Y : 0f;
                    float wz = float.IsFinite(axisOrDir.Z) ? axisOrDir.Z : 0f;
                    if (wx == 0f && wy == 0f && wz == 0f)
                        return false;
                    if (transformToLocal)
                        strength *= invPlaneScale;
                    axisOrDir = new Vector3(wx, wy, wz);
                    break;
                }

                default:
                    // 拒绝未知枚举值，避免污染 combinedMask
                    return false;
            }

            float radiusSq = radius > 0f ? radius * radius : 0f;
            if (!float.IsFinite(radiusSq))
                radius = 0f;

            pf = new PreparedField
            {
                Kind = src.Kind,
                Falloff = src.Falloff,
                Shape = src.Shape,
                HasBoxZ = hasBoxZ,
                Mask = src.Mask,
                Position = pos,
                AxisOrDir = axisOrDir,
                BoxDualX = boxDualX,
                BoxDualY = boxDualY,
                BoxDualZ = boxDualZ,
                Strength = strength,
                InwardStrength = inward,
                Radius = radius,
                RadiusSq = radius > 0f ? radiusSq : 0f,
                InvRadius = radius > 0f ? 1f / radius : 0f,
                MinDistanceSq = minDist * minDist,
                Frequency = freq,
            };
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EvaluateField(in PreparedField f, Vector3 pos, float simTime, ref Vector3 accel)
        {
            switch (f.Kind)
            {
                case ForceFieldKind.Directional:
                {
                    if (!TryComputeVolumeFalloff(in f, pos, out float falloff))
                        return;

                    accel += f.AxisOrDir * (f.Strength * falloff);
                    return;
                }

                case ForceFieldKind.PointRadial:
                {
                    Vector3 delta = f.Position - pos;
                    if (!TryComputeVolumeFalloffFromDelta(in f, delta, out float falloff, out float distSq))
                        return;

                    float rawDist = MathF.Sqrt(distSq);
                    float safeDist = distSq > f.MinDistanceSq ? rawDist : MathF.Sqrt(f.MinDistanceSq);
                    if (rawDist > 1e-5f)
                        accel += delta * (f.Strength * falloff / safeDist);
                    return;
                }

                case ForceFieldKind.Vortex:
                {
                    Vector3 toCenter = f.Position - pos;
                    if (!TryComputeVolumeFalloffFromDelta(in f, toCenter, out float falloff, out _))
                        return;

                    // 投影到垂直旋转轴的平面，得到径向向量与切向旋涡方向
                    float axial = Vector3.Dot(toCenter, f.AxisOrDir);
                    Vector3 radialToCenter = toCenter - f.AxisOrDir * axial;
                    float radDistSq = radialToCenter.LengthSquared();
                    if (!(radDistSq > 1e-8f))
                        return;

                    float radDist = MathF.Sqrt(radDistSq);
                    float safeRadDist = radDistSq > f.MinDistanceSq ? radDist : MathF.Sqrt(f.MinDistanceSq);
                    float invNorm = 1f / radDist;
                    float radialScale = radDist / safeRadDist; // 中心附近平滑衰减切向与向心力避免震荡

                    Vector3 radialDir = radialToCenter * invNorm;
                    Vector3 tangentDir = Vector3.Cross(f.AxisOrDir, radialDir);

                    accel += tangentDir * (f.Strength * falloff * radialScale);
                    if (f.InwardStrength != 0f)
                        accel += radialDir * (f.InwardStrength * falloff * radialScale);
                    return;
                }

                case ForceFieldKind.Turbulence:
                {
                    if (!TryComputeVolumeFalloff(in f, pos, out float falloff))
                        return;

                    // 轻量无纹理三角伪卷曲湍流：各轴相位互质避免周期网格感
                    float freq = f.Frequency;
                    float phase = simTime * 2.1f;
                    float px = pos.X * freq + phase;
                    float py = pos.Y * freq - phase * 0.73f;
                    float pz = pos.Z * freq + phase * 0.51f;

                    float nx = MathF.Sin(py + MathF.Cos(pz * 1.31f + px * 0.47f));
                    float ny = MathF.Cos(px - MathF.Sin(pz * 1.13f + py * 0.53f));
                    float nz = MathF.Sin(px * 0.71f - py * 0.89f + MathF.Cos(pz));

                    float mag = f.Strength * falloff;
                    accel.X += nx * f.AxisOrDir.X * mag;
                    accel.Y += ny * f.AxisOrDir.Y * mag;
                    accel.Z += nz * f.AxisOrDir.Z * mag;
                    return;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryComputeVolumeFalloff(in PreparedField f, Vector3 pos, out float falloff)
        {
            if (f.Shape == ForceFieldShape.Box)
                return TryComputeBoxFalloff(in f, f.Position - pos, out falloff);

            if (f.Radius > 0f)
            {
                float distSq = Vector3.DistanceSquared(pos, f.Position);
                if (!(distSq < f.RadiusSq))
                {
                    falloff = 0f;
                    return false;
                }

                falloff = ComputeFalloff(f.Falloff, MathF.Sqrt(distSq) * f.InvRadius);
                return true;
            }

            falloff = 1f;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryComputeVolumeFalloffFromDelta(in PreparedField f, Vector3 delta, out float falloff, out float distSq)
        {
            distSq = delta.LengthSquared();
            if (f.Shape == ForceFieldShape.Box)
                return TryComputeBoxFalloff(in f, delta, out falloff);

            if (f.Radius > 0f)
            {
                if (!(distSq < f.RadiusSq))
                {
                    falloff = 0f;
                    return false;
                }

                falloff = ComputeFalloff(f.Falloff, MathF.Sqrt(distSq) * f.InvRadius);
                return true;
            }

            falloff = 1f;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryComputeBoxFalloff(in PreparedField f, Vector3 delta, out float falloff)
        {
            float u = MathF.Abs(Vector3.Dot(delta, f.BoxDualX));
            if (!(u < 1f))
            {
                falloff = 0f;
                return false;
            }

            float v = MathF.Abs(Vector3.Dot(delta, f.BoxDualY));
            if (!(v < 1f))
            {
                falloff = 0f;
                return false;
            }

            float normDist = u > v ? u : v;
            if (f.HasBoxZ)
            {
                float w = MathF.Abs(Vector3.Dot(delta, f.BoxDualZ));
                if (!(w < 1f))
                {
                    falloff = 0f;
                    return false;
                }

                if (w > normDist)
                    normDist = w;
            }

            falloff = ComputeFalloff(f.Falloff, normDist);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 TransformDualVectorToLocal(Vector3 worldDual, in Matrix originWorld)
        {
            return new Vector3(
                originWorld.M11 * worldDual.X + originWorld.M12 * worldDual.Y + originWorld.M13 * worldDual.Z,
                originWorld.M21 * worldDual.X + originWorld.M22 * worldDual.Y + originWorld.M23 * worldDual.Z,
                originWorld.M31 * worldDual.X + originWorld.M32 * worldDual.Y + originWorld.M33 * worldDual.Z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ComputeFalloff(ForceFieldFalloff mode, float normalizedDist)
        {
            float t = Math.Clamp(1f - normalizedDist, 0f, 1f);
            return mode switch
            {
                ForceFieldFalloff.Linear => t,
                ForceFieldFalloff.Quadratic => t * t,
                _ => 1f,
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 NormalizeOrFallback(Vector3 v, Vector3 fallback)
        {
            float lenSq = v.LengthSquared();
            return float.IsFinite(lenSq) && lenSq > 1e-12f ? v / MathF.Sqrt(lenSq) : fallback;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ComputeXYAxisScale(in Matrix m)
        {
            // 取 XY 主平面轴均尺，避免 Transform3D.From2D(..., scale) 在 Z=1 时被三轴平均拉偏 2D 缩放比例
            float sx = new Vector3(m.M11, m.M12, m.M13).Length();
            float sy = new Vector3(m.M21, m.M22, m.M23).Length();
            float avg = (sx + sy) * 0.5f;
            return float.IsFinite(avg) && avg > 1e-5f ? avg : 1f;
        }
    }
}
