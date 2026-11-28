#nullable enable

using System;
using Apotheosis.Runtime.Foundation.Transform;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Core
{
    /// <summary>粒子子系统原点绑定类型。</summary>
    public enum ParticleOriginKind : byte
    {
        Static = 0,
        TransformNode = 1,
        Custom = 2,
    }

    /// <summary>主线程采集的只读原点世界变换快照，用于向粒子工作线程安全传递坐标。</summary>
    public readonly struct ParticleOriginSnapshot
    {
        public static readonly ParticleOriginSnapshot Identity = new(Matrix.Identity, Matrix.Identity, Vector3.Zero, isAlive: true);

        public readonly Matrix WorldMatrix;
        public readonly Matrix InverseWorldMatrix;
        public readonly Vector3 Position;
        public readonly bool IsAlive;

        public ParticleOriginSnapshot(in Matrix worldMatrix, in Matrix inverseWorldMatrix, Vector3 position, bool isAlive)
        {
            WorldMatrix = worldMatrix;
            InverseWorldMatrix = inverseWorldMatrix;
            Position = position;
            IsAlive = isAlive;
        }

        /// <summary>从 <see cref="Transform3D"/> 构建存活原点快照。</summary>
        public static ParticleOriginSnapshot FromTransform(in Transform3D transform, bool isAlive = true)
        {
            Matrix world = transform.ToMatrix();
            float det = world.Determinant();
            Matrix inverse = float.IsFinite(det) && MathF.Abs(det) > 1e-12f ? Matrix.Invert(world) : Matrix.Identity;
            return new ParticleOriginSnapshot(in world, in inverse, transform.Position, isAlive);
        }

        /// <summary>从纯平移世界坐标快速构建原点快照（免去矩阵求逆开销）。</summary>
        public static ParticleOriginSnapshot FromPosition(Vector3 position, bool isAlive = true)
        {
            Matrix world = Matrix.CreateTranslation(position);
            Matrix inverse = Matrix.CreateTranslation(-position);
            return new ParticleOriginSnapshot(in world, in inverse, position, isAlive);
        }

        /// <summary>保留上一帧有效变换矩阵，仅将存活标记置为失活（用于目标销毁时的脱钩烘焙）。</summary>
        public ParticleOriginSnapshot AsDead() => new(in WorldMatrix, in InverseWorldMatrix, Position, isAlive: false);
    }

    /// <summary>粒子子系统的原点绑定描述（支持静态变换、<see cref="TransformHandle"/> 或自定义采样委托）。</summary>
    public readonly struct ParticleOriginBinding
    {
        public readonly ParticleOriginKind Kind;
        public readonly Transform3D StaticTransform;
        public readonly TransformHandle TransformHandle;
        public readonly Func<ParticleOriginSnapshot, ParticleOriginSnapshot>? CustomSampler;

        public ParticleOriginBinding()
        {
            Kind = ParticleOriginKind.Static;
            StaticTransform = Transform3D.Identity;
            TransformHandle = TransformHandle.Invalid;
            CustomSampler = null;
        }

        private ParticleOriginBinding(ParticleOriginKind kind, in Transform3D staticTransform, TransformHandle transformHandle, Func<ParticleOriginSnapshot, ParticleOriginSnapshot>? customSampler)
        {
            Kind = kind;
            StaticTransform = staticTransform;
            TransformHandle = transformHandle;
            CustomSampler = customSampler;
        }

        /// <summary>世界原点静态绑定。</summary>
        public static ParticleOriginBinding Identity => FromStatic(Transform3D.Identity);

        /// <summary>绑定到固定 3D 变换。</summary>
        public static ParticleOriginBinding FromStatic(in Transform3D transform)
            => new(ParticleOriginKind.Static, in transform, TransformHandle.Invalid, null);

        /// <summary>绑定到固定 3D 坐标。</summary>
        public static ParticleOriginBinding FromPosition(Vector3 position)
            => FromStatic(new Transform3D(position));

        /// <summary>绑定到固定 2D 世界坐标（可选景深）。</summary>
        public static ParticleOriginBinding FromPosition2D(Vector2 position, float depthZ = 0f)
            => FromStatic(new Transform3D(new Vector3(position, depthZ)));

        /// <summary>绑定到既有的 <see cref="TransformHandle"/> 节点。</summary>
        public static ParticleOriginBinding FromTransform(TransformHandle handle)
            => new(ParticleOriginKind.TransformNode, Transform3D.Identity, handle, null);

        /// <summary>绑定到接收上一帧快照的自定义主线程采样委托。</summary>
        public static ParticleOriginBinding FromCustom(Func<ParticleOriginSnapshot, ParticleOriginSnapshot> sampler)
        {
            ArgumentNullException.ThrowIfNull(sampler);
            return new(ParticleOriginKind.Custom, Transform3D.Identity, TransformHandle.Invalid, sampler);
        }

        /// <summary>绑定到无参自定义主线程采样委托。</summary>
        public static ParticleOriginBinding FromCustom(Func<ParticleOriginSnapshot> sampler)
        {
            ArgumentNullException.ThrowIfNull(sampler);
            return new(ParticleOriginKind.Custom, Transform3D.Identity, TransformHandle.Invalid, _ => sampler());
        }

        /// <summary>在主线程安全采样当前帧的原点世界矩阵快照；绑定目标失活或回调异常时保留上一帧有效矩阵。</summary>
        public ParticleOriginSnapshot SampleOnMainThread(in ParticleOriginSnapshot previous)
        {
            switch (Kind)
            {
                case ParticleOriginKind.Static:
                    // 防御绕过无参构造的 default(ParticleOriginBinding)（Scale 为 Zero 时回退单位变换）
                    if (StaticTransform.Scale == Vector3.Zero && StaticTransform.Rotation == default && StaticTransform.Position == Vector3.Zero)
                        return ParticleOriginSnapshot.Identity;
                    return ParticleOriginSnapshot.FromTransform(in StaticTransform, isAlive: true);

                case ParticleOriginKind.TransformNode:
                    if (TransformHandle.TryGetNode(out TransformNode? node))
                    {
                        Matrix world = node.WorldMatrix;
                        Matrix inv = node.WorldInverseMatrix;
                        return new ParticleOriginSnapshot(in world, in inv, world.Translation, isAlive: true);
                    }

                    return previous.AsDead();

                case ParticleOriginKind.Custom:
                    if (CustomSampler is null)
                        return previous.AsDead();

                    try
                    {
                        return CustomSampler(previous);
                    }
                    catch
                    {
                        return previous.AsDead();
                    }

                default:
                    return previous.AsDead();
            }
        }
    }
}
