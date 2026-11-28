#nullable enable

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Emitters
{
    /// <summary>零堆分配的快速确定性伪随机数生成器（SplitMix64 混种 + XorShift64* 步进），专供粒子工作线程发射器使用。</summary>
    public struct ParticleRandom
    {
        private const float Inv24Bit = 1f / 16777216f; // 2^-24
        private static long _seedCounter;

        private ulong _state;

        /// <summary>使用指定种子初始化伪随机数状态（零种子亦会经 SplitMix64 混淆为非零状态）。</summary>
        public ParticleRandom(ulong seed)
        {
            _state = MixSeed(seed);
        }

        /// <summary>生成进程内单调不重复的随机种子（结合原子计数与高精度时间戳，避免同毫秒并发碰撞）。</summary>
        public static ulong NextSeed()
        {
            ulong seq = (ulong)Interlocked.Increment(ref _seedCounter);
            ulong ticks = (ulong)Stopwatch.GetTimestamp();
            return MixSeed(ticks ^ (seq * 0x9E3779B97F4A7C15UL));
        }

        /// <summary>生成下一个 64 位无符号随机整数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong NextUInt64()
        {
            ulong x = _state;
            if (x == 0)
                x = 0x9E3779B97F4A7C15UL;

            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            _state = x;
            return x * 0x2545F4914F6CDD1DUL;
        }

        /// <summary>生成下一个 32 位无符号随机整数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint NextUInt32() => (uint)(NextUInt64() >> 32);

        /// <summary>生成区间 <c>[0, 1)</c> 内的均匀随机单精度浮点数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float NextFloat() => (NextUInt64() >> 40) * Inv24Bit;

        /// <summary>生成区间 <c>[min, max)</c> 内的均匀随机单精度浮点数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float NextFloat(float min, float max) => min + (max - min) * NextFloat();

        /// <summary>生成区间 <c>[-1, 1)</c> 内的均匀随机单精度浮点数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float NextSignedFloat() => NextFloat() * 2f - 1f;

        /// <summary>生成区间 <c>[minInclusive, maxExclusive)</c> 内的均匀随机整数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
                return minInclusive;

            ulong range = (ulong)((long)maxExclusive - minInclusive);
            return (int)((long)minInclusive + (long)(NextUInt64() % range));
        }

        /// <summary>生成闭区间 <c>[minInclusive, maxInclusive]</c> 内的均匀随机整数（无 <c>int.MaxValue + 1</c> 溢出风险）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int NextIntInclusive(int minInclusive, int maxInclusive)
        {
            if (maxInclusive <= minInclusive)
                return minInclusive;

            ulong span = (ulong)((long)maxInclusive - minInclusive);
            if (span == ulong.MaxValue)
                return unchecked((int)NextUInt64());

            ulong range = span + 1UL;
            return (int)((long)minInclusive + (long)(NextUInt64() % range));
        }

        /// <summary>生成区间 <c>[0, 2π)</c> 内的均匀随机弧度角。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float NextAngle() => NextFloat() * MathF.Tau;

        /// <summary>按指定概率 <paramref name="probability"/> 返回布尔结果。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool NextBool(float probability = 0.5f) => NextFloat() < probability;

        /// <summary>在 2D 单位圆周上均匀采样方向向量。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2 NextUnitVector2()
        {
            (float sin, float cos) = MathF.SinCos(NextAngle());
            return new Vector2(cos, sin);
        }

        /// <summary>在 2D 圆形区域内均匀采样位置（<paramref name="onEdge"/> 为真时仅在圆周采样）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2 NextPointInCircle(float radius, bool onEdge = false)
        {
            if (!float.IsFinite(radius) || !(radius > 0f))
                return Vector2.Zero;

            float r = onEdge ? radius : radius * MathF.Sqrt(NextFloat());
            (float sin, float cos) = MathF.SinCos(NextAngle());
            return new Vector2(cos * r, sin * r);
        }

        /// <summary>在 2D 圆环区域内按面积均匀采样位置。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2 NextPointInRing(float innerRadius, float outerRadius)
        {
            float a = float.IsFinite(innerRadius) && innerRadius > 0f ? innerRadius : 0f;
            float b = float.IsFinite(outerRadius) && outerRadius > 0f ? outerRadius : 0f;
            float rMin = MathF.Min(a, b);
            float rMax = MathF.Max(a, b);
            if (!(rMax > 0f))
                return Vector2.Zero;

            // 先按 rMax 归一化比例再开方，杜绝大半径下 rMax*rMax 溢出为 +Inf 导致 Inf - Inf = NaN
            float ratio = rMin / rMax;
            float r = rMax * MathF.Sqrt(ratio * ratio + (1f - ratio * ratio) * NextFloat());
            (float sin, float cos) = MathF.SinCos(NextAngle());
            return new Vector2(cos * r, sin * r);
        }

        /// <summary>在 2D 扇形锥角内均匀采样单位方向向量。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2 NextDirectionInCone2D(float centerAngleRad, float halfSpreadRad)
        {
            float center = float.IsFinite(centerAngleRad) ? centerAngleRad : 0f;
            float spread = float.IsFinite(halfSpreadRad) ? MathF.Abs(halfSpreadRad) : 0f;
            float angle = center + NextSignedFloat() * spread;
            (float sin, float cos) = MathF.SinCos(angle);
            return new Vector2(cos, sin);
        }

        /// <summary>在 3D 单位球面上按立体角均匀采样方向向量。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 NextUnitVector3()
        {
            float z = NextFloat() * 2f - 1f;
            float radial = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
            (float sin, float cos) = MathF.SinCos(NextAngle());
            return new Vector3(radial * cos, radial * sin, z);
        }

        /// <summary>在 3D 球体内按体积均匀采样位置（<paramref name="onSurface"/> 为真时仅在球面采样）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 NextPointInSphere(float radius, bool onSurface = false)
        {
            if (!float.IsFinite(radius) || !(radius > 0f))
                return Vector3.Zero;

            float r = onSurface ? radius : radius * MathF.Cbrt(NextFloat());
            return NextUnitVector3() * r;
        }

        /// <summary>在 3D 圆锥内按立体角均匀采样单位方向向量。</summary>
        public Vector3 NextDirectionInCone3D(Vector3 axis, float halfSpreadRad)
        {
            float axisLenSq = axis.LengthSquared();
            Vector3 forward = float.IsFinite(axisLenSq) && axisLenSq > 1e-12f ? axis / MathF.Sqrt(axisLenSq) : new Vector3(0f, -1f, 0f);
            if (!float.IsFinite(halfSpreadRad))
                return forward;

            float spread = Math.Clamp(MathF.Abs(halfSpreadRad), 0f, MathF.PI);
            if (!(spread > 1e-5f))
                return forward;

            // 在 [cos(spread), 1] 内均匀采样 cosTheta 以保证圆锥内立体角均匀分布
            float cosMax = MathF.Cos(spread);
            float cosTheta = cosMax + (1f - cosMax) * NextFloat();
            float sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));
            (float sinPhi, float cosPhi) = MathF.SinCos(NextAngle());

            // 构造与 forward 正交的局部切线基底 (tangent, bitangent)
            Vector3 refAxis = MathF.Abs(forward.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(refAxis, forward));
            Vector3 bitangent = Vector3.Cross(forward, tangent);

            return Vector3.Normalize(forward * cosTheta + (tangent * cosPhi + bitangent * sinPhi) * sinTheta);
        }

        /// <summary>在轴对齐包围盒 <c>[-halfExtents, +halfExtents]</c> 内均匀采样局部位置。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 NextPointInBox(Vector3 halfExtents)
            => new(NextSignedFloat() * halfExtents.X, NextSignedFloat() * halfExtents.Y, NextSignedFloat() * halfExtents.Z);

        /// <summary>按各分量独立均匀采样区间 <c>[min, max]</c> 内的 2D 向量。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2 NextVector2(Vector2 min, Vector2 max)
            => new(NextFloat(min.X, max.X), NextFloat(min.Y, max.Y));

        /// <summary>按各分量独立均匀采样区间 <c>[min, max]</c> 内的 3D 向量。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 NextVector3(Vector3 min, Vector3 max)
            => new(NextFloat(min.X, max.X), NextFloat(min.Y, max.Y), NextFloat(min.Z, max.Z));

        /// <summary>在两个 HDR 颜色之间均匀随机插值。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector4 NextColorLerp(Vector4 a, Vector4 b) => Vector4.Lerp(a, b, NextFloat());

        /// <summary>按 Shoemake 算法在 SO(3) 上均匀采样随机 3D 旋转四元数。</summary>
        public Quaternion NextQuaternion()
        {
            float u1 = NextFloat();
            float u2 = NextAngle();
            float u3 = NextAngle();
            float sqrt1MinusU1 = MathF.Sqrt(MathF.Max(0f, 1f - u1));
            float sqrtU1 = MathF.Sqrt(MathF.Max(0f, u1));
            (float sin2, float cos2) = MathF.SinCos(u2);
            (float sin3, float cos3) = MathF.SinCos(u3);
            return new Quaternion(sqrt1MinusU1 * sin2, sqrt1MinusU1 * cos2, sqrtU1 * sin3, sqrtU1 * cos3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MixSeed(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return z != 0 ? z : 0x9E3779B97F4A7C15UL;
        }
    }
}
