#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;

namespace Apotheosis.Runtime.Foundation.Transform
{
    /// <summary>
    /// 带代际号校验的零分配安全句柄，用于跨帧安全持有可能被对象池复用的 <see cref="TransformNode"/> 引用。
    /// <para>
    /// 当节点被清理并归还对象池后，即便该对象实例被借给其他实体，由于 <see cref="TransformNode.Generation"/> 已自增，
    /// 旧句柄的 <see cref="IsValid"/> 仍会立刻判定为 <c>false</c>，彻底杜绝悬挂引用与串台。
    /// </para>
    /// </summary>
    public readonly struct TransformHandle : IEquatable<TransformHandle>
    {
        private readonly TransformNode? _node;
        private readonly uint _generation;

        /// <summary>空/无效句柄。</summary>
        public static TransformHandle Invalid => default;

        internal TransformHandle(TransformNode node)
        {
            _node = node;
            _generation = node.Generation;
        }

        /// <summary>目标节点是否依然存活且未被对象池回收复用。</summary>
        public bool IsValid
            => _node is not null
            && !_node.IsDisposed
            && _node.Generation == _generation;

        /// <summary>尝试安全获取有效节点实例；若节点已清理或已复用则返回 <c>false</c>。</summary>
        public bool TryGetNode([NotNullWhen(true)] out TransformNode? node)
        {
            if (IsValid)
            {
                node = _node!;
                return true;
            }

            node = null;
            return false;
        }

        public bool Equals(TransformHandle other)
            => ReferenceEquals(_node, other._node) && _generation == other._generation;

        public override bool Equals(object? obj)
            => obj is TransformHandle other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(_node, _generation);

        public static bool operator ==(TransformHandle left, TransformHandle right) => left.Equals(right);
        public static bool operator !=(TransformHandle left, TransformHandle right) => !left.Equals(right);
    }
}
