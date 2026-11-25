#nullable enable

using System;
using System.Collections.Generic;
using Apotheosis.Runtime.Foundation.Collections;
using Apotheosis.Runtime.Foundation.Transform.Bindings;
using Apotheosis.Runtime.Foundation.Transform.Resolvers;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Foundation.Transform
{
    /// <summary>
    /// 3D 场景图变换节点：维护局部 <see cref="Transform3D"/>、父子层级关系、惰性世界矩阵缓存、
    /// 显式实体绑定、级联清理虚函数以及代际安全对象池复用能力。
    /// </summary>
    public class TransformNode : IIndexedSwapItem
    {
        private const float SingularMatrixEpsilon = 1e-12f;
        private const int MaxRetainedChildrenCapacity = 64;

        private readonly List<TransformNode> _children = new();

        private TransformNode? _parent;
        private Transform3D _local;
        private int _depth;
        private int _worldListIndex = RuntimeConstants.InvalidIndex;

        private Matrix _localMatrix = Matrix.Identity;
        private Matrix _worldMatrix = Matrix.Identity;
        private Matrix _worldInverseMatrix = Matrix.Identity;
        private Transform3D _worldTransform = Transform3D.Identity;
        private Vector3 _worldScaleSign = Vector3.One;

        private bool _localDirty = true;
        private bool _worldDirty = true;
        private bool _worldInverseDirty = true;
        private bool _worldDecomposedDirty = true;

        private uint _version = 1;
        private bool _isCleaningUp;
        private Action<TransformNode>? _poolReturnAction;

        int IIndexedSwapItem.SwapListIndex { get => _worldListIndex; set => _worldListIndex = value; }

        internal bool IsRegisteredInWorld => _worldListIndex >= 0;
        internal bool InPool { get; set; }

        /// <summary>可选调试名称（便于日志与 DebugTool 检视）。</summary>
        public string? Name { get; set; }

        /// <summary>
        /// 节点复用代际号（永不为 0，每次从对象池借出时自增），配合 <see cref="TransformHandle"/> 杜绝悬挂引用与串台。
        /// </summary>
        public uint Generation { get; private set; } = RuntimeConstants.InitialGeneration;

        /// <summary>该节点是否由对象池分配，清理结束后是否会自动归还池。</summary>
        public bool IsPooled => _poolReturnAction is not null;

        /// <summary>节点是否已完成清理（归还池中闲置时同样为 <c>true</c>）。</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>单调递增的变更版本号，可用于外部缓存失效判断。</summary>
        public uint Version => _version;

        /// <summary>当前关联的实体绑定（可为空，表示纯空间挂点/空节点）。</summary>
        public ITransformBinding? Binding { get; private set; }

        /// <summary>
        /// 可选的实例级判定委托；若非空，默认实现的 <see cref="ShouldCleanupTarget"/> 将优先使用本委托结果。
        /// </summary>
        public Func<TransformNode, TransformCleanupReason, bool>? ShouldCleanupTargetPredicate { get; set; }

        /// <summary>构造位于原点、无旋转、单位缩放的变换节点（非池化）。</summary>
        public TransformNode(string? name = null)
            : this(Transform3D.Identity, name)
        {
        }

        /// <summary>使用指定初始局部变换构造节点（非池化）。</summary>
        public TransformNode(Transform3D localTransform, string? name = null)
        {
            _local = localTransform;
            Name = name;
        }

        /// <summary>获取指向当前节点与当前代际号的安全值类型句柄。</summary>
        public TransformHandle GetHandle() => new(this);

        internal void InitializeFromPool(Action<TransformNode> returnAction, Transform3D initialTransform, string? name)
        {
            uint nextGen = Generation + 1;
            Generation = nextGen == RuntimeConstants.IndexNone ? RuntimeConstants.InitialGeneration : nextGen;

            _poolReturnAction = returnAction;
            InPool = false;
            IsDisposed = false;
            _isCleaningUp = false;

            _local = initialTransform;
            Name = name;

            _localDirty = true;
            _worldDirty = true;
            _worldInverseDirty = true;
            _worldDecomposedDirty = true;
            _version++;
        }

        // ==================== 父子层级 ====================

        /// <summary>父节点（根节点为 <c>null</c>）。</summary>
        public TransformNode? Parent => _parent;

        /// <summary>直接子节点只读列表。</summary>
        public IReadOnlyList<TransformNode> Children => _children;

        /// <summary>直接子节点数量。</summary>
        public int ChildCount => _children.Count;

        /// <summary>层级树根节点（无父节点时返回自身）。</summary>
        public TransformNode Root
        {
            get
            {
                TransformNode current = this;
                while (current._parent is not null)
                {
                    current = current._parent;
                }

                return current;
            }
        }

        /// <summary>在层级树中的深度（根节点为 0，O(1) 缓存读取）。</summary>
        public int Depth => _depth;

        /// <summary>判断当前节点是否为 <paramref name="ancestor"/> 的后代节点。</summary>
        public bool IsDescendantOf(TransformNode ancestor)
        {
            ArgumentNullException.ThrowIfNull(ancestor);

            TransformNode? current = _parent;
            while (current is not null)
            {
                if (ReferenceEquals(current, ancestor))
                    return true;

                current = current._parent;
            }

            return false;
        }

        /// <summary>
        /// 设置父节点（传入 <c>null</c> 脱离父级成为根节点）。
        /// </summary>
        /// <param name="newParent">新父节点。</param>
        /// <param name="worldPositionStays">
        /// 为 <c>true</c> 时自动反算局部变换以保持当前世界空间姿态不变；
        /// 为 <c>false</c> 时保持原有局部变换数值不变。
        /// 注：受纯 TRS 变换不含剪切（Shear）分量的数学限制，若新父节点带有非均匀缩放且与子节点存在不同旋转，
        /// 世界位置与世界旋转会精确保持，线性缩放部分按近似无剪切 TRS 还原（与 Unity <c>Transform</c> 语义一致）。
        /// </param>
        public void SetParent(TransformNode? newParent, bool worldPositionStays = false)
        {
            ThrowIfDisposed();

            if (newParent is not null)
            {
                if (newParent.IsDisposed)
                    throw new ObjectDisposedException(nameof(newParent), "无法挂载到已清理的父节点。");

                if (ReferenceEquals(newParent, this))
                    throw new InvalidOperationException("TransformNode 不能将自身设为父节点。");

                if (newParent.IsDescendantOf(this))
                    throw new InvalidOperationException("检测到 TransformNode 父子循环引用。");
            }

            if (ReferenceEquals(_parent, newParent))
                return;

            Matrix savedWorldMatrix = default;
            Vector3 savedWorldPos = default;
            Quaternion savedWorldRot = default;
            Vector3 savedWorldScale = default;
            Vector3 savedWorldSign = default;
            if (worldPositionStays)
            {
                savedWorldMatrix = WorldMatrix;
                savedWorldPos = WorldPosition;
                savedWorldRot = WorldRotation;
                savedWorldScale = WorldScale;
                savedWorldSign = WorldScaleSign;
            }

            _parent?._children.Remove(this);
            _parent = newParent;
            newParent?._children.Add(this);
            UpdateDepthRecursive(newParent is null ? 0 : newParent._depth + 1);

            if (worldPositionStays)
            {
                if (newParent is null)
                {
                    _local = new Transform3D(savedWorldPos, savedWorldRot, savedWorldScale);
                }
                else
                {
                    Vector3 newLocalPos = Vector3.Transform(savedWorldPos, newParent.WorldInverseMatrix);

                    Vector3 parentSign = newParent.WorldScaleSign;
                    Quaternion invParentRot = Quaternion.Inverse(Transform3D.NormalizeOrIdentity(newParent.WorldRotation));
                    Quaternion newLocalRot = Transform3D.NormalizeOrIdentity(
                        Transform3D.ConjugateBySign(invParentRot * savedWorldRot, parentSign));

                    Matrix localMatrix = savedWorldMatrix * newParent.WorldInverseMatrix;
                    Vector3 localSign = savedWorldSign * parentSign;
                    float sx = new Vector3(localMatrix.M11, localMatrix.M12, localMatrix.M13).Length();
                    float sy = new Vector3(localMatrix.M21, localMatrix.M22, localMatrix.M23).Length();
                    float sz = new Vector3(localMatrix.M31, localMatrix.M32, localMatrix.M33).Length();
                    Vector3 newLocalScale = new(localSign.X * sx, localSign.Y * sy, localSign.Z * sz);

                    _local = new Transform3D(newLocalPos, newLocalRot, newLocalScale);
                }

                MarkLocalDirty();
            }
            else
            {
                MarkWorldDirtyRecursive();
            }
        }

        /// <summary>将所有直接子节点脱离当前节点成为独立根节点。</summary>
        public void DetachChildren(bool worldPositionStays = false)
        {
            for (int i = _children.Count - 1; i >= 0; i--)
            {
                _children[i].SetParent(null, worldPositionStays);
            }
        }

        // ==================== 局部变换属性 ====================

        /// <summary>局部 3D 变换整体读写。</summary>
        public Transform3D LocalTransform
        {
            get => _local;
            set
            {
                ThrowIfDisposed();
                if (_local.Equals(value))
                    return;

                _local = value;
                MarkLocalDirty();
            }
        }

        /// <summary>局部 3D 位置。</summary>
        public Vector3 LocalPosition
        {
            get => _local.Position;
            set
            {
                ThrowIfDisposed();
                if (_local.Position == value)
                    return;

                _local.Position = value;
                MarkLocalDirty();
            }
        }

        /// <summary>局部 2D 位置（保留当前局部 Z）。</summary>
        public Vector2 LocalPosition2D
        {
            get => _local.Position2D;
            set => LocalPosition = new Vector3(value.X, value.Y, _local.Position.Z);
        }

        /// <summary>局部 3D 旋转四元数。</summary>
        public Quaternion LocalRotation
        {
            get => _local.Rotation;
            set
            {
                ThrowIfDisposed();
                if (_local.Rotation == value)
                    return;

                _local.Rotation = value;
                MarkLocalDirty();
            }
        }

        /// <summary>局部欧拉角（弧度，X=Pitch, Y=Yaw, Z=Roll）。</summary>
        public Vector3 LocalEulerAngles
        {
            get => _local.EulerAngles;
            set => LocalRotation = Transform3D.CreateRotationFromEuler(value);
        }

        /// <summary>局部绕 +Z 轴 2D 旋转弧度（写入时将局部旋转设为纯 Z 轴旋转）。</summary>
        public float LocalRotationZ
        {
            get => _local.RotationZ;
            set
            {
                Quaternion next = MathF.Abs(value) > 1e-6f
                    ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, value)
                    : Quaternion.Identity;
                LocalRotation = next;
            }
        }

        /// <summary>局部 3D 缩放。</summary>
        public Vector3 LocalScale
        {
            get => _local.Scale;
            set
            {
                ThrowIfDisposed();
                if (_local.Scale == value)
                    return;

                _local.Scale = value;
                MarkLocalDirty();
            }
        }

        /// <summary>局部 2D 缩放（保留当前局部 Z 缩放）。</summary>
        public Vector2 LocalScale2D
        {
            get => _local.Scale2D;
            set => LocalScale = new Vector3(value.X, value.Y, _local.Scale.Z);
        }

        /// <summary>局部是否处于水平翻转态（<c>LocalScale.X &lt; 0</c>）。</summary>
        public bool IsLocalFlippedX => _local.Scale.X < 0f;

        /// <summary>局部是否处于垂直翻转态（<c>LocalScale.Y &lt; 0</c>，例如原版重力药剂倒转）。</summary>
        public bool IsLocalFlippedY => _local.Scale.Y < 0f;

        /// <summary>
        /// 水平翻转：严格仅修改 <see cref="LocalScale"/> 的 X 分量正负号（保留绝对值，不修改旋转）。
        /// </summary>
        public void SetFlipX(bool flipped)
        {
            ThrowIfDisposed();
            float absX = MathF.Abs(_local.Scale.X);
            float targetX = flipped ? -absX : absX;
            if (_local.Scale.X == targetX)
                return;

            _local.Scale.X = targetX;
            MarkLocalDirty();
        }

        /// <summary>
        /// 垂直翻转（适配原版重力药剂人物倒转）：严格仅修改 <see cref="LocalScale"/> 的 Y 分量正负号（保留绝对值，不修改旋转）。
        /// </summary>
        public void SetFlipY(bool flipped)
        {
            ThrowIfDisposed();
            float absY = MathF.Abs(_local.Scale.Y);
            float targetY = flipped ? -absY : absY;
            if (_local.Scale.Y == targetY)
                return;

            _local.Scale.Y = targetY;
            MarkLocalDirty();
        }

        // ==================== 世界矩阵与世界变换 ====================

        /// <summary>局部变换矩阵 <c>S * R * T</c>。</summary>
        public Matrix LocalMatrix
        {
            get
            {
                EnsureLocalMatrix();
                return _localMatrix;
            }
        }

        /// <summary>世界空间变换矩阵（惰性缓存，支持任意 3D 旋转与负缩放镜像传播）。</summary>
        public Matrix WorldMatrix
        {
            get
            {
                EnsureWorldMatrix();
                return _worldMatrix;
            }
        }

        /// <summary>世界空间变换矩阵的逆矩阵（若矩阵奇异则回退为 <see cref="Matrix.Identity"/>）。</summary>
        public Matrix WorldInverseMatrix
        {
            get
            {
                EnsureWorldMatrix();
                if (_worldInverseDirty)
                {
                    _worldInverseMatrix = MathF.Abs(_worldMatrix.Determinant()) > SingularMatrixEpsilon
                        ? Matrix.Invert(_worldMatrix)
                        : Matrix.Identity;
                    _worldInverseDirty = false;
                }

                return _worldInverseMatrix;
            }
        }

        /// <summary>世界空间分解后的 <see cref="Transform3D"/>（根节点无损等于局部变换）。</summary>
        public Transform3D WorldTransform
        {
            get
            {
                EnsureWorldDecomposed();
                return _worldTransform;
            }
        }

        /// <summary>世界空间 3D 位置（写入时自动换算为局部位置）。</summary>
        public Vector3 WorldPosition
        {
            get => WorldMatrix.Translation;
            set
            {
                ThrowIfDisposed();
                if (_parent is null)
                {
                    LocalPosition = value;
                }
                else
                {
                    LocalPosition = Vector3.Transform(value, _parent.WorldInverseMatrix);
                }
            }
        }

        /// <summary>世界空间 2D 位置（写入时保留当前世界 Z 坐标）。</summary>
        public Vector2 WorldPosition2D
        {
            get
            {
                Vector3 pos = WorldMatrix.Translation;
                return new Vector2(pos.X, pos.Y);
            }
            set => WorldPosition = new Vector3(value.X, value.Y, WorldPosition.Z);
        }

        /// <summary>
        /// 世界空间 3D 旋转四元数（写入时自动考虑父级负缩放反射共轭并换算为局部旋转）。
        /// </summary>
        public Quaternion WorldRotation
        {
            get => WorldTransform.Rotation;
            set
            {
                ThrowIfDisposed();
                Quaternion normalized = Transform3D.NormalizeOrIdentity(value);
                if (_parent is null)
                {
                    LocalRotation = normalized;
                }
                else
                {
                    // XNA 行向量约定 + 父级反射矩阵 F：R_world = R_parent * (F * R_local * F)
                    // => R_local = F * (inv(R_parent) * R_world) * F
                    Quaternion invParent = Quaternion.Inverse(Transform3D.NormalizeOrIdentity(_parent.WorldRotation));
                    LocalRotation = Transform3D.NormalizeOrIdentity(
                        Transform3D.ConjugateBySign(invParent * normalized, _parent.WorldScaleSign));
                }
            }
        }

        /// <summary>世界空间欧拉角（弧度）。</summary>
        public Vector3 WorldEulerAngles
        {
            get => Transform3D.ToEulerAngles(WorldRotation);
            set => WorldRotation = Transform3D.CreateRotationFromEuler(value);
        }

        /// <summary>世界空间近似缩放（等同 Unity 的 <c>lossyScale</c>，且保留各轴精确翻转符号）。</summary>
        public Vector3 WorldScale => WorldTransform.Scale;

        /// <summary>世界空间各轴缩放的累计符号（每个分量严格为 <c>+1</c> 或 <c>-1</c>）。</summary>
        internal Vector3 WorldScaleSign
        {
            get
            {
                EnsureWorldDecomposed();
                return _worldScaleSign;
            }
        }

        /// <summary>世界空间矩阵是否呈奇次镜像翻转状态（3D 行列式小于 0）。</summary>
        public bool IsWorldMirrored => WorldMatrix.Determinant() < 0f;

        /// <summary>将当前节点的世界变换投影为 2D 渲染参数（<see cref="SpriteTransform2D"/>）。</summary>
        public SpriteTransform2D ToSprite2D(bool preferHorizontalFlip = false)
        {
            Matrix world = WorldMatrix;
            return Transform3D.ExtractSprite2D(in world, preferHorizontalFlip);
        }

        // ==================== 坐标空间转换 ====================

        /// <summary>将局部坐标点转换到世界空间。</summary>
        public Vector3 LocalToWorldPoint(Vector3 localPoint)
            => Vector3.Transform(localPoint, WorldMatrix);

        /// <summary>将局部 2D 坐标点转换到世界 2D 空间。</summary>
        public Vector2 LocalToWorldPoint(Vector2 localPoint)
        {
            Vector3 world = Vector3.Transform(new Vector3(localPoint, 0f), WorldMatrix);
            return new Vector2(world.X, world.Y);
        }

        /// <summary>将世界坐标点转换到当前节点的局部空间。</summary>
        public Vector3 WorldToLocalPoint(Vector3 worldPoint)
            => Vector3.Transform(worldPoint, WorldInverseMatrix);

        /// <summary>将世界 2D 坐标点转换到当前节点的局部 2D 空间。</summary>
        public Vector2 WorldToLocalPoint(Vector2 worldPoint)
        {
            Vector3 local = Vector3.Transform(new Vector3(worldPoint, 0f), WorldInverseMatrix);
            return new Vector2(local.X, local.Y);
        }

        /// <summary>将局部方向向量转换到世界空间（受旋转与缩放影响，不含平移）。</summary>
        public Vector3 LocalToWorldVector(Vector3 localVector)
            => Vector3.TransformNormal(localVector, WorldMatrix);

        /// <summary>将世界方向向量转换到局部空间（含逆旋转与逆缩放，不含平移）。</summary>
        public Vector3 WorldToLocalVector(Vector3 worldVector)
            => Vector3.TransformNormal(worldVector, WorldInverseMatrix);

        // ==================== 显式 Resolver 与绑定管理 ====================

        /// <summary>使用显式解析器立即从指定目标读取空间变换写入本节点。</summary>
        public void SyncFrom<TTarget>(
            TTarget target,
            ITransformResolver<TTarget> resolver,
            TransformSyncFlags flags = TransformSyncFlags.All)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            ThrowIfDisposed();
            resolver.ReadFrom(this, target, flags);
        }

        /// <summary>使用显式解析器立即将本节点的世界变换写回指定目标的原有数据内。</summary>
        public void ApplyTo<TTarget>(
            TTarget target,
            ITransformResolver<TTarget> resolver,
            TransformSyncFlags flags = TransformSyncFlags.All)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            ThrowIfDisposed();
            resolver.WriteTo(this, target, flags);
        }

        /// <summary>挂载实体绑定（若已有旧绑定会自动释放回池），并可选注册到 <see cref="TransformWorldSystem"/>。</summary>
        public void AttachBinding(ITransformBinding binding, bool registerToWorld = true)
        {
            ArgumentNullException.ThrowIfNull(binding);
            ThrowIfDisposed();

            if (!ReferenceEquals(Binding, binding))
            {
                Binding?.Release();
                Binding = binding;
            }

            if (registerToWorld)
            {
                TransformWorldSystem.Register(this);
            }
        }

        /// <summary>解除当前实体绑定并释放/回池（可选从 <see cref="TransformWorldSystem"/> 注销）。</summary>
        public void DetachBinding(bool unregisterFromWorld = true)
        {
            ITransformBinding? oldBinding = Binding;
            Binding = null;
            oldBinding?.Release();

            if (unregisterFromWorld && IsRegisteredInWorld)
            {
                TransformWorldSystem.Unregister(this);
            }
        }

        /// <summary>立即从当前绑定的目标执行一次 Pull 同步。</summary>
        public void SyncFromTarget()
        {
            if (!IsDisposed)
            {
                Binding?.SyncFromTarget(this);
            }
        }

        /// <summary>立即将本节点世界变换 Push 写回当前绑定的目标。</summary>
        public void ApplyToTarget()
        {
            if (!IsDisposed)
            {
                Binding?.ApplyToTarget(this);
            }
        }

        // ==================== 级联清理、虚函数钩子与自动回池 ====================

        /// <summary>
        /// 清理本节点并级联清理所有子节点；若本节点借自对象池，清理完成后将自动重置并归还对象池。
        /// </summary>
        public void Cleanup(TransformCleanupReason reason = TransformCleanupReason.Manual)
        {
            if (IsDisposed || _isCleaningUp)
                return;

            _isCleaningUp = true;
            try
            {
                // 1. 先倒序递归清理子节点（确保子节点在清理瞬间仍可读取父节点最后的世界坐标）
                while (_children.Count > 0)
                {
                    TransformNode child = _children[^1];
                    child.Cleanup(TransformCleanupReason.ParentCleaned);

                    if (_children.Count > 0 && ReferenceEquals(_children[^1], child))
                    {
                        _children.RemoveAt(_children.Count - 1);
                    }
                }

                // 2. 清理钩子
                OnCleanup(reason);

                // 3. 虚函数判定是否一并清理关联实体
                ITransformBinding? binding = Binding;
                if (binding is not null && ShouldCleanupTarget(reason))
                {
                    binding.CleanupTarget(reason);
                }

                // 4. 解除绑定并将池化绑定归还池
                Binding = null;
                binding?.Release();

                // 5. 从世界托管系统注销
                if (IsRegisteredInWorld)
                {
                    TransformWorldSystem.Unregister(this);
                }

                // 6. 从父节点脱离
                if (_parent is not null)
                {
                    _parent._children.Remove(this);
                    _parent = null;
                    _depth = 0;
                }

                IsDisposed = true;
            }
            finally
            {
                _isCleaningUp = false;
            }

            // 7. 若借自对象池，彻底重置引用与状态并归还对应池
            if (_poolReturnAction is not null && !InPool)
            {
                ResetStateForPool();
                _poolReturnAction(this);
            }
        }

        /// <summary>
        /// 虚函数：在节点清理时调用，用于判断与该 <see cref="TransformNode"/> 关联的实体是否应当一并清理。
        /// </summary>
        protected virtual bool ShouldCleanupTarget(TransformCleanupReason reason)
        {
            if (reason is TransformCleanupReason.TargetDestroyed or TransformCleanupReason.WorldUnload)
                return false;

            if (ShouldCleanupTargetPredicate is not null)
                return ShouldCleanupTargetPredicate(this, reason);

            return Binding?.DefaultCleanupTargetOnDispose ?? false;
        }

        /// <summary>
        /// 虚函数：节点执行清理时的扩展钩子（在子节点清理完成后、关联实体清理与解绑之前调用）。
        /// </summary>
        protected virtual void OnCleanup(TransformCleanupReason reason) { }

        /// <summary>
        /// 虚函数：当池化的自定义子类节点归还对象池时调用，用于重置子类扩展字段与清空引用。
        /// </summary>
        protected virtual void OnResetForPool() { }

        private void ResetStateForPool()
        {
            Name = null;
            ShouldCleanupTargetPredicate = null;
            _depth = 0;
            _worldListIndex = RuntimeConstants.InvalidIndex;
            _local = Transform3D.Identity;
            _localMatrix = Matrix.Identity;
            _worldMatrix = Matrix.Identity;
            _worldInverseMatrix = Matrix.Identity;
            _worldTransform = Transform3D.Identity;
            _worldScaleSign = Vector3.One;
            _localDirty = true;
            _worldDirty = true;
            _worldInverseDirty = true;
            _worldDecomposedDirty = true;

            _children.Clear();
            if (_children.Capacity > MaxRetainedChildrenCapacity)
            {
                _children.Capacity = MaxRetainedChildrenCapacity;
            }

            OnResetForPool();
        }

        // ==================== 批量同步与内部脏标记维护 ====================

        /// <summary>批量写入 2D 世界空间同步结果，整棵子树仅触发一次脏传播。</summary>
        internal void ApplyWorld2DSync(bool setPosition, Vector2 worldCenter, bool setRotation, float worldRotationZ,
            bool setUniformScale, float uniformScale, bool setFlip, bool flipX, bool flipY)
        {
            ThrowIfDisposed();

            Transform3D next = _local;
            TransformNode? parent = _parent;

            if (setPosition)
            {
                float worldZ = parent is null ? _local.Position.Z : WorldPosition.Z;
                Vector3 targetWorldPos = new(worldCenter, worldZ);
                next.Position = parent is null ? targetWorldPos : Vector3.Transform(targetWorldPos, parent.WorldInverseMatrix);
            }

            if (setRotation)
            {
                Quaternion targetWorldRot = Transform3D.NormalizeOrIdentity(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, worldRotationZ));
                if (parent is null)
                {
                    next.Rotation = targetWorldRot;
                }
                else
                {
                    Quaternion invParent = Quaternion.Inverse(Transform3D.NormalizeOrIdentity(parent.WorldRotation));
                    next.Rotation = Transform3D.NormalizeOrIdentity(Transform3D.ConjugateBySign(invParent * targetWorldRot, parent.WorldScaleSign));
                }
            }

            if (setUniformScale)
            {
                float mag = MathF.Abs(uniformScale);
                Vector3 parentScale = parent?.WorldScale ?? Vector3.One;
                float localMagX = MathF.Abs(parentScale.X) > 1e-6f ? mag / MathF.Abs(parentScale.X) : mag;
                float localMagY = MathF.Abs(parentScale.Y) > 1e-6f ? mag / MathF.Abs(parentScale.Y) : mag;
                next.Scale = new Vector3(next.Scale.X < 0f ? -localMagX : localMagX, next.Scale.Y < 0f ? -localMagY : localMagY, next.Scale.Z);
            }

            if (setFlip)
            {
                Vector3 parentSign = parent?.WorldScaleSign ?? Vector3.One;
                bool localFlipX = flipX ^ (parentSign.X < 0f);
                bool localFlipY = flipY ^ (parentSign.Y < 0f);
                float absX = MathF.Abs(next.Scale.X);
                float absY = MathF.Abs(next.Scale.Y);
                next.Scale.X = localFlipX ? -absX : absX;
                next.Scale.Y = localFlipY ? -absY : absY;
            }

            if (_local.Equals(next))
                return;

            _local = next;
            MarkLocalDirty();
        }

        private void UpdateDepthRecursive(int newDepth)
        {
            _depth = newDepth;
            for (int i = 0; i < _children.Count; i++)
                _children[i].UpdateDepthRecursive(newDepth + 1);
        }

        private void MarkLocalDirty()
        {
            _localDirty = true;
            MarkWorldDirtyRecursive();
        }

        private void MarkWorldDirtyRecursive()
        {
            // 始终沿子树推进 _version，避免父节点连续修改时仅观察子节点 Version 的外部缓存漏感知
            _worldDirty = true;
            _worldInverseDirty = true;
            _worldDecomposedDirty = true;
            _version++;

            for (int i = 0; i < _children.Count; i++)
            {
                _children[i].MarkWorldDirtyRecursive();
            }
        }

        private void EnsureLocalMatrix()
        {
            if (!_localDirty)
                return;

            _localMatrix = _local.ToMatrix();
            _localDirty = false;
        }

        private void EnsureWorldMatrix()
        {
            if (!_worldDirty)
                return;

            EnsureLocalMatrix();

            if (_parent is null)
            {
                _worldMatrix = _localMatrix;
            }
            else
            {
                _worldMatrix = _localMatrix * _parent.WorldMatrix;
            }

            _worldDirty = false;
            _worldInverseDirty = true;
            _worldDecomposedDirty = true;
        }

        private void EnsureWorldDecomposed()
        {
            EnsureWorldMatrix();
            if (!_worldDecomposedDirty)
                return;

            Vector3 localSign = Transform3D.ExtractScaleSign(_local.Scale);
            if (_parent is null)
            {
                _worldScaleSign = localSign;
                _worldTransform = new Transform3D(
                    _local.Position,
                    Transform3D.NormalizeOrIdentity(_local.Rotation),
                    _local.Scale);
            }
            else
            {
                Vector3 parentSign = _parent.WorldScaleSign;
                _worldScaleSign = localSign * parentSign;

                // 层级旋转链叠加（经父级反射共轭修正）：与根节点语义严格统一，不因挂到 Identity 或 FlipY 父节点而跳变 180°
                Quaternion parentRot = Transform3D.NormalizeOrIdentity(_parent.WorldRotation);
                Quaternion conjugatedLocalRot = Transform3D.ConjugateBySign(_local.Rotation, parentSign);
                Quaternion worldRot = Transform3D.NormalizeOrIdentity(parentRot * conjugatedLocalRot);

                float sx = new Vector3(_worldMatrix.M11, _worldMatrix.M12, _worldMatrix.M13).Length();
                float sy = new Vector3(_worldMatrix.M21, _worldMatrix.M22, _worldMatrix.M23).Length();
                float sz = new Vector3(_worldMatrix.M31, _worldMatrix.M32, _worldMatrix.M33).Length();
                Vector3 worldScale = new(_worldScaleSign.X * sx, _worldScaleSign.Y * sy, _worldScaleSign.Z * sz);

                _worldTransform = new Transform3D(_worldMatrix.Translation, worldRot, worldScale);
            }

            _worldDecomposedDirty = false;
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(TransformNode), $"TransformNode '{Name ?? "<unnamed>"}' 已被清理销毁。");
        }

        public override string ToString()
            => $"TransformNode(Name={Name ?? "<unnamed>"}, Gen={Generation}, Children={_children.Count}, Disposed={IsDisposed})";
    }
}
