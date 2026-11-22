#nullable enable

using System;
using System.Collections.Generic;
using Apotheosis.Runtime.Foundation.Transform.Resolvers;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis.Runtime.Foundation.Transform.Bindings
{
    /// <summary>
    /// 集中管理原版实体与自定义实体专用绑定对象池的清理入口。
    /// </summary>
    internal static class TransformBindingPools
    {
        public const int DefaultMaxPoolSize = 1024;

        public static void ClearAll()
        {
            PlayerTransformBinding.ClearPool();
            NpcTransformBinding.ClearPool();
            ProjectileTransformBinding.ClearPool();
            WorldItemTransformBinding.ClearPool();
            TransformableBinding.ClearPool();
        }
    }

    /// <summary>
    /// <see cref="Player"/> 专用池化无闭包绑定（永不销毁玩家实体）。
    /// </summary>
    public sealed class PlayerTransformBinding : ITransformBinding
    {
        private static readonly Stack<PlayerTransformBinding> _pool = new(16);

        private Player? _target;
        private ITransformResolver<Player>? _resolver;
        private int _whoAmI;
        private bool _inPool;

        public Player? Target => _target;
        object? ITransformBinding.Target => _target;

        public TransformSyncMode SyncMode { get; set; }
        public TransformSyncFlags SyncFlags { get; set; }

        public bool IsAlive
            => _target is { active: true }
            && (uint)_whoAmI < (uint)Main.player.Length
            && ReferenceEquals(Main.player[_whoAmI], _target);

        public bool DefaultCleanupTargetOnDispose => false;

        private PlayerTransformBinding() { }

        public static PlayerTransformBinding Rent(
            Player player,
            ITransformResolver<Player>? resolver = null,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All)
        {
            ArgumentNullException.ThrowIfNull(player);

            PlayerTransformBinding binding = _pool.Count > 0 ? _pool.Pop() : new PlayerTransformBinding();
            binding._inPool = false;
            binding._target = player;
            binding._resolver = resolver ?? PlayerTransformResolver.Default;
            binding._whoAmI = player.whoAmI;
            binding.SyncMode = syncMode;
            binding.SyncFlags = syncFlags;
            return binding;
        }

        public void SyncFromTarget(TransformNode node)
        {
            if (_target is not null && _resolver is not null && IsAlive)
            {
                _resolver.ReadFrom(node, _target, SyncFlags);
            }
        }

        public void ApplyToTarget(TransformNode node)
        {
            if (_target is not null && _resolver is not null && IsAlive)
            {
                _resolver.WriteTo(node, _target, SyncFlags);
            }
        }

        public void CleanupTarget(TransformCleanupReason reason)
        {
            // 玩家实体绝不随 Transform 清理被销毁
        }

        public void Release()
        {
            if (_inPool)
                return;

            _target = null;
            _resolver = null;
            _whoAmI = RuntimeConstants.InvalidIndex;
            SyncMode = TransformSyncMode.Manual;
            SyncFlags = TransformSyncFlags.None;
            _inPool = true;

            if (_pool.Count < TransformBindingPools.DefaultMaxPoolSize)
            {
                _pool.Push(this);
            }
        }

        internal static void ClearPool() => _pool.Clear();
    }

    /// <summary>
    /// <see cref="NPC"/> 专用池化无闭包绑定（内置 <c>whoAmI</c> / 引用 / <c>type</c> / <see cref="ModNPC"/> 防槽位复用校验）。
    /// </summary>
    public sealed class NpcTransformBinding : ITransformBinding
    {
        private static readonly Stack<NpcTransformBinding> _pool = new(64);

        private NPC? _target;
        private ITransformResolver<NPC>? _resolver;
        private int _whoAmI;
        private int _expectedType;
        private ModNPC? _expectedModNpc;
        private bool? _defaultCleanupOverride;
        private bool _inPool;

        public NPC? Target => _target;
        object? ITransformBinding.Target => _target;

        public TransformSyncMode SyncMode { get; set; }
        public TransformSyncFlags SyncFlags { get; set; }

        public bool IsAlive
            => _target is { active: true }
            && (uint)_whoAmI < (uint)Main.npc.Length
            && ReferenceEquals(Main.npc[_whoAmI], _target)
            && _target.type == _expectedType
            && ReferenceEquals(_target.ModNPC, _expectedModNpc);

        public bool DefaultCleanupTargetOnDispose
            => _defaultCleanupOverride ?? (SyncMode == TransformSyncMode.Push);

        private NpcTransformBinding() { }

        public static NpcTransformBinding Rent(
            NPC npc,
            ITransformResolver<NPC>? resolver = null,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            bool? cleanupTargetOnDispose = null)
        {
            ArgumentNullException.ThrowIfNull(npc);

            NpcTransformBinding binding = _pool.Count > 0 ? _pool.Pop() : new NpcTransformBinding();
            binding._inPool = false;
            binding._target = npc;
            binding._resolver = resolver ?? NpcTransformResolver.Default;
            binding._whoAmI = npc.whoAmI;
            binding._expectedType = npc.type;
            binding._expectedModNpc = npc.ModNPC;
            binding._defaultCleanupOverride = cleanupTargetOnDispose;
            binding.SyncMode = syncMode;
            binding.SyncFlags = syncFlags;
            return binding;
        }

        public void SyncFromTarget(TransformNode node)
        {
            if (_target is not null && _resolver is not null && IsAlive)
            {
                _resolver.ReadFrom(node, _target, SyncFlags);
            }
        }

        public void ApplyToTarget(TransformNode node)
        {
            if (_target is not null && _resolver is not null && IsAlive)
            {
                _resolver.WriteTo(node, _target, SyncFlags);
            }
        }

        public void CleanupTarget(TransformCleanupReason reason)
        {
            if (_target is not null && IsAlive)
            {
                _target.active = false;
            }
        }

        public void Release()
        {
            if (_inPool)
                return;

            _target = null;
            _resolver = null;
            _expectedModNpc = null;
            _whoAmI = RuntimeConstants.InvalidIndex;
            _expectedType = 0;
            _defaultCleanupOverride = null;
            SyncMode = TransformSyncMode.Manual;
            SyncFlags = TransformSyncFlags.None;
            _inPool = true;

            if (_pool.Count < TransformBindingPools.DefaultMaxPoolSize)
            {
                _pool.Push(this);
            }
        }

        internal static void ClearPool() => _pool.Clear();
    }

    /// <summary>
    /// <see cref="Projectile"/> 专用池化无闭包绑定（内置 <c>whoAmI</c> / 引用 / <c>type</c> / <see cref="ModProjectile"/> 防槽位复用校验）。
    /// </summary>
    public sealed class ProjectileTransformBinding : ITransformBinding
    {
        private static readonly Stack<ProjectileTransformBinding> _pool = new(128);

        private Projectile? _target;
        private ITransformResolver<Projectile>? _resolver;
        private int _whoAmI;
        private int _expectedType;
        private ModProjectile? _expectedModProj;
        private bool? _defaultCleanupOverride;
        private bool _inPool;

        public Projectile? Target => _target;
        object? ITransformBinding.Target => _target;

        public TransformSyncMode SyncMode { get; set; }
        public TransformSyncFlags SyncFlags { get; set; }

        public bool IsAlive
            => _target is { active: true }
            && (uint)_whoAmI < (uint)Main.projectile.Length
            && ReferenceEquals(Main.projectile[_whoAmI], _target)
            && _target.type == _expectedType
            && ReferenceEquals(_target.ModProjectile, _expectedModProj);

        public bool DefaultCleanupTargetOnDispose
            => _defaultCleanupOverride ?? (SyncMode == TransformSyncMode.Push);

        private ProjectileTransformBinding() { }

        public static ProjectileTransformBinding Rent(
            Projectile projectile,
            ITransformResolver<Projectile>? resolver = null,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            bool? cleanupTargetOnDispose = null)
        {
            ArgumentNullException.ThrowIfNull(projectile);

            ProjectileTransformBinding binding = _pool.Count > 0 ? _pool.Pop() : new ProjectileTransformBinding();
            binding._inPool = false;
            binding._target = projectile;
            binding._resolver = resolver ?? ProjectileTransformResolver.Default;
            binding._whoAmI = projectile.whoAmI;
            binding._expectedType = projectile.type;
            binding._expectedModProj = projectile.ModProjectile;
            binding._defaultCleanupOverride = cleanupTargetOnDispose;
            binding.SyncMode = syncMode;
            binding.SyncFlags = syncFlags;
            return binding;
        }

        public void SyncFromTarget(TransformNode node)
        {
            if (_target is not null && _resolver is not null && IsAlive)
            {
                _resolver.ReadFrom(node, _target, SyncFlags);
            }
        }

        public void ApplyToTarget(TransformNode node)
        {
            if (_target is not null && _resolver is not null && IsAlive)
            {
                _resolver.WriteTo(node, _target, SyncFlags);
            }
        }

        public void CleanupTarget(TransformCleanupReason reason)
        {
            if (_target is not null && IsAlive)
            {
                _target.Kill();
            }
        }

        public void Release()
        {
            if (_inPool)
                return;

            _target = null;
            _resolver = null;
            _expectedModProj = null;
            _whoAmI = RuntimeConstants.InvalidIndex;
            _expectedType = 0;
            _defaultCleanupOverride = null;
            SyncMode = TransformSyncMode.Manual;
            SyncFlags = TransformSyncFlags.None;
            _inPool = true;

            if (_pool.Count < TransformBindingPools.DefaultMaxPoolSize)
            {
                _pool.Push(this);
            }
        }

        internal static void ClearPool() => _pool.Clear();
    }

    /// <summary>
    /// <see cref="WorldItem"/>（掉落于世界中的物品实体）专用池化无闭包绑定（内置 <c>Main.item</c> 槽位与 <c>inner</c> 防复用校验）。
    /// </summary>
    public sealed class WorldItemTransformBinding : ITransformBinding
    {
        private static readonly Stack<WorldItemTransformBinding> _pool = new(64);

        private WorldItem? _target;
        private ITransformResolver<WorldItem>? _resolver;
        private int _whoAmI;
        private Item? _expectedInner;
        private int _expectedType;
        private bool? _defaultCleanupOverride;
        private bool _inPool;

        public WorldItem? Target => _target;
        object? ITransformBinding.Target => _target;

        public TransformSyncMode SyncMode { get; set; }
        public TransformSyncFlags SyncFlags { get; set; }

        public bool IsAlive
            => _target is { active: true, IsAir: false }
            && (uint)_whoAmI < (uint)Main.item.Length
            && ReferenceEquals(Main.item[_whoAmI], _target)
            && ReferenceEquals(_target.inner, _expectedInner)
            && _target.type == _expectedType;

        public bool DefaultCleanupTargetOnDispose
            => _defaultCleanupOverride ?? (SyncMode == TransformSyncMode.Push);

        private WorldItemTransformBinding() { }

        public static WorldItemTransformBinding Rent(
            WorldItem worldItem,
            ITransformResolver<WorldItem>? resolver = null,
            TransformSyncMode syncMode = TransformSyncMode.Pull,
            TransformSyncFlags syncFlags = TransformSyncFlags.All,
            bool? cleanupTargetOnDispose = null)
        {
            ArgumentNullException.ThrowIfNull(worldItem);

            WorldItemTransformBinding binding = _pool.Count > 0 ? _pool.Pop() : new WorldItemTransformBinding();
            binding._inPool = false;
            binding._target = worldItem;
            binding._resolver = resolver ?? WorldItemTransformResolver.Default;
            binding._whoAmI = worldItem.whoAmI;
            binding._expectedInner = worldItem.inner;
            binding._expectedType = worldItem.type;
            binding._defaultCleanupOverride = cleanupTargetOnDispose;
            binding.SyncMode = syncMode;
            binding.SyncFlags = syncFlags;
            return binding;
        }

        public void SyncFromTarget(TransformNode node)
        {
            if (_target is not null && _resolver is not null && IsAlive)
            {
                _resolver.ReadFrom(node, _target, SyncFlags);
            }
        }

        public void ApplyToTarget(TransformNode node)
        {
            if (_target is not null && _resolver is not null && IsAlive)
            {
                _resolver.WriteTo(node, _target, SyncFlags);
            }
        }

        public void CleanupTarget(TransformCleanupReason reason)
        {
            if (_target is not null && IsAlive)
            {
                _target.inner.TurnToAir();
            }
        }

        public void Release()
        {
            if (_inPool)
                return;

            _target = null;
            _resolver = null;
            _expectedInner = null;
            _whoAmI = RuntimeConstants.InvalidIndex;
            _expectedType = 0;
            _defaultCleanupOverride = null;
            SyncMode = TransformSyncMode.Manual;
            SyncFlags = TransformSyncFlags.None;
            _inPool = true;

            if (_pool.Count < TransformBindingPools.DefaultMaxPoolSize)
            {
                _pool.Push(this);
            }
        }

        internal static void ClearPool() => _pool.Clear();
    }

    /// <summary>
    /// 自定义 <see cref="ITransformable"/> 实体专用池化绑定（节点本身即唯一数据源，共用存活检测与级联清理）。
    /// </summary>
    public sealed class TransformableBinding : ITransformBinding
    {
        private static readonly Stack<TransformableBinding> _pool = new(64);

        private ITransformable? _target;
        private bool? _defaultCleanupOverride;
        private bool _inPool;

        public ITransformable? Target => _target;
        object? ITransformBinding.Target => _target;

        public TransformSyncMode SyncMode { get; set; }
        public TransformSyncFlags SyncFlags { get; set; }

        public bool IsAlive => _target is { IsAlive: true };

        public bool DefaultCleanupTargetOnDispose => _defaultCleanupOverride ?? true;

        private TransformableBinding() { }

        public static TransformableBinding Rent(
            ITransformable target,
            bool? cleanupTargetOnDispose = true)
        {
            ArgumentNullException.ThrowIfNull(target);

            TransformableBinding binding = _pool.Count > 0 ? _pool.Pop() : new TransformableBinding();
            binding._inPool = false;
            binding._target = target;
            binding._defaultCleanupOverride = cleanupTargetOnDispose;
            binding.SyncMode = TransformSyncMode.Manual;
            binding.SyncFlags = TransformSyncFlags.None;
            return binding;
        }

        public void SyncFromTarget(TransformNode node) { }

        public void ApplyToTarget(TransformNode node) { }

        public void CleanupTarget(TransformCleanupReason reason)
        {
            if (_target is { IsAlive: true })
            {
                _target.OnTransformCleanup(reason);
            }
        }

        public void Release()
        {
            if (_inPool)
                return;

            _target = null;
            _defaultCleanupOverride = null;
            SyncMode = TransformSyncMode.Manual;
            SyncFlags = TransformSyncFlags.None;
            _inPool = true;

            if (_pool.Count < TransformBindingPools.DefaultMaxPoolSize)
            {
                _pool.Push(this);
            }
        }

        internal static void ClearPool() => _pool.Clear();
    }
}
