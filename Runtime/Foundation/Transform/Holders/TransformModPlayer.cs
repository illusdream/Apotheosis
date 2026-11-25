#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using Apotheosis.Runtime.Foundation.Transform.Resolvers;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis.Runtime.Foundation.Transform.Holders
{
    /// <summary>嵌入原版 <see cref="Player"/> 的逐玩家变换宿主。</summary>
    public sealed class TransformModPlayer : ModPlayer
    {
        private readonly record struct BindArgs(Player Target, TransformSyncMode Mode, TransformSyncFlags Flags, ITransformResolver<Player>? Resolver);

        private static readonly Action<TransformNode, BindArgs> BindAction = static (node, a) =>
            node.BindPlayer(a.Target, a.Mode, a.Flags, a.Resolver);

        private TransformSlot _slot;

        /// <summary>当前玩家是否已持有有效的 <see cref="TransformNode"/>。</summary>
        public bool HasTransform => _slot.HasTransform;

        /// <summary>零分配尝试获取当前玩家的 <see cref="TransformNode"/>。</summary>
        public bool TryGetTransform([NotNullWhen(true)] out TransformNode? node) => _slot.TryGet(out node);

        /// <summary>获取或懒借出当前玩家的 <see cref="TransformNode"/>（已存在时不覆盖既有同步配置）。</summary>
        public TransformNode GetOrCreateTransform()
            => _slot.TryGet(out TransformNode? existing) ? existing
                : _slot.GetOrCreate(new BindArgs(Player, TransformSyncMode.Pull, TransformSyncFlags.All, null), static a => a.Target.name, BindAction);

        /// <summary>获取或懒借出当前玩家的 <see cref="TransformNode"/>，并显式指定同步配置。</summary>
        public TransformNode GetOrCreateTransform(TransformSyncMode syncMode, TransformSyncFlags syncFlags = TransformSyncFlags.All, ITransformResolver<Player>? resolver = null)
            => _slot.GetOrCreate(syncMode, syncFlags, resolver is not null, new BindArgs(Player, syncMode, syncFlags, resolver), static a => a.Target.name, BindAction);

        /// <summary>注入自定义 <see cref="TransformNode"/> 作为当前玩家的唯一变换节点。</summary>
        public TNode SetTransform<TNode>(TNode customNode, TransformSyncMode syncMode = TransformSyncMode.Pull, TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<Player>? resolver = null, bool syncInitialFromTarget = true) where TNode : TransformNode
            => _slot.Set(customNode, syncInitialFromTarget, new BindArgs(Player, syncMode, syncFlags, resolver), BindAction);

        public override ModPlayer Clone(Player newEntity)
        {
            // 联机 clientClone 复制玩家快照时强制清空句柄，避免副本共享真实玩家的 TransformNode
            var clone = (TransformModPlayer)base.Clone(newEntity);
            clone._slot.ResetHandle();
            return clone;
        }

        public override void OnEnterWorld() => _slot.CleanupAndClear(TransformCleanupReason.WorldUnload, detachBeforeCleanup: true);
    }
}
