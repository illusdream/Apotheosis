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
    /// 嵌入原版 <see cref="Player"/> 的逐玩家变换宿主（按需从对象池懒借出，通过 <see cref="TransformHandle"/> 防串台）。
    /// </summary>
    public sealed class TransformModPlayer : ModPlayer
    {
        private TransformHandle _handle;

        /// <summary>当前玩家是否已持有有效的 <see cref="TransformNode"/>。</summary>
        public bool HasTransform => _handle.IsValid;

        /// <summary>零分配尝试获取当前玩家的 <see cref="TransformNode"/>。</summary>
        public bool TryGetTransform([NotNullWhen(true)] out TransformNode? node)
            => _handle.TryGetNode(out node);

        /// <summary>获取或懒借出当前玩家的 <see cref="TransformNode"/>（已存在时不覆盖既有同步配置）。</summary>
        public TransformNode GetOrCreateTransform()
        {
            if (_handle.TryGetNode(out TransformNode? existing))
                return existing;

            return CreateAndBind(TransformSyncMode.Pull, TransformSyncFlags.All, resolver: null);
        }

        /// <summary>获取或懒借出当前玩家的 <see cref="TransformNode"/>，并显式指定同步配置。</summary>
        public TransformNode GetOrCreateTransform(
            TransformSyncMode syncMode,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<Player>? resolver = null)
        {
            if (_handle.TryGetNode(out TransformNode? existing))
            {
                if (existing.Binding is null || resolver is not null)
                {
                    existing.BindPlayer(Player, syncMode, syncFlags, resolver);
                }
                else
                {
                    existing.Binding.SyncMode = syncMode;
                    existing.Binding.SyncFlags = syncFlags;
                }

                return existing;
            }

            return CreateAndBind(syncMode, syncFlags, resolver);
        }

        /// <summary>
        /// 注入自定义 <see cref="TransformNode"/>（例如重写了清理虚函数的子类）作为当前玩家的唯一变换节点。
        /// </summary>
        public TNode SetTransform<TNode>(
            TNode customNode,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<Player>? resolver = null,
            bool syncInitialFromTarget = true) where TNode : TransformNode
        {
            ArgumentNullException.ThrowIfNull(customNode);

            if (_handle.TryGetNode(out TransformNode? previous) && !ReferenceEquals(previous, customNode))
            {
                previous.DetachBinding();
                previous.Cleanup(TransformCleanupReason.Manual);
            }

            customNode.BindPlayer(Player, syncMode, syncFlags, resolver);
            if (syncInitialFromTarget)
            {
                customNode.SyncFromTarget();
            }

            _handle = customNode.GetHandle();
            return customNode;
        }

        public override void OnEnterWorld()
        {
            // 进入新世界时若旧句柄仍残留则先安全清理
            if (_handle.TryGetNode(out TransformNode? node))
            {
                _handle = default;
                node.DetachBinding();
                node.Cleanup(TransformCleanupReason.WorldUnload);
            }
        }

        private TransformNode CreateAndBind(
            TransformSyncMode syncMode,
            TransformSyncFlags syncFlags,
            ITransformResolver<Player>? resolver)
        {
            TransformNode node = TransformNodePool.Rent(Player.name);
            node.BindPlayer(Player, syncMode, syncFlags, resolver);
            node.SyncFromTarget();
            _handle = node.GetHandle();
            return node;
        }
    }
}
