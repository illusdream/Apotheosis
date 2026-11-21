#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using Apotheosis.Runtime.Foundation.Transform.Pooling;
using Apotheosis.Runtime.Foundation.Transform.Resolvers;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis.Runtime.Foundation.Transform.Holders
{
    /// <summary>
    /// 面向世界掉落物实体 <see cref="WorldItem"/> 的逐实体变换宿主（依托 <see cref="WorldItem.inner"/> 上的 <see cref="GlobalItem"/> 存储句柄）。
    /// <para>
    /// 仅服务于真实存在于世界空间中的 <see cref="WorldItem"/>；在 <see cref="Clone"/> 时强制清空句柄，防止物品复制后串用同一个 <see cref="TransformNode"/>。
    /// </para>
    /// </summary>
    public sealed class TransformGlobalItem : GlobalItem
    {
        private TransformHandle _handle;

        public override bool InstancePerEntity => true;

        /// <summary>当前掉落物是否已持有有效的 <see cref="TransformNode"/>。</summary>
        public bool HasTransform => _handle.IsValid;

        /// <summary>零分配尝试获取当前掉落物的 <see cref="TransformNode"/>。</summary>
        public bool TryGetTransform([NotNullWhen(true)] out TransformNode? node)
            => _handle.TryGetNode(out node);

        /// <summary>获取或懒借出掉落物实体 <see cref="WorldItem"/> 的 <see cref="TransformNode"/>（已存在时不覆盖既有同步配置）。</summary>
        public TransformNode GetOrCreateTransform(WorldItem worldItem)
        {
            if (_handle.TryGetNode(out TransformNode? existing))
                return existing;

            return CreateAndBind(worldItem, TransformSyncMode.Pull, TransformSyncFlags.All, resolver: null, cleanupTargetOnDispose: null);
        }

        /// <summary>获取或懒借出掉落物实体 <see cref="WorldItem"/> 的 <see cref="TransformNode"/>，并显式指定同步配置。</summary>
        public TransformNode GetOrCreateTransform(
            WorldItem worldItem,
            TransformSyncMode syncMode,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<WorldItem>? resolver = null,
            bool? cleanupTargetOnDispose = null)
        {
            if (_handle.TryGetNode(out TransformNode? existing))
            {
                if (existing.Binding is null || resolver is not null || cleanupTargetOnDispose.HasValue)
                {
                    existing.BindWorldItem(worldItem, syncMode, syncFlags, resolver, registerToWorld: true, cleanupTargetOnDispose);
                }
                else
                {
                    existing.Binding.SyncMode = syncMode;
                    existing.Binding.SyncFlags = syncFlags;
                }

                return existing;
            }

            return CreateAndBind(worldItem, syncMode, syncFlags, resolver, cleanupTargetOnDispose);
        }

        /// <summary>注入自定义 <see cref="TransformNode"/>（例如重写了清理虚函数的子类）作为该世界掉落物的唯一变换节点。</summary>
        public TNode SetTransform<TNode>(
            WorldItem worldItem,
            TNode customNode,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<WorldItem>? resolver = null,
            bool? cleanupTargetOnDispose = null,
            bool syncInitialFromTarget = true) where TNode : TransformNode
        {
            ArgumentNullException.ThrowIfNull(customNode);

            if (_handle.TryGetNode(out TransformNode? previous) && !ReferenceEquals(previous, customNode))
            {
                previous.DetachBinding();
                previous.Cleanup(TransformCleanupReason.Manual);
            }

            customNode.BindWorldItem(worldItem, syncMode, syncFlags, resolver, registerToWorld: true, cleanupTargetOnDispose);
            if (syncInitialFromTarget)
            {
                customNode.SyncFromTarget();
            }

            _handle = customNode.GetHandle();
            return customNode;
        }

        public override GlobalItem Clone(Item? from, Item to)
        {
            var clone = (TransformGlobalItem)base.Clone(from, to);
            clone._handle = default;
            return clone;
        }

        private TransformNode CreateAndBind(
            WorldItem worldItem,
            TransformSyncMode syncMode,
            TransformSyncFlags syncFlags,
            ITransformResolver<WorldItem>? resolver,
            bool? cleanupTargetOnDispose)
        {
            TransformNode node = TransformNodePool.Rent(worldItem.Name);
            node.BindWorldItem(worldItem, syncMode, syncFlags, resolver, registerToWorld: true, cleanupTargetOnDispose);
            node.SyncFromTarget();
            _handle = node.GetHandle();
            return node;
        }
    }
}
