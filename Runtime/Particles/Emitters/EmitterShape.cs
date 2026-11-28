#nullable enable

using System;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Emitters
{
    /// <summary>通用发射器采样几何形状分类。</summary>
    public enum EmitterShapeKind : byte
    {
        Point = 0,
        Circle = 1,
        Ring = 2,
        Box2D = 3,
        Box3D = 4,
        Sphere = 5,
        Cone2D = 6,
        Cone3D = 7,
        Line = 8,
    }

    /// <summary>不可变粒子发射形状描述（值类型，支持点、圆、环、2D/3D 盒、球、2D/3D 锥与线段采样）。</summary>
    public readonly struct EmitterShape
    {
        /// <summary>形状类型。</summary>
        public readonly EmitterShapeKind Kind;

        /// <summary>相对发射原点的局部偏移（线段模式下为起点）。</summary>
        public readonly Vector3 Offset;

        /// <summary>辅助三维参数（盒状为半尺寸，3D 锥为轴向，线段为终点）。</summary>
        public readonly Vector3 VectorParam;

        /// <summary>主半径（圆、环外径、球半径、锥底半径）。</summary>
        public readonly float Radius;

        /// <summary>次级标量参数（环内径、2D 锥中心弧度）。</summary>
        public readonly float SecondaryParam;

        /// <summary>锥形半张角弧度（2D/3D 锥适用）。</summary>
        public readonly float HalfSpreadRad;

        /// <summary>是否仅从边缘或表面发射（圆/球适用）。</summary>
        public readonly bool EmitFromEdge;

        private EmitterShape(EmitterShapeKind kind, Vector3 offset, Vector3 vectorParam, float radius, float secondaryParam, float halfSpreadRad, bool emitFromEdge)
        {
            Kind = kind;
            Offset = offset;
            VectorParam = vectorParam;
            Radius = radius;
            SecondaryParam = secondaryParam;
            HalfSpreadRad = halfSpreadRad;
            EmitFromEdge = emitFromEdge;
        }

        /// <summary>单点发射（默认 2D 平面全向或 3D 球面全向）。</summary>
        public static EmitterShape Point(Vector3 offset = default)
            => new(EmitterShapeKind.Point, offset, Vector3.Zero, 0f, 0f, 0f, false);

        /// <summary>2D 圆形区域发射（默认沿径向向外，可指定仅从圆周发射）。</summary>
        public static EmitterShape Circle(float radius, bool emitFromEdge = false, Vector3 offset = default)
            => new(EmitterShapeKind.Circle, offset, Vector3.Zero, SanitizeNonNegative(radius), 0f, 0f, emitFromEdge);

        /// <summary>2D 圆环区域按面积均匀发射（沿径向向外）。</summary>
        public static EmitterShape Ring(float innerRadius, float outerRadius, Vector3 offset = default)
            => new(EmitterShapeKind.Ring, offset, Vector3.Zero, SanitizeNonNegative(outerRadius), SanitizeNonNegative(innerRadius), 0f, false);

        /// <summary>2D 轴对齐矩形发射（半宽高为 <paramref name="halfExtents"/>）。</summary>
        public static EmitterShape Box2D(Vector2 halfExtents, Vector3 offset = default)
            => new(EmitterShapeKind.Box2D, offset, new Vector3(SanitizeNonNegative(MathF.Abs(halfExtents.X)), SanitizeNonNegative(MathF.Abs(halfExtents.Y)), 0f), 0f, 0f, 0f, false);

        /// <summary>3D 轴对齐长方体发射（半尺寸为 <paramref name="halfExtents"/>）。</summary>
        public static EmitterShape Box3D(Vector3 halfExtents, Vector3 offset = default)
            => new(EmitterShapeKind.Box3D, offset, new Vector3(SanitizeNonNegative(MathF.Abs(halfExtents.X)), SanitizeNonNegative(MathF.Abs(halfExtents.Y)), SanitizeNonNegative(MathF.Abs(halfExtents.Z))), 0f, 0f, 0f, false);

        /// <summary>3D 球体发射（默认沿径向向外，可指定仅从球面发射）。</summary>
        public static EmitterShape Sphere(float radius, bool emitFromSurface = false, Vector3 offset = default)
            => new(EmitterShapeKind.Sphere, offset, Vector3.Zero, SanitizeNonNegative(radius), 0f, 0f, emitFromSurface);

        /// <summary>2D 扇形锥发射（可选底面发射圆半径 <paramref name="baseRadius"/>）。</summary>
        public static EmitterShape Cone2D(float centerAngleRad, float halfSpreadRad, float baseRadius = 0f, Vector3 offset = default)
            => new(EmitterShapeKind.Cone2D, offset, Vector3.Zero, SanitizeNonNegative(baseRadius), float.IsFinite(centerAngleRad) ? centerAngleRad : 0f, SanitizeNonNegative(MathF.Abs(halfSpreadRad)), false);

        /// <summary>3D 圆锥发射（沿 <paramref name="axis"/> 轴向按立体角均匀分布，可选垂直轴向的底面圆盘半径 <paramref name="baseRadius"/>）。</summary>
        public static EmitterShape Cone3D(Vector3 axis, float halfSpreadRad, float baseRadius = 0f, Vector3 offset = default)
            => new(EmitterShapeKind.Cone3D, offset, axis, SanitizeNonNegative(baseRadius), 0f, SanitizeNonNegative(MathF.Abs(halfSpreadRad)), false);

        /// <summary>3D 线段均匀发射。</summary>
        public static EmitterShape Line(Vector3 start, Vector3 end)
            => new(EmitterShapeKind.Line, start, end, 0f, 0f, 0f, false);

        /// <summary>2D 线段均匀发射。</summary>
        public static EmitterShape Line2D(Vector2 start, Vector2 end, float depthZ = 0f)
            => new(EmitterShapeKind.Line, new Vector3(start, depthZ), new Vector3(end, depthZ), 0f, 0f, 0f, false);

        /// <summary>采样一对局部位置偏移与初始单位方向向量。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Sample(ref ParticleRandom rng, bool prefer3DDirection, out Vector3 localPosition, out Vector3 localDirection)
        {
            switch (Kind)
            {
                case EmitterShapeKind.Circle:
                {
                    Vector2 p = rng.NextPointInCircle(Radius, EmitFromEdge);
                    localPosition = new Vector3(Offset.X + p.X, Offset.Y + p.Y, Offset.Z);
                    localDirection = NormalizeDirection2D(p, ref rng);
                    return;
                }

                case EmitterShapeKind.Ring:
                {
                    Vector2 p = rng.NextPointInRing(SecondaryParam, Radius);
                    localPosition = new Vector3(Offset.X + p.X, Offset.Y + p.Y, Offset.Z);
                    localDirection = NormalizeDirection2D(p, ref rng);
                    return;
                }

                case EmitterShapeKind.Box2D:
                {
                    localPosition = new Vector3(
                        Offset.X + rng.NextSignedFloat() * VectorParam.X,
                        Offset.Y + rng.NextSignedFloat() * VectorParam.Y,
                        Offset.Z);
                    localDirection = prefer3DDirection ? rng.NextUnitVector3() : new Vector3(rng.NextUnitVector2(), 0f);
                    return;
                }

                case EmitterShapeKind.Box3D:
                {
                    localPosition = Offset + rng.NextPointInBox(VectorParam);
                    localDirection = rng.NextUnitVector3();
                    return;
                }

                case EmitterShapeKind.Sphere:
                {
                    Vector3 p = rng.NextPointInSphere(Radius, EmitFromEdge);
                    localPosition = Offset + p;
                    localDirection = NormalizeDirection3D(p, ref rng);
                    return;
                }

                case EmitterShapeKind.Cone2D:
                {
                    Vector2 baseOff = Radius > 0f ? rng.NextPointInCircle(Radius) : Vector2.Zero;
                    localPosition = new Vector3(Offset.X + baseOff.X, Offset.Y + baseOff.Y, Offset.Z);
                    localDirection = new Vector3(rng.NextDirectionInCone2D(SecondaryParam, HalfSpreadRad), 0f);
                    return;
                }

                case EmitterShapeKind.Cone3D:
                {
                    Vector3 baseOff = Vector3.Zero;
                    if (Radius > 0f && float.IsFinite(Radius))
                    {
                        // 在垂直于锥轴的底面圆盘内按面积均匀采样偏移
                        float axisLenSq = VectorParam.LengthSquared();
                        Vector3 forward = float.IsFinite(axisLenSq) && axisLenSq > 1e-12f ? VectorParam / MathF.Sqrt(axisLenSq) : new Vector3(0f, -1f, 0f);
                        Vector3 refAxis = MathF.Abs(forward.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
                        Vector3 tangent = Vector3.Normalize(Vector3.Cross(refAxis, forward));
                        Vector3 bitangent = Vector3.Cross(forward, tangent);
                        Vector2 disk = rng.NextPointInCircle(Radius);
                        baseOff = tangent * disk.X + bitangent * disk.Y;
                    }

                    localPosition = Offset + baseOff;
                    localDirection = rng.NextDirectionInCone3D(VectorParam, HalfSpreadRad);
                    return;
                }

                case EmitterShapeKind.Line:
                {
                    localPosition = Vector3.Lerp(Offset, VectorParam, rng.NextFloat());
                    localDirection = prefer3DDirection ? rng.NextUnitVector3() : new Vector3(rng.NextUnitVector2(), 0f);
                    return;
                }

                default:
                {
                    localPosition = Offset;
                    localDirection = prefer3DDirection ? rng.NextUnitVector3() : new Vector3(rng.NextUnitVector2(), 0f);
                    return;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float SanitizeNonNegative(float value) => float.IsFinite(value) && value > 0f ? value : 0f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 NormalizeDirection2D(Vector2 p, ref ParticleRandom rng)
        {
            float lenSq = p.LengthSquared();
            if (float.IsFinite(lenSq) && lenSq > 1e-10f)
            {
                float invLen = 1f / MathF.Sqrt(lenSq);
                return new Vector3(p.X * invLen, p.Y * invLen, 0f);
            }

            // 超大有限半径下平方会溢出为 +Inf，先按最大分量缩放再归一化避免得出零向量
            if (float.IsInfinity(lenSq))
            {
                float maxAbs = MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y));
                if (float.IsFinite(maxAbs) && maxAbs > 0f)
                {
                    Vector2 scaled = p / maxAbs;
                    float sLenSq = scaled.LengthSquared();
                    if (sLenSq > 0f)
                    {
                        float invLen = 1f / MathF.Sqrt(sLenSq);
                        return new Vector3(scaled.X * invLen, scaled.Y * invLen, 0f);
                    }
                }
            }

            return new Vector3(rng.NextUnitVector2(), 0f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 NormalizeDirection3D(Vector3 p, ref ParticleRandom rng)
        {
            float lenSq = p.LengthSquared();
            if (float.IsFinite(lenSq) && lenSq > 1e-10f)
                return p / MathF.Sqrt(lenSq);

            if (float.IsInfinity(lenSq))
            {
                float maxAbs = MathF.Max(MathF.Abs(p.X), MathF.Max(MathF.Abs(p.Y), MathF.Abs(p.Z)));
                if (float.IsFinite(maxAbs) && maxAbs > 0f)
                {
                    Vector3 scaled = p / maxAbs;
                    float sLenSq = scaled.LengthSquared();
                    if (sLenSq > 0f)
                        return scaled / MathF.Sqrt(sLenSq);
                }
            }

            return rng.NextUnitVector3();
        }
    }
}
