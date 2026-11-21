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
    /// 嵌入原版 <see cref="NPC"/> 与所有 <see cref="ModNPC"/> 的逐实体变换宿主（<c>InstancePerEntity = true</c>）。
    /// </summary>
    public sealed class TransformGlobalNPC : GlobalNPC
    {
        private TransformHandle _handle;

        public override bool InstancePerEntity => true;

        /// <summary>当前 NPC 是否已持有有效的 <see cref="TransformNode"/>。</summary>
        public bool HasTransform => _handle.IsValid;

        /// <summary>零分配尝试获取当前 NPC 的 <see cref="TransformNode"/>。</summary>
        public bool TryGetTransform([NotNullWhen(true)] out TransformNode? node)
            => _handle.TryGetNode(out node);

        /// <summary>获取或懒借出当前 NPC 的 <see cref="TransformNode"/>（已存在时不覆盖既有同步配置）。</summary>
        public TransformNode GetOrCreateTransform(NPC npc)
        {
            if (_handle.TryGetNode(out TransformNode? existing))
                return existing;

            return CreateAndBind(npc, TransformSyncMode.Pull, TransformSyncFlags.All, resolver: null, cleanupTargetOnDispose: null);
        }

        /// <summary>获取或懒借出当前 NPC 的 <see cref="TransformNode"/>，并显式指定同步配置。</summary>
        public TransformNode GetOrCreateTransform(
            NPC npc,
            TransformSyncMode syncMode,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<NPC>? resolver = null,
            bool? cleanupTargetOnDispose = null)
        {
            if (_handle.TryGetNode(out TransformNode? existing))
            {
                if (existing.Binding is null || resolver is not null || cleanupTargetOnDispose.HasValue)
                {
                    existing.BindNpc(npc, syncMode, syncFlags, resolver, registerToWorld: true, cleanupTargetOnDispose);
                }
                else
                {
                    existing.Binding.SyncMode = syncMode;
                    existing.Binding.SyncFlags = syncFlags;
                }

                return existing;
            }

            return CreateAndBind(npc, syncMode, syncFlags, resolver, cleanupTargetOnDispose);
        }

        /// <summary>
        /// 注入自定义 <see cref="TransformNode"/>（例如重写了清理虚函数的子类）作为该 NPC 的唯一变换节点。
        /// </summary>
        public TNode SetTransform<TNode>(
            NPC npc,
            TNode customNode,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<NPC>? resolver = null,
            bool? cleanupTargetOnDispose = null,
            bool syncInitialFromTarget = true) where TNode : TransformNode
        {
            ArgumentNullException.ThrowIfNull(customNode);

            if (_handle.TryGetNode(out TransformNode? previous) && !ReferenceEquals(previous, customNode))
            {
                previous.DetachBinding();
                previous.Cleanup(TransformCleanupReason.Manual);
            }

            customNode.BindNpc(npc, syncMode, syncFlags, resolver, registerToWorld: true, cleanupTargetOnDispose);
            if (syncInitialFromTarget)
            {
                customNode.SyncFromTarget();
            }

            _handle = customNode.GetHandle();
            return customNode;
        }

        public override void OnKill(NPC npc)
        {
            if (_handle.TryGetNode(out TransformNode? node))
            {
                _handle = default;
                node.Cleanup(TransformCleanupReason.TargetDestroyed);
            }
        }

        private TransformNode CreateAndBind(
            NPC npc,
            TransformSyncMode syncMode,
            TransformSyncFlags syncFlags,
            ITransformResolver<NPC>? resolver,
            bool? cleanupTargetOnDispose)
        {
            TransformNode node = TransformNodePool.Rent(npc.TypeName);
            node.BindNpc(npc, syncMode, syncFlags, resolver, registerToWorld: true, cleanupTargetOnDispose);
            node.SyncFromTarget();
            _handle = node.GetHandle();
            return node;
        }
    }
}
