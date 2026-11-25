#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using Apotheosis.Runtime.Foundation.Transform.Resolvers;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis.Runtime.Foundation.Transform.Holders
{
    /// <summary>面向世界掉落物实体 <see cref="WorldItem"/> 的逐实体变换宿主。</summary>
    public sealed class TransformGlobalItem : GlobalItem
    {
        private readonly record struct BindArgs(WorldItem Target, TransformSyncMode Mode, TransformSyncFlags Flags, ITransformResolver<WorldItem>? Resolver, bool? CleanupOnDispose);

        private static readonly Action<TransformNode, BindArgs> BindAction = static (node, a) =>
            node.BindWorldItem(a.Target, a.Mode, a.Flags, a.Resolver, registerToWorld: true, a.CleanupOnDispose);

        private TransformSlot _slot;

        public override bool InstancePerEntity => true;

        /// <summary>当前掉落物是否已持有有效的 <see cref="TransformNode"/>。</summary>
        public bool HasTransform => _slot.HasTransform;

        /// <summary>零分配尝试获取当前掉落物的 <see cref="TransformNode"/>。</summary>
        public bool TryGetTransform([NotNullWhen(true)] out TransformNode? node) => _slot.TryGet(out node);

        /// <summary>获取或懒借出掉落物实体 <see cref="WorldItem"/> 的 <see cref="TransformNode"/>（已存在时不覆盖既有同步配置）。</summary>
        public TransformNode GetOrCreateTransform(WorldItem worldItem)
            => _slot.TryGet(out TransformNode? existing) ? existing
                : _slot.GetOrCreate(new BindArgs(worldItem, TransformSyncMode.Pull, TransformSyncFlags.All, null, null), static a => a.Target.Name, BindAction);

        /// <summary>获取或懒借出掉落物实体 <see cref="WorldItem"/> 的 <see cref="TransformNode"/>，并显式指定同步配置。</summary>
        public TransformNode GetOrCreateTransform(WorldItem worldItem, TransformSyncMode syncMode, TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<WorldItem>? resolver = null, bool? cleanupTargetOnDispose = null)
            => _slot.GetOrCreate(syncMode, syncFlags, resolver is not null || cleanupTargetOnDispose.HasValue,
                new BindArgs(worldItem, syncMode, syncFlags, resolver, cleanupTargetOnDispose), static a => a.Target.Name, BindAction);

        /// <summary>注入自定义 <see cref="TransformNode"/> 作为该世界掉落物的唯一变换节点。</summary>
        public TNode SetTransform<TNode>(WorldItem worldItem, TNode customNode, TransformSyncMode syncMode = TransformSyncMode.Pull, TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<WorldItem>? resolver = null, bool? cleanupTargetOnDispose = null, bool syncInitialFromTarget = true) where TNode : TransformNode
            => _slot.Set(customNode, syncInitialFromTarget, new BindArgs(worldItem, syncMode, syncFlags, resolver, cleanupTargetOnDispose), BindAction);

        public override GlobalItem Clone(Item? from, Item to)
        {
            // 物品复制时强制清空句柄，防止串用同一个 TransformNode
            var clone = (TransformGlobalItem)base.Clone(from, to);
            clone._slot.ResetHandle();
            return clone;
        }
    }
}
