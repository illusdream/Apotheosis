#nullable enable

using System.Diagnostics.CodeAnalysis;
using Apotheosis.Runtime.Foundation.Transform.Holders;
using Apotheosis.Runtime.Foundation.Transform.Resolvers;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis.Runtime.Foundation.Transform
{
    /// <summary>
    /// 原版世界实体（<see cref="Player"/>、<see cref="NPC"/>、<see cref="Projectile"/>、<see cref="WorldItem"/>）
    /// 与对应 <see cref="ModPlayer"/>、<see cref="ModNPC"/>、<see cref="ModProjectile"/> 的统一 <see cref="TransformNode"/> 访问入口。
    /// <para>
    /// 所有 <c>ModXxx</c> 扩展方法均透明转发至底层原版实体宿主，确保每个实体全模组仅存在唯一一份 <see cref="TransformNode"/>。
    /// </para>
    /// </summary>
    public static class TransformEntityExtensions
    {
        // ==================== Player & ModPlayer ====================

        public static bool HasTransform(this Player player)
            => player.GetModPlayer<TransformModPlayer>().HasTransform;

        public static bool TryGetTransform(this Player player, [NotNullWhen(true)] out TransformNode? node)
            => player.GetModPlayer<TransformModPlayer>().TryGetTransform(out node);

        public static TransformNode GetTransform(this Player player)
            => player.GetModPlayer<TransformModPlayer>().GetOrCreateTransform();

        public static TransformNode GetTransform(
            this Player player,
            TransformSyncMode syncMode,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<Player>? resolver = null)
            => player.GetModPlayer<TransformModPlayer>().GetOrCreateTransform(syncMode, syncFlags, resolver);

        public static TNode SetTransform<TNode>(
            this Player player,
            TNode customNode,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<Player>? resolver = null,
            bool syncInitialFromTarget = true) where TNode : TransformNode
            => player.GetModPlayer<TransformModPlayer>().SetTransform(customNode, syncMode, syncFlags, resolver, syncInitialFromTarget);

        public static bool HasTransform(this ModPlayer modPlayer)
            => modPlayer.Player.HasTransform();

        public static bool TryGetTransform(this ModPlayer modPlayer, [NotNullWhen(true)] out TransformNode? node)
            => modPlayer.Player.TryGetTransform(out node);

        public static TransformNode GetTransform(this ModPlayer modPlayer)
            => modPlayer.Player.GetTransform();

        public static TransformNode GetTransform(
            this ModPlayer modPlayer,
            TransformSyncMode syncMode,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<Player>? resolver = null)
            => modPlayer.Player.GetTransform(syncMode, syncFlags, resolver);

        public static TNode SetTransform<TNode>(
            this ModPlayer modPlayer,
            TNode customNode,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<Player>? resolver = null,
            bool syncInitialFromTarget = true) where TNode : TransformNode
            => modPlayer.Player.SetTransform(customNode, syncMode, syncFlags, resolver, syncInitialFromTarget);

        // ==================== NPC & ModNPC ====================

        public static bool HasTransform(this NPC npc)
            => npc.GetGlobalNPC<TransformGlobalNPC>().HasTransform;

        public static bool TryGetTransform(this NPC npc, [NotNullWhen(true)] out TransformNode? node)
            => npc.GetGlobalNPC<TransformGlobalNPC>().TryGetTransform(out node);

        public static TransformNode GetTransform(this NPC npc)
            => npc.GetGlobalNPC<TransformGlobalNPC>().GetOrCreateTransform(npc);

        public static TransformNode GetTransform(
            this NPC npc,
            TransformSyncMode syncMode,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<NPC>? resolver = null,
            bool? cleanupTargetOnDispose = null)
            => npc.GetGlobalNPC<TransformGlobalNPC>().GetOrCreateTransform(npc, syncMode, syncFlags, resolver, cleanupTargetOnDispose);

        public static TNode SetTransform<TNode>(
            this NPC npc,
            TNode customNode,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<NPC>? resolver = null,
            bool? cleanupTargetOnDispose = null,
            bool syncInitialFromTarget = true) where TNode : TransformNode
            => npc.GetGlobalNPC<TransformGlobalNPC>().SetTransform(npc, customNode, syncMode, syncFlags, resolver, cleanupTargetOnDispose, syncInitialFromTarget);

        public static bool HasTransform(this ModNPC modNpc)
            => modNpc.NPC.HasTransform();

        public static bool TryGetTransform(this ModNPC modNpc, [NotNullWhen(true)] out TransformNode? node)
            => modNpc.NPC.TryGetTransform(out node);

        public static TransformNode GetTransform(this ModNPC modNpc)
            => modNpc.NPC.GetTransform();

        public static TransformNode GetTransform(
            this ModNPC modNpc,
            TransformSyncMode syncMode,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<NPC>? resolver = null,
            bool? cleanupTargetOnDispose = null)
            => modNpc.NPC.GetTransform(syncMode, syncFlags, resolver, cleanupTargetOnDispose);

        public static TNode SetTransform<TNode>(
            this ModNPC modNpc,
            TNode customNode,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<NPC>? resolver = null,
            bool? cleanupTargetOnDispose = null,
            bool syncInitialFromTarget = true) where TNode : TransformNode
            => modNpc.NPC.SetTransform(customNode, syncMode, syncFlags, resolver, cleanupTargetOnDispose, syncInitialFromTarget);

        // ==================== Projectile & ModProjectile ====================

        public static bool HasTransform(this Projectile projectile)
            => projectile.GetGlobalProjectile<TransformGlobalProjectile>().HasTransform;

        public static bool TryGetTransform(this Projectile projectile, [NotNullWhen(true)] out TransformNode? node)
            => projectile.GetGlobalProjectile<TransformGlobalProjectile>().TryGetTransform(out node);

        public static TransformNode GetTransform(this Projectile projectile)
            => projectile.GetGlobalProjectile<TransformGlobalProjectile>().GetOrCreateTransform(projectile);

        public static TransformNode GetTransform(
            this Projectile projectile,
            TransformSyncMode syncMode,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<Projectile>? resolver = null,
            bool? cleanupTargetOnDispose = null)
            => projectile.GetGlobalProjectile<TransformGlobalProjectile>().GetOrCreateTransform(projectile, syncMode, syncFlags, resolver, cleanupTargetOnDispose);

        public static TNode SetTransform<TNode>(
            this Projectile projectile,
            TNode customNode,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<Projectile>? resolver = null,
            bool? cleanupTargetOnDispose = null,
            bool syncInitialFromTarget = true) where TNode : TransformNode
            => projectile.GetGlobalProjectile<TransformGlobalProjectile>().SetTransform(projectile, customNode, syncMode, syncFlags, resolver, cleanupTargetOnDispose, syncInitialFromTarget);

        public static bool HasTransform(this ModProjectile modProjectile)
            => modProjectile.Projectile.HasTransform();

        public static bool TryGetTransform(this ModProjectile modProjectile, [NotNullWhen(true)] out TransformNode? node)
            => modProjectile.Projectile.TryGetTransform(out node);

        public static TransformNode GetTransform(this ModProjectile modProjectile)
            => modProjectile.Projectile.GetTransform();

        public static TransformNode GetTransform(
            this ModProjectile modProjectile,
            TransformSyncMode syncMode,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<Projectile>? resolver = null,
            bool? cleanupTargetOnDispose = null)
            => modProjectile.Projectile.GetTransform(syncMode, syncFlags, resolver, cleanupTargetOnDispose);

        public static TNode SetTransform<TNode>(
            this ModProjectile modProjectile,
            TNode customNode,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<Projectile>? resolver = null,
            bool? cleanupTargetOnDispose = null,
            bool syncInitialFromTarget = true) where TNode : TransformNode
            => modProjectile.Projectile.SetTransform(customNode, syncMode, syncFlags, resolver, cleanupTargetOnDispose, syncInitialFromTarget);

        // ==================== WorldItem ====================

        public static bool HasTransform(this WorldItem worldItem)
            => worldItem.inner.GetGlobalItem<TransformGlobalItem>().HasTransform;

        public static bool TryGetTransform(this WorldItem worldItem, [NotNullWhen(true)] out TransformNode? node)
            => worldItem.inner.GetGlobalItem<TransformGlobalItem>().TryGetTransform(out node);

        public static TransformNode GetTransform(this WorldItem worldItem)
            => worldItem.inner.GetGlobalItem<TransformGlobalItem>().GetOrCreateTransform(worldItem);

        public static TransformNode GetTransform(
            this WorldItem worldItem,
            TransformSyncMode syncMode,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<WorldItem>? resolver = null,
            bool? cleanupTargetOnDispose = null)
            => worldItem.inner.GetGlobalItem<TransformGlobalItem>().GetOrCreateTransform(worldItem, syncMode, syncFlags, resolver, cleanupTargetOnDispose);

        public static TNode SetTransform<TNode>(
            this WorldItem worldItem,
            TNode customNode,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<WorldItem>? resolver = null,
            bool? cleanupTargetOnDispose = null,
            bool syncInitialFromTarget = true) where TNode : TransformNode
            => worldItem.inner.GetGlobalItem<TransformGlobalItem>().SetTransform(worldItem, customNode, syncMode, syncFlags, resolver, cleanupTargetOnDispose, syncInitialFromTarget);
    }
}
