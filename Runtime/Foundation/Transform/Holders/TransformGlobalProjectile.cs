#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using Apotheosis.Runtime.Foundation.Transform.Resolvers;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis.Runtime.Foundation.Transform.Holders
{
    /// <summary>嵌入原版 <see cref="Projectile"/> 与 <see cref="ModProjectile"/> 的逐实体变换宿主。</summary>
    public sealed class TransformGlobalProjectile : GlobalProjectile
    {
        private readonly record struct BindArgs(Projectile Target, TransformSyncMode Mode, TransformSyncFlags Flags, ITransformResolver<Projectile>? Resolver, bool? CleanupOnDispose);

        private static readonly Action<TransformNode, BindArgs> BindAction = static (node, a) =>
            node.BindProjectile(a.Target, a.Mode, a.Flags, a.Resolver, registerToWorld: true, a.CleanupOnDispose);

        private TransformSlot _slot;

        public override bool InstancePerEntity => true;

        /// <summary>当前弹幕是否已持有有效的 <see cref="TransformNode"/>。</summary>
        public bool HasTransform => _slot.HasTransform;

        /// <summary>零分配尝试获取当前弹幕的 <see cref="TransformNode"/>。</summary>
        public bool TryGetTransform([NotNullWhen(true)] out TransformNode? node) => _slot.TryGet(out node);

        /// <summary>获取或懒借出当前弹幕的 <see cref="TransformNode"/>（已存在时不覆盖既有同步配置）。</summary>
        public TransformNode GetOrCreateTransform(Projectile projectile)
            => _slot.TryGet(out TransformNode? existing) ? existing
                : _slot.GetOrCreate(new BindArgs(projectile, TransformSyncMode.Pull, TransformSyncFlags.All, null, null), static a => a.Target.Name, BindAction);

        /// <summary>获取或懒借出当前弹幕的 <see cref="TransformNode"/>，并显式指定同步配置。</summary>
        public TransformNode GetOrCreateTransform(Projectile projectile, TransformSyncMode syncMode, TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<Projectile>? resolver = null, bool? cleanupTargetOnDispose = null)
            => _slot.GetOrCreate(syncMode, syncFlags, resolver is not null || cleanupTargetOnDispose.HasValue,
                new BindArgs(projectile, syncMode, syncFlags, resolver, cleanupTargetOnDispose), static a => a.Target.Name, BindAction);

        /// <summary>注入自定义 <see cref="TransformNode"/> 作为该弹幕的唯一变换节点。</summary>
        public TNode SetTransform<TNode>(Projectile projectile, TNode customNode, TransformSyncMode syncMode = TransformSyncMode.Pull, TransformSyncFlags syncFlags = TransformSyncFlags.All,
            ITransformResolver<Projectile>? resolver = null, bool? cleanupTargetOnDispose = null, bool syncInitialFromTarget = true) where TNode : TransformNode
            => _slot.Set(customNode, syncInitialFromTarget, new BindArgs(projectile, syncMode, syncFlags, resolver, cleanupTargetOnDispose), BindAction);

        public override GlobalProjectile Clone(Projectile? from, Projectile to)
        {
            // 弹幕克隆时强制清空句柄，防止两个实体共享同一个 TransformNode
            var clone = (TransformGlobalProjectile)base.Clone(from, to);
            clone._slot.ResetHandle();
            return clone;
        }

        public override void OnKill(Projectile projectile, int timeLeft) => _slot.CleanupAndClear(TransformCleanupReason.TargetDestroyed);
    }
}
