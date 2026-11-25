#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using Apotheosis.Runtime.Foundation.Transform.Resolvers;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis.Runtime.Foundation.Transform.Holders
{
    /// <summary>嵌入原版 <see cref="NPC"/> 与 <see cref="ModNPC"/> 的逐实体变换宿主。</summary>
    public sealed class TransformGlobalNPC : GlobalNPC
    {
        private readonly record struct BindArgs(NPC Target, TransformSyncMode Mode, TransformSyncFlags Flags, ITransformResolver<NPC>? Resolver, bool? CleanupOnDispose);

        private static readonly Action<TransformNode, BindArgs> BindAction = static (node, a) =>
            node.BindNpc(a.Target, a.Mode, a.Flags, a.Resolver, registerToWorld: true, a.CleanupOnDispose);

        private TransformSlot _slot;

        public override bool InstancePerEntity => true;

        /// <summary>当前 NPC 是否已持有有效的 <see cref="TransformNode"/>。</summary>
        public bool HasTransform => _slot.HasTransform;

        /// <summary>零分配尝试获取当前 NPC 的 <see cref="TransformNode"/>。</summary>
        public bool TryGetTransform([NotNullWhen(true)] out TransformNode? node) => _slot.TryGet(out node);

        /// <summary>获取或懒借出当前 NPC 的 <see cref="TransformNode"/>（已存在时不覆盖既有同步配置）。</summary>
        public TransformNode GetOrCreateTransform(NPC npc)
            => _slot.TryGet(out TransformNode? existing) ? existing
                : _slot.GetOrCreate(new BindArgs(npc, TransformSyncMode.Pull, TransformSyncFlags.All, null, null), static a => a.Target.TypeName, BindAction);

        /// <summary>获取或懒借出当前 NPC 的 <see cref="TransformNode"/>，并显式指定同步配置。</summary>
        public TransformNode GetOrCreateTransform(NPC npc, TransformSyncMode syncMode, TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<NPC>? resolver = null, bool? cleanupTargetOnDispose = null)
            => _slot.GetOrCreate(syncMode, syncFlags, resolver is not null || cleanupTargetOnDispose.HasValue,
                new BindArgs(npc, syncMode, syncFlags, resolver, cleanupTargetOnDispose), static a => a.Target.TypeName, BindAction);

        /// <summary>注入自定义 <see cref="TransformNode"/> 作为该 NPC 的唯一变换节点。</summary>
        public TNode SetTransform<TNode>(NPC npc, TNode customNode, TransformSyncMode syncMode = TransformSyncMode.Pull, TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<NPC>? resolver = null, bool? cleanupTargetOnDispose = null, bool syncInitialFromTarget = true) where TNode : TransformNode
            => _slot.Set(customNode, syncInitialFromTarget, new BindArgs(npc, syncMode, syncFlags, resolver, cleanupTargetOnDispose), BindAction);

        public override GlobalNPC Clone(NPC? from, NPC to)
        {
            // NPC 克隆时强制清空句柄，防止两个实体共享同一个 TransformNode
            var clone = (TransformGlobalNPC)base.Clone(from, to);
            clone._slot.ResetHandle();
            return clone;
        }

        public override void OnKill(NPC npc) => _slot.CleanupAndClear(TransformCleanupReason.TargetDestroyed);
    }
}
