#nullable enable

using System;
using Apotheosis.Runtime.Foundation.Transform.Resolvers;

namespace Apotheosis.Runtime.Foundation.Transform.Bindings
{
    /// <summary>
    /// 通用强类型实体绑定，适用于任意自定义类型配合自定义 <see cref="ITransformResolver{TTarget}"/>。
    /// <para>原版 <c>Player</c> / <c>NPC</c> / <c>Projectile</c> / <c>Item</c> 推荐使用无闭包池化绑定。</para>
    /// </summary>
    public sealed class TransformBinding<TTarget> : ITransformBinding where TTarget : class
    {
        private readonly Func<TTarget, bool>? _isAlivePredicate;
        private readonly Action<TTarget, TransformCleanupReason>? _cleanupAction;
        private readonly bool? _defaultCleanupOverride;

        public TTarget? Target { get; private set; }
        object? ITransformBinding.Target => Target;

        public ITransformResolver<TTarget> Resolver { get; }

        public TransformSyncMode SyncMode { get; set; }
        public TransformSyncFlags SyncFlags { get; set; }

        public bool IsAlive
            => Target is not null && (_isAlivePredicate is null || _isAlivePredicate(Target));

        public bool DefaultCleanupTargetOnDispose
            => _defaultCleanupOverride ?? (SyncMode == TransformSyncMode.Push || Target is ITransformable);

        public TransformBinding(
            TTarget target,
            ITransformResolver<TTarget> resolver,
            TransformSyncMode syncMode = TransformSyncMode.Manual,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            Func<TTarget, bool>? isAlivePredicate = null,
            Action<TTarget, TransformCleanupReason>? cleanupAction = null,
            bool? defaultCleanupTargetOnDispose = null)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            SyncMode = syncMode;
            SyncFlags = syncFlags;
            _isAlivePredicate = isAlivePredicate;
            _cleanupAction = cleanupAction;
            _defaultCleanupOverride = defaultCleanupTargetOnDispose;
        }

        public void SyncFromTarget(TransformNode node)
        {
            if (Target is not null && IsAlive)
            {
                Resolver.ReadFrom(node, Target, SyncFlags);
            }
        }

        public void ApplyToTarget(TransformNode node)
        {
            if (Target is not null && IsAlive)
            {
                Resolver.WriteTo(node, Target, SyncFlags);
            }
        }

        public void CleanupTarget(TransformCleanupReason reason)
        {
            if (Target is not null && IsAlive)
            {
                _cleanupAction?.Invoke(Target, reason);
            }
        }

        public void Release()
        {
            Target = null;
        }
    }
}
