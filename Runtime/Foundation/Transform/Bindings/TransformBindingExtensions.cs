#nullable enable

using System;
using Apotheosis.Runtime.Foundation.Transform.Bindings;
using Apotheosis.Runtime.Foundation.Transform.Resolvers;
using Terraria;

namespace Apotheosis.Runtime.Foundation.Transform
{
    /// <summary>
    /// <see cref="TransformNode"/> 的快捷实体绑定扩展方法（置于根命名空间便于直接调用）。
    /// </summary>
    public static class TransformBindingExtensions
    {
        /// <summary>将节点绑定到原版 <see cref="Player"/>（池化无闭包绑定，默认 <see cref="TransformSyncMode.Pull"/>）。</summary>
        public static PlayerTransformBinding BindPlayer(
            this TransformNode node,
            Player player,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<Player>? resolver = null,
            bool registerToWorld = true)
        {
            ArgumentNullException.ThrowIfNull(node);
            var binding = PlayerTransformBinding.Rent(player, resolver, syncMode, syncFlags);
            node.AttachBinding(binding, registerToWorld);
            return binding;
        }

        /// <summary>将节点绑定到原版 <see cref="NPC"/>（池化无闭包绑定）。</summary>
        public static NpcTransformBinding BindNpc(
            this TransformNode node,
            NPC npc,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<NPC>? resolver = null,
            bool registerToWorld = true,
            bool? cleanupTargetOnDispose = null)
        {
            ArgumentNullException.ThrowIfNull(node);
            var binding = NpcTransformBinding.Rent(npc, resolver, syncMode, syncFlags, cleanupTargetOnDispose);
            node.AttachBinding(binding, registerToWorld);
            return binding;
        }

        /// <summary>将节点绑定到原版 <see cref="Projectile"/>（池化无闭包绑定）。</summary>
        public static ProjectileTransformBinding BindProjectile(
            this TransformNode node,
            Projectile projectile,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<Projectile>? resolver = null,
            bool registerToWorld = true,
            bool? cleanupTargetOnDispose = null)
        {
            ArgumentNullException.ThrowIfNull(node);
            var binding = ProjectileTransformBinding.Rent(projectile, resolver, syncMode, syncFlags, cleanupTargetOnDispose);
            node.AttachBinding(binding, registerToWorld);
            return binding;
        }

        /// <summary>将节点绑定到世界掉落物实体 <see cref="WorldItem"/>（池化无闭包绑定）。</summary>
        public static WorldItemTransformBinding BindWorldItem(
            this TransformNode node,
            WorldItem worldItem,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<WorldItem>? resolver = null,
            bool registerToWorld = true,
            bool? cleanupTargetOnDispose = null)
        {
            ArgumentNullException.ThrowIfNull(node);
            var binding = WorldItemTransformBinding.Rent(worldItem, resolver, syncMode, syncFlags, cleanupTargetOnDispose);
            node.AttachBinding(binding, registerToWorld);
            return binding;
        }

        /// <summary>将节点绑定到自定义 <see cref="ITransformable"/> 实体（池化绑定）。</summary>
        public static TransformableBinding BindTransformable(
            this TransformNode node,
            ITransformable target,
            bool registerToWorld = true,
            bool? cleanupTargetOnDispose = true)
        {
            ArgumentNullException.ThrowIfNull(node);
            var binding = TransformableBinding.Rent(target, cleanupTargetOnDispose);
            node.AttachBinding(binding, registerToWorld);
            return binding;
        }
    }
}
