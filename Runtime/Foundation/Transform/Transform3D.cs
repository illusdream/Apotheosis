#nullable enable

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Foundation.Transform
{
    /// <summary>
    /// 轻量级全 3D 变换值类型（基于 XNA/FNA 内置 <see cref="Vector3"/>、<see cref="Quaternion"/> 与 <see cref="Matrix"/>）。
    /// <para>
    /// 坐标系约定（Terraria 兼容右手系）：<c>+X</c> 向右，<c>+Y</c> 向下，<c>+Z</c> 指向屏幕内。
    /// 绕 <c>+Z</c> 轴正旋转与 Terraria 原版 2D 顺时针旋转完全一致；矩阵采用 XNA 行向量顺序 <c>S * R * T</c>。
    /// </para>
    /// </summary>
    public struct Transform3D : IEquatable<Transform3D>
    {
        private const float Epsilon = 1e-6f;

        /// <summary>单位变换：位置为零、无旋转、缩放为 (1, 1, 1)。</summary>
        public static readonly Transform3D Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

        /// <summary>3D 位置（XY 对应 Terraria 世界坐标）。</summary>
        public Vector3 Position;

        /// <summary>3D 旋转四元数。</summary>
        public Quaternion Rotation;

        /// <summary>3D 缩放（允许负值，例如 <c>Scale.X &lt; 0</c> 表达水平翻转，<c>Scale.Y &lt; 0</c> 表达垂直/重力倒转）。</summary>
        public Vector3 Scale;

        /// <summary>构造默认单位变换。</summary>
        public Transform3D()
        {
            Position = Vector3.Zero;
            Rotation = Quaternion.Identity;
            Scale = Vector3.One;
        }

        /// <summary>使用完整 3D 位置、旋转与缩放构造变换。</summary>
        public Transform3D(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Position = position;
            Rotation = rotation;
            Scale = scale;
        }

        /// <summary>使用 3D 位置与旋转构造变换（默认缩放为 1）。</summary>
        public Transform3D(Vector3 position, Quaternion rotation)
            : this(position, rotation, Vector3.One)
        {
        }

        /// <summary>仅使用 3D 位置构造变换。</summary>
        public Transform3D(Vector3 position)
            : this(position, Quaternion.Identity, Vector3.One)
        {
        }

        /// <summary>从 2D 参数构造 3D 变换（Z 位置为 0，Z 缩放为 1，旋转绕 +Z 轴顺时针）。</summary>
        public static Transform3D From2D(Vector2 position, float rotationZ = 0f, Vector2? scale = null, float depthZ = 0f)
        {
            Vector2 s = scale ?? Vector2.One;
            Quaternion q = MathF.Abs(rotationZ) > Epsilon
                ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rotationZ)
                : Quaternion.Identity;
            return new Transform3D(new Vector3(position, depthZ), q, new Vector3(s, 1f));
        }

        /// <summary>从 2D 参数与均匀缩放构造 3D 变换。</summary>
        public static Transform3D From2D(Vector2 position, float rotationZ, float uniformScale, float depthZ = 0f)
            => From2D(position, rotationZ, new Vector2(uniformScale, uniformScale), depthZ);

        /// <summary>
        /// 按固定顺序（局部先绕 X、再绕 Y、最后绕 Z，对应矩阵 <c>Rx * Ry * Rz</c>）从欧拉角（弧度）构造四元数。
        /// </summary>
        public static Quaternion CreateRotationFromEuler(Vector3 eulerRadians)
        {
            Quaternion qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, eulerRadians.X);
            Quaternion qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, eulerRadians.Y);
            Quaternion qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, eulerRadians.Z);
            // XNA 行向量约定下 M(q2 * q1) = M(q1) * M(q2)，因此 Qz * Qy * Qx 等价于 Rx * Ry * Rz
            return Quaternion.Normalize(qz * qy * qx);
        }

        /// <summary>从位置、欧拉角（弧度）与缩放构造 3D 变换。</summary>
        public static Transform3D FromEuler(Vector3 position, Vector3 eulerRadians, Vector3? scale = null)
            => new(position, CreateRotationFromEuler(eulerRadians), scale ?? Vector3.One);

        /// <summary>
        /// 将四元数解算为与 <see cref="CreateRotationFromEuler"/> 一致的欧拉角（弧度，X=Pitch, Y=Yaw, Z=Roll）。
        /// </summary>
        public static Vector3 ToEulerAngles(Quaternion rotation)
        {
            double qx = rotation.X;
            double qy = rotation.Y;
            double qz = rotation.Z;
            double qw = rotation.W;
            double lenSq = qx * qx + qy * qy + qz * qz + qw * qw;
            if (lenSq <= 1e-12)
                return Vector3.Zero;

            double invLen = 1.0 / Math.Sqrt(lenSq);
            qx *= invLen;
            qy *= invLen;
            qz *= invLen;
            qw *= invLen;

            // 使用 double 直接由四元数展开矩阵元素，避免近万向节死锁时 float32 有效位数丢失
            double xx = qx * qx;
            double yy = qy * qy;
            double zz = qz * qz;
            double xy = qx * qy;
            double xz = qx * qz;
            double yz = qy * qz;
            double xw = qx * qw;
            double yw = qy * qw;
            double zw = qz * qw;

            double m11 = 1.0 - 2.0 * (yy + zz);
            double m12 = 2.0 * (xy + zw);
            double m13 = 2.0 * (xz - yw);
            double m22 = 1.0 - 2.0 * (xx + zz);
            double m23 = 2.0 * (yz + xw);
            double m32 = 2.0 * (yz - xw);
            double m33 = 1.0 - 2.0 * (xx + yy);

            double sinY = Math.Clamp(-m13, -1.0, 1.0);
            double cosY = Math.Sqrt(m11 * m11 + m12 * m12);

            double x;
            double y;
            double z;
            if (cosY > 1e-9)
            {
                y = Math.Atan2(sinY, cosY);
                x = Math.Atan2(m23, m33);
                z = Math.Atan2(m12, m11);
            }
            else
            {
                // 万向节死锁分支：精确钳位 Y = ±π/2，固定 Z=0，由 M32=-sin(x)、M22=cos(x) 还原 X
                y = Math.CopySign(Math.PI * 0.5, sinY);
                x = Math.Atan2(-m32, m22);
                z = 0.0;
            }

            return new Vector3((float)x, (float)y, (float)z);
        }

        /// <summary>XY 平面 2D 位置（保留原有 Z）。</summary>
        public Vector2 Position2D
        {
            readonly get => new(Position.X, Position.Y);
            set => Position = new Vector3(value.X, value.Y, Position.Z);
        }

        /// <summary>XY 平面 2D 缩放（保留原有 Z 缩放）。</summary>
        public Vector2 Scale2D
        {
            readonly get => new(Scale.X, Scale.Y);
            set => Scale = new Vector3(value.X, value.Y, Scale.Z);
        }

        /// <summary>欧拉角（弧度，X/Y/Z 顺序）。</summary>
        public Vector3 EulerAngles
        {
            readonly get => ToEulerAngles(Rotation);
            set => Rotation = CreateRotationFromEuler(value);
        }

        /// <summary>
        /// 绕 +Z 轴的 2D 旋转弧度（读取时取欧拉角 Z 分量；写入时将旋转设为纯绕 Z 轴旋转）。
        /// </summary>
        public float RotationZ
        {
            readonly get => ToEulerAngles(Rotation).Z;
            set => Rotation = MathF.Abs(value) > Epsilon
                ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, value)
                : Quaternion.Identity;
        }

        /// <summary>是否通过负 <c>Scale.X</c> 处于水平翻转态。</summary>
        public readonly bool IsFlippedX => Scale.X < 0f;

        /// <summary>是否通过负 <c>Scale.Y</c> 处于垂直翻转态（例如原版重力药剂倒转）。</summary>
        public readonly bool IsFlippedY => Scale.Y < 0f;

        /// <summary>局部 +X 轴经当前旋转后的单位方向（右向）。</summary>
        public readonly Vector3 Right => Vector3.Transform(Vector3.UnitX, NormalizeOrIdentity(Rotation));

        /// <summary>局部 +Y 轴经当前旋转后的单位方向（Terraria 坐标系下为下向）。</summary>
        public readonly Vector3 Down => Vector3.Transform(Vector3.UnitY, NormalizeOrIdentity(Rotation));

        /// <summary>局部 +Z 轴经当前旋转后的单位方向（指向屏幕内）。</summary>
        public readonly Vector3 Forward => Vector3.Transform(Vector3.UnitZ, NormalizeOrIdentity(Rotation));

        /// <summary>严格仅修改 <see cref="Scale"/> 的 X 符号以表达水平翻转（保留绝对值，不改动旋转）。</summary>
        public void SetFlipX(bool flipped)
        {
            float absX = MathF.Abs(Scale.X);
            Scale.X = flipped ? -absX : absX;
        }

        /// <summary>严格仅修改 <see cref="Scale"/> 的 Y 符号以表达垂直翻转（保留绝对值，不改动旋转）。</summary>
        public void SetFlipY(bool flipped)
        {
            float absY = MathF.Abs(Scale.Y);
            Scale.Y = flipped ? -absY : absY;
        }

        /// <summary>合成为 XNA 行向量变换矩阵 <c>Scale * Rotation * Translation</c>。</summary>
        public readonly Matrix ToMatrix()
        {
            Quaternion q = NormalizeOrIdentity(Rotation);
            return Matrix.CreateScale(Scale)
                 * Matrix.CreateFromQuaternion(q)
                 * Matrix.CreateTranslation(Position);
        }

        /// <summary>将局部空间点变换到目标空间（应用缩放、旋转、平移）。</summary>
        public readonly Vector3 TransformPoint(Vector3 localPoint)
        {
            Vector3 scaled = localPoint * Scale;
            Vector3 rotated = Vector3.Transform(scaled, NormalizeOrIdentity(Rotation));
            return rotated + Position;
        }

        /// <summary>将局部空间向量变换到目标空间（应用缩放与旋转，不含平移）。</summary>
        public readonly Vector3 TransformVector(Vector3 localVector)
        {
            Vector3 scaled = localVector * Scale;
            return Vector3.Transform(scaled, NormalizeOrIdentity(Rotation));
        }

        /// <summary>仅使用当前旋转变换方向向量（忽略缩放与平移）。</summary>
        public readonly Vector3 TransformDirection(Vector3 localDirection)
            => Vector3.Transform(localDirection, NormalizeOrIdentity(Rotation));

        /// <summary>将目标空间点逆变换回局部空间。</summary>
        public readonly Vector3 InverseTransformPoint(Vector3 point)
        {
            Quaternion invRot = Quaternion.Inverse(NormalizeOrIdentity(Rotation));
            Vector3 unrotated = Vector3.Transform(point - Position, invRot);
            return new Vector3(
                SafeDivide(unrotated.X, Scale.X),
                SafeDivide(unrotated.Y, Scale.Y),
                SafeDivide(unrotated.Z, Scale.Z));
        }

        /// <summary>组合父变换与子变换（等价于矩阵 <c>child.ToMatrix() * parent.ToMatrix()</c> 的分解结果）。</summary>
        public static Transform3D Combine(in Transform3D parent, in Transform3D child)
        {
            Matrix combined = child.ToMatrix() * parent.ToMatrix();
            return DecomposeMatrix(in combined);
        }

        /// <summary>在两个变换间插值（位置与缩放线性插值，旋转采用球面线性插值 <see cref="Quaternion.Slerp"/>）。</summary>
        public static Transform3D Lerp(in Transform3D a, in Transform3D b, float amount)
        {
            return new Transform3D(
                Vector3.Lerp(a.Position, b.Position, amount),
                Quaternion.Slerp(NormalizeOrIdentity(a.Rotation), NormalizeOrIdentity(b.Rotation), amount),
                Vector3.Lerp(a.Scale, b.Scale, amount));
        }

        /// <summary>将当前 3D 变换投影为 2D 渲染参数。</summary>
        public readonly SpriteTransform2D ToSprite2D(bool preferHorizontalFlip = false)
        {
            Matrix m = ToMatrix();
            return ExtractSprite2D(in m, preferHorizontalFlip);
        }

        /// <summary>
        /// 从任意仿射变换矩阵安全分解出 <see cref="Transform3D"/>。
        /// <para>相较于 XNA 原生 <see cref="Matrix.Decompose"/>，本方法能正确处理负缩放（镜像翻转）及零缩放退化情况。</para>
        /// </summary>
        public static Transform3D DecomposeMatrix(in Matrix matrix)
        {
            Vector3 position = matrix.Translation;

            Vector3 rowX = new(matrix.M11, matrix.M12, matrix.M13);
            Vector3 rowY = new(matrix.M21, matrix.M22, matrix.M23);
            Vector3 rowZ = new(matrix.M31, matrix.M32, matrix.M33);

            float sx = rowX.Length();
            float sy = rowY.Length();
            float sz = rowZ.Length();

            float det = Vector3.Dot(Vector3.Cross(rowX, rowY), rowZ);
            if (det < 0f)
            {
                sx = -sx;
            }

            Vector3 basisX;
            Vector3 basisY;
            Vector3 basisZ;

            if (MathF.Abs(sx) <= Epsilon && MathF.Abs(sy) > Epsilon && MathF.Abs(sz) > Epsilon)
            {
                // X 轴零缩放退化但 Y/Z 轴有效：优先利用未退化的 Y/Z 轴重建右手正交基，保留有效旋转
                basisY = Vector3.Normalize(rowY / sy);
                Vector3 rawZ = rowZ / sz;
                Vector3 orthoZ = rawZ - basisY * Vector3.Dot(rawZ, basisY);
                if (orthoZ.LengthSquared() <= Epsilon * Epsilon)
                {
                    Vector3 fallbackAxis = MathF.Abs(basisY.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
                    orthoZ = fallbackAxis - basisY * Vector3.Dot(fallbackAxis, basisY);
                }

                basisZ = Vector3.Normalize(orthoZ);
                basisX = Vector3.Normalize(Vector3.Cross(basisY, basisZ));
            }
            else
            {
                basisX = MathF.Abs(sx) > Epsilon ? rowX / sx : Vector3.UnitX;
                basisY = MathF.Abs(sy) > Epsilon ? rowY / sy : Vector3.UnitY;

                basisX = NormalizeOrFallback(basisX, Vector3.UnitX);

                Vector3 orthoY = basisY - basisX * Vector3.Dot(basisY, basisX);
                if (orthoY.LengthSquared() <= Epsilon * Epsilon)
                {
                    // 退化兜底：动态选择与 basisX 不平行的轴再正交化，保证旋转矩阵严格正交
                    Vector3 fallbackAxis = MathF.Abs(basisX.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
                    orthoY = fallbackAxis - basisX * Vector3.Dot(fallbackAxis, basisX);
                }

                basisY = Vector3.Normalize(orthoY);
                basisZ = Vector3.Normalize(Vector3.Cross(basisX, basisY));
            }

            Matrix rotMatrix = new(
                basisX.X, basisX.Y, basisX.Z, 0f,
                basisY.X, basisY.Y, basisY.Z, 0f,
                basisZ.X, basisZ.Y, basisZ.Z, 0f,
                0f, 0f, 0f, 1f);

            Quaternion rotation = NormalizeOrIdentity(Quaternion.CreateFromRotationMatrix(rotMatrix));
            return new Transform3D(position, rotation, new Vector3(sx, sy, sz));
        }

        /// <summary>从世界变换矩阵投影出 2D 渲染参数（<see cref="SpriteTransform2D"/>）。</summary>
        public static SpriteTransform2D ExtractSprite2D(in Matrix worldMatrix, bool preferHorizontalFlip = false)
        {
            Vector2 pos = new(worldMatrix.M41, worldMatrix.M42);
            float depthZ = worldMatrix.M43;

            Vector2 ux = new(worldMatrix.M11, worldMatrix.M12);
            Vector2 uy = new(worldMatrix.M21, worldMatrix.M22);

            float scaleX = ux.Length();
            float scaleY = uy.Length();
            float det2D = ux.X * uy.Y - ux.Y * uy.X;

            if (det2D >= 0f)
            {
                float rotation = scaleX > Epsilon
                    ? MathF.Atan2(ux.Y, ux.X)
                    : (scaleY > Epsilon ? MathF.Atan2(-uy.X, uy.Y) : 0f);
                return new SpriteTransform2D(pos, rotation, new Vector2(scaleX, scaleY), SpriteEffects.None, depthZ, isBackFacing: false);
            }

            bool useHorizontalFlip = preferHorizontalFlip || ux.X <= 0f;
            if (useHorizontalFlip)
            {
                float rotation = scaleX > Epsilon
                    ? MathF.Atan2(-ux.Y, -ux.X)
                    : (scaleY > Epsilon ? MathF.Atan2(-uy.X, uy.Y) : 0f);
                return new SpriteTransform2D(pos, rotation, new Vector2(scaleX, scaleY), SpriteEffects.FlipHorizontally, depthZ, isBackFacing: true);
            }
            else
            {
                float rotation = scaleX > Epsilon
                    ? MathF.Atan2(ux.Y, ux.X)
                    : (scaleY > Epsilon ? MathF.Atan2(uy.X, -uy.Y) : 0f);
                return new SpriteTransform2D(pos, rotation, new Vector2(scaleX, scaleY), SpriteEffects.FlipVertically, depthZ, isBackFacing: true);
            }
        }

        /// <summary>
        /// 计算四元数旋转在反射矩阵 <c>F = diag(signX, signY, signZ)</c> 下的共轭旋转（对应矩阵 <c>F * M(q) * F</c>）。
        /// </summary>
        internal static Quaternion ConjugateBySign(Quaternion q, Vector3 sign)
        {
            Quaternion n = NormalizeOrIdentity(q);
            return new Quaternion(
                sign.Y * sign.Z * n.X,
                sign.X * sign.Z * n.Y,
                sign.X * sign.Y * n.Z,
                n.W);
        }

        internal static Vector3 ExtractScaleSign(Vector3 scale)
            => new(scale.X < 0f ? -1f : 1f, scale.Y < 0f ? -1f : 1f, scale.Z < 0f ? -1f : 1f);

        internal static Quaternion NormalizeOrIdentity(Quaternion q)
        {
            float lenSq = q.LengthSquared();
            if (lenSq <= Epsilon * Epsilon)
                return Quaternion.Identity;

            return MathF.Abs(lenSq - 1f) <= 1e-5f ? q : Quaternion.Normalize(q);
        }

        private static Vector3 NormalizeOrFallback(Vector3 v, Vector3 fallback)
        {
            float lenSq = v.LengthSquared();
            if (lenSq <= Epsilon * Epsilon)
                return fallback;

            return v / MathF.Sqrt(lenSq);
        }

        private static float SafeDivide(float numerator, float denominator)
            => MathF.Abs(denominator) > Epsilon ? numerator / denominator : 0f;

        public readonly bool Equals(Transform3D other)
            => Position.Equals(other.Position)
            && Rotation.Equals(other.Rotation)
            && Scale.Equals(other.Scale);

        public readonly override bool Equals(object? obj)
            => obj is Transform3D other && Equals(other);

        public readonly override int GetHashCode()
            => HashCode.Combine(Position, Rotation, Scale);

        public static bool operator ==(Transform3D left, Transform3D right) => left.Equals(right);
        public static bool operator !=(Transform3D left, Transform3D right) => !left.Equals(right);

        public readonly override string ToString()
            => $"Transform3D(Pos={Position}, Rot={Rotation}, Scale={Scale})";
    }
}
