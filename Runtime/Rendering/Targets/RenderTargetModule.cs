#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
#if DEBUG
using log4net;
#endif

namespace Apotheosis.Runtime.Rendering.Targets
{
    /// <summary>渲染目标注册、池化复用、持久目标与 MRT 绑定栈管理模块。</summary>
    public sealed class RenderTargetModule
    {
        private const int MaxTargets = byte.MaxValue + 1;
        private const int InitialScopeStackCapacity = 16;
        private const int DefaultIdleEvictFrames = 300;
        private const int DefaultMaxIdlePerBucket = 8;

        private struct ScopeFrame
        {
            public RenderTargetBinding[]? SavedBindings;
            public int Token;
        }

        private readonly IRenderTarget?[] _slots = new IRenderTarget?[MaxTargets];
        private readonly Dictionary<string, RenderTargetHandle> _nameToHandle = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PooledFrameRenderTarget> _channelsByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PersistentRenderTarget> _persistentByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<RenderTargetPoolKey, List<PooledEntry>> _idleBuckets = new();
        private readonly List<PooledEntry> _activeEntries = new(16);
        private readonly List<PooledFrameRenderTarget> _activeChannelsThisFrame = new(8);
        private readonly List<PooledFrameRenderTarget> _endFrameChannelScratch = new(8);
        private readonly List<RenderTargetPoolKey> _emptyBucketKeys = new(8);
        private readonly object _lock = new();

        // 渲染目标压栈与预分配 2~4 路 MRT 缓冲（仅主线程访问，Unload 时派发到主线程重置）
        private ScopeFrame[] _scopeStack;
        private int _scopeDepth;
        private int _nextScopeToken;
        private readonly RenderTargetBinding[] _mrt2 = new RenderTargetBinding[2];
        private readonly RenderTargetBinding[] _mrt3 = new RenderTargetBinding[3];
        private readonly RenderTargetBinding[] _mrt4 = new RenderTargetBinding[4];

        private int _count;
        private int _poolEpoch = 1;
        private int _resolutionEpoch = 1;
        private int _refWidth;
        private int _refHeight;
        private int _idleEvictFrames = DefaultIdleEvictFrames;
        private int _maxIdlePerBucket = DefaultMaxIdlePerBucket;

        private long _estimatedPoolVramBytes;
        private long _totalCreated;
        private long _totalReused;
        private long _totalEvicted;
        private long _resizeRebuilds;
        private int _suspectedLeakCount;
#if DEBUG
        private int _lastWarnedLeakCount;
        private ILog? _logger;
#endif

        public RenderTargetModule()
        {
            _scopeStack = new ScopeFrame[InitialScopeStackCapacity];
            RegisterBuiltInsLocked();
        }

        public RenderTargetHandle PreDrawOffscreen => new((byte)BuiltInRenderTarget.PreDrawOffscreen);
        public RenderTargetHandle AfterTiles => new((byte)BuiltInRenderTarget.AfterTiles);
        public RenderTargetHandle BeforeProjectiles => new((byte)BuiltInRenderTarget.BeforeProjectiles);
        public RenderTargetHandle AfterProjectiles => new((byte)BuiltInRenderTarget.AfterProjectiles);
        public RenderTargetHandle AfterDust => new((byte)BuiltInRenderTarget.AfterDust);
        public RenderTargetHandle AfterInterface => new((byte)BuiltInRenderTarget.AfterInterface);

        public int RegisteredCount => Volatile.Read(ref _count);

        /// <summary>空闲 RT 超过该帧数未被租借时自动释放（默认 300 帧）。</summary>
        public int IdleEvictFrames
        {
            get => Volatile.Read(ref _idleEvictFrames);
            set => Volatile.Write(ref _idleEvictFrames, Math.Max(1, value));
        }

        /// <summary>同一物理规格桶内最多保留的空闲 RT 数量（默认 8）。</summary>
        public int MaxIdlePerBucket
        {
            get => Volatile.Read(ref _maxIdlePerBucket);
            set => Volatile.Write(ref _maxIdlePerBucket, Math.Max(1, value));
        }

        /// <summary>通过强类型枚举获取内置渲染目标句柄。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RenderTargetHandle Get(BuiltInRenderTarget builtIn) => new((byte)builtIn);

        /// <summary>通过全局唯一名称获取已注册渲染目标句柄；未注册或已释放时抛出异常。</summary>
        public RenderTargetHandle Get(string name)
        {
            if (TryGetHandle(name, out RenderTargetHandle handle))
                return handle;

            throw new KeyNotFoundException($"未找到名为 \"{name}\" 的已注册渲染目标。");
        }

        /// <summary>尝试通过名称获取已注册渲染目标句柄。</summary>
        public bool TryGetHandle(string name, out RenderTargetHandle handle)
        {
            lock (_lock)
            {
                if (_nameToHandle.TryGetValue(name, out handle) && _slots[handle.Id] is not null)
                    return true;
            }

            handle = default;
            return false;
        }

        /// <summary>注册自定义 <see cref="IRenderTarget"/> 并分配 1 字节句柄（同名已注册时返回已有句柄）。</summary>
        public RenderTargetHandle Register(IRenderTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentException.ThrowIfNullOrWhiteSpace(target.Name);

            lock (_lock)
            {
                return RegisterLocked(target);
            }
        }

        /// <summary>通过 1 字节句柄解析已注册的 <see cref="IRenderTarget"/>。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(RenderTargetHandle handle, [NotNullWhen(true)] out IRenderTarget? target)
        {
            int id = handle.Id;
            if (id < Volatile.Read(ref _count))
            {
                target = Volatile.Read(ref _slots[id]);
                return target is not null;
            }

            target = null;
            return false;
        }

        // ==================== 模式 1：瞬时租借 ====================

        /// <summary>从池中租借一个瞬时渲染目标（仅主线程调用，使用完毕通过 <c>using</c> 立即还池）。</summary>
        public PooledRenderTarget Rent(in RenderTargetDescriptor descriptor)
        {
            GraphicsDevice gd = GetRequiredGraphicsDevice("RenderTargetModule.Rent");
            return Rent(gd, in descriptor);
        }

        /// <summary>从池中租借一个瞬时渲染目标（仅主线程调用，使用完毕通过 <c>using</c> 立即还池）。</summary>
        public PooledRenderTarget Rent(GraphicsDevice graphicsDevice, in RenderTargetDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(graphicsDevice);
            descriptor.Validate(nameof(descriptor));
            RenderManager.Shaders.AssertMainThread("RenderTargetModule.Rent");

            if (RenderManager.IsUnloading || graphicsDevice.IsDisposed)
                return default;

            if (!RentEntryInternal(graphicsDevice, in descriptor, isTransientLease: true, out PooledEntry? entry, out int epoch, out int token))
                return default;

            return new PooledRenderTarget(this, entry, epoch, token);
        }

        // ==================== 模式 2：命名帧通道 ====================

        /// <summary>注册命名帧通道（默认当帧首绑清屏为透明），返回 1 字节句柄供 <c>RenderCommand</c> 直接使用。</summary>
        public RenderTargetHandle RegisterPooledChannel(string name, RenderTiming timing, in RenderTargetDescriptor descriptor)
            => RegisterPooledChannel(name, timing, in descriptor, Color.Transparent);

        /// <summary>注册命名帧通道（支持自定义首绑清屏色；若需跨多阶段重复写入同一通道请使用 <c>PreserveContents</c>）。</summary>
        public RenderTargetHandle RegisterPooledChannel(string name, RenderTiming timing, in RenderTargetDescriptor descriptor, Color? autoClearColor)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            descriptor.Validate(nameof(descriptor));

            lock (_lock)
            {
                if (_channelsByName.TryGetValue(name, out PooledFrameRenderTarget? existing))
                {
                    if (existing.Descriptor != descriptor || existing.Timing != timing || existing.AutoClearColor != autoClearColor)
                        throw new InvalidOperationException($"命名帧通道 \"{name}\" 已按不同规格注册，禁止以冲突参数重复注册。");

                    return existing.Handle;
                }

                if (_nameToHandle.TryGetValue(name, out RenderTargetHandle occupied) && _slots[occupied.Id] is not null)
                    throw new InvalidOperationException($"渲染目标名称 \"{name}\" 已被非帧通道目标占用。");

                var channel = new PooledFrameRenderTarget(this, name, timing, in descriptor, autoClearColor);
                RenderTargetHandle handle = RegisterLocked(channel);
                channel.Handle = handle;
                _channelsByName[name] = channel;
#if DEBUG
                if (timing is RenderTiming.AfterTiles or RenderTiming.BeforeProjectiles or RenderTiming.AfterProjectiles or RenderTiming.AfterDust)
                {
                    _logger?.Warn($"[RenderTargetModule] 离屏通道 \"{name}\" 注册在 {timing}（位于 Main.screenTarget 世界捕获中途），" +
                                  "FNA 重绑 DiscardContents 目标可能清空屏幕，请优先改用 RenderTiming.PreDrawOffscreen。");
                }
#endif
                return handle;
            }
        }

        /// <summary>按名称获取已注册的命名帧通道。</summary>
        public bool TryGetChannel(string name, [NotNullWhen(true)] out PooledFrameRenderTarget? channel)
        {
            lock (_lock)
            {
                return _channelsByName.TryGetValue(name, out channel);
            }
        }

        /// <summary>按 1 字节句柄获取已注册的命名帧通道。</summary>
        public bool TryGetChannel(RenderTargetHandle handle, [NotNullWhen(true)] out PooledFrameRenderTarget? channel)
        {
            if (TryGet(handle, out IRenderTarget? target) && target is PooledFrameRenderTarget pooled)
            {
                channel = pooled;
                return true;
            }

            channel = null;
            return false;
        }

        // ==================== 模式 3：跨帧持久 RT ====================

        /// <summary>获取或创建按键索引的跨帧持久渲染目标。</summary>
        public PersistentRenderTarget AcquirePersistent(string key, in RenderTargetDescriptor descriptor, RenderTiming timing = RenderTiming.PreDrawOffscreen)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            descriptor.Validate(nameof(descriptor));

            // 纯 CPU 注册，底层 RenderTarget2D 延迟到主线程首次 Resolve 时惰性分配
            lock (_lock)
            {
                if (_persistentByKey.TryGetValue(key, out PersistentRenderTarget? existing))
                {
                    if (existing.Descriptor != descriptor || existing.Timing != timing)
                        throw new InvalidOperationException($"跨帧持久 RT \"{key}\" 已按不同规格创建，请先调用 ReleasePersistent 或保持一致参数。");

                    return existing;
                }

                var persistent = new PersistentRenderTarget(this, key, in descriptor, timing);
                _persistentByKey[key] = persistent;
                return persistent;
            }
        }

        /// <summary>尝试按键获取已存在的跨帧持久渲染目标。</summary>
        public bool TryGetPersistent(string key, [NotNullWhen(true)] out PersistentRenderTarget? target)
        {
            lock (_lock)
            {
                return _persistentByKey.TryGetValue(key, out target);
            }
        }

        /// <summary>释放并销毁指定键的跨帧持久渲染目标。</summary>
        public bool ReleasePersistent(string key)
        {
            PersistentRenderTarget? removed;
            lock (_lock)
            {
                if (!_persistentByKey.Remove(key, out removed))
                    return false;

                // 若曾通过 Targets.Register 绑定过 1B 槽位，同步清空避免残留已销毁僵尸槽位
                if (_nameToHandle.TryGetValue(key, out RenderTargetHandle handle) && ReferenceEquals(_slots[handle.Id], removed))
                    Volatile.Write(ref _slots[handle.Id], null);
            }

            removed.Dispose();
            return true;
        }

        // ==================== 渲染目标作用域栈（1~4 路 MRT 预分配压栈） ====================

        /// <summary>压栈切换至单个渲染目标（传 <c>null</c> 表示后备缓冲），退出作用域自动恢复原绑定。</summary>
        public RenderTargetScope Push(RenderTarget2D? target)
            => PushScopeInternal(GetRequiredGraphicsDevice("RenderTargetModule.Push"), 1, target, null, null, null);

        /// <summary>压栈切换至单个渲染目标（传 <c>null</c> 表示后备缓冲），退出作用域自动恢复原绑定。</summary>
        public RenderTargetScope Push(GraphicsDevice graphicsDevice, RenderTarget2D? target)
            => PushScopeInternal(graphicsDevice, 1, target, null, null, null);

        /// <summary>压栈切换至 2 路 MRT 渲染目标，退出作用域自动恢复原绑定。</summary>
        public RenderTargetScope Push(RenderTarget2D rt0, RenderTarget2D rt1)
            => PushScopeInternal(GetRequiredGraphicsDevice("RenderTargetModule.Push"), 2, rt0, rt1, null, null);

        /// <summary>压栈切换至 2 路 MRT 渲染目标，退出作用域自动恢复原绑定。</summary>
        public RenderTargetScope Push(GraphicsDevice graphicsDevice, RenderTarget2D rt0, RenderTarget2D rt1)
            => PushScopeInternal(graphicsDevice, 2, rt0, rt1, null, null);

        /// <summary>压栈切换至 3 路 MRT 渲染目标，退出作用域自动恢复原绑定。</summary>
        public RenderTargetScope Push(RenderTarget2D rt0, RenderTarget2D rt1, RenderTarget2D rt2)
            => PushScopeInternal(GetRequiredGraphicsDevice("RenderTargetModule.Push"), 3, rt0, rt1, rt2, null);

        /// <summary>压栈切换至 3 路 MRT 渲染目标，退出作用域自动恢复原绑定。</summary>
        public RenderTargetScope Push(GraphicsDevice graphicsDevice, RenderTarget2D rt0, RenderTarget2D rt1, RenderTarget2D rt2)
            => PushScopeInternal(graphicsDevice, 3, rt0, rt1, rt2, null);

        /// <summary>压栈切换至 4 路 MRT 渲染目标，退出作用域自动恢复原绑定。</summary>
        public RenderTargetScope Push(RenderTarget2D rt0, RenderTarget2D rt1, RenderTarget2D rt2, RenderTarget2D rt3)
            => PushScopeInternal(GetRequiredGraphicsDevice("RenderTargetModule.Push"), 4, rt0, rt1, rt2, rt3);

        /// <summary>压栈切换至 4 路 MRT 渲染目标，退出作用域自动恢复原绑定。</summary>
        public RenderTargetScope Push(GraphicsDevice graphicsDevice, RenderTarget2D rt0, RenderTarget2D rt1, RenderTarget2D rt2, RenderTarget2D rt3)
            => PushScopeInternal(graphicsDevice, 4, rt0, rt1, rt2, rt3);

        // ==================== 遥测与主动回收 ====================

        /// <summary>立即在主线程释放池中全部空闲 RT，返回释放数量。</summary>
        public int EvictIdleTargets()
        {
            RenderManager.Shaders.AssertMainThread("RenderTargetModule.EvictIdleTargets");

            int evicted = 0;
            lock (_lock)
            {
                foreach (List<PooledEntry> bucket in _idleBuckets.Values)
                {
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        DisposePooledEntryOnMainThreadLocked(bucket[i]);
                        evicted++;
                    }
                    bucket.Clear();
                }

                _idleBuckets.Clear();
                _totalEvicted += evicted;
            }

            return evicted;
        }

        /// <summary>获取当前 RT 池与持久目标的统计快照。</summary>
        public RenderTargetStats GetStats()
        {
            lock (_lock)
            {
                int activeTransient = 0;
                int activeChannel = 0;
                for (int i = 0; i < _activeEntries.Count; i++)
                {
                    if (_activeEntries[i].IsTransientLease)
                        activeTransient++;
                    else
                        activeChannel++;
                }

                int idleCount = 0;
                foreach (List<PooledEntry> bucket in _idleBuckets.Values)
                    idleCount += bucket.Count;

                long persistentBytes = 0;
                foreach (PersistentRenderTarget persistent in _persistentByKey.Values)
                    persistentBytes += persistent.EstimatedBytes;

                return new RenderTargetStats(_refWidth, _refHeight, activeTransient, activeChannel, idleCount, _channelsByName.Count, _persistentByKey.Count,
                    _estimatedPoolVramBytes, persistentBytes, _totalCreated, _totalReused, _totalEvicted, _resizeRebuilds, _suspectedLeakCount);
            }
        }

        // ==================== 内部协作接口 ====================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryGetTiming(RenderTargetHandle handle, out RenderTiming timing)
        {
            if (TryGet(handle, out IRenderTarget? target))
            {
                timing = target.Timing;
                return true;
            }

            timing = default;
            return false;
        }

        internal bool IsLeaseValid(PooledEntry entry, int poolEpoch, int leaseToken)
        {
            return Volatile.Read(ref _poolEpoch) == poolEpoch && entry.InUse && entry.LeaseToken == leaseToken && !entry.IsDisposed && !entry.Target.IsDisposed;
        }

        internal bool TryRentForChannel(GraphicsDevice gd, in RenderTargetDescriptor descriptor, [NotNullWhen(true)] out PooledEntry? entry, out int epoch, out int token)
        {
            return RentEntryInternal(gd, in descriptor, isTransientLease: false, out entry, out epoch, out token);
        }

        internal void TrackActiveChannel(PooledFrameRenderTarget channel)
        {
            lock (_lock)
            {
                if (!RenderManager.IsUnloading)
                    _activeChannelsThisFrame.Add(channel);
            }
        }

        internal RenderTargetPoolKey ResolveDescriptorKey(GraphicsDevice gd, in RenderTargetDescriptor descriptor)
        {
            UpdateReferenceResolution(gd);
            lock (_lock)
            {
                return descriptor.ResolveKey(Math.Max(1, _refWidth), Math.Max(1, _refHeight));
            }
        }

        internal void NotifyPersistentRebuilt()
        {
            lock (_lock)
            {
                _resizeRebuilds++;
            }
        }

        internal void ReturnLease(PooledEntry entry, int poolEpoch, int leaseToken)
        {
            // 卸载或重载后的陈旧句柄静默忽略，防止污染新周期池状态
            if (Volatile.Read(ref _poolEpoch) != poolEpoch)
                return;

            RenderTarget2D? toDispose = null;

            lock (_lock)
            {
                if (_poolEpoch != poolEpoch || !entry.InUse || entry.LeaseToken != leaseToken)
                    return;

                entry.InUse = false;
                entry.LeaseToken++;
                RemoveActiveEntryLocked(entry);

                if (entry.IsDisposed || entry.Target.IsDisposed)
                {
                    if (!entry.IsDisposed)
                    {
                        entry.IsDisposed = true;
                        _estimatedPoolVramBytes = Math.Max(0, _estimatedPoolVramBytes - entry.EstimatedBytes);
                    }
                    return;
                }

                // 租借期间若发生窗口缩放，旧分辨率屏幕比例 RT 归还时直接淘汰，避免在池中滞留 300 帧
                if (entry.IsScreenRelative && entry.ResolutionEpoch != _resolutionEpoch)
                {
                    toDispose = MarkEntryForDisposalLocked(entry);
                    _totalEvicted++;
                }
                else
                {
                    if (!_idleBuckets.TryGetValue(entry.Key, out List<PooledEntry>? bucket))
                    {
                        bucket = new List<PooledEntry>(4);
                        _idleBuckets[entry.Key] = bucket;
                    }

                    if (bucket.Count >= Volatile.Read(ref _maxIdlePerBucket))
                    {
                        toDispose = MarkEntryForDisposalLocked(entry);
                        _totalEvicted++;
                    }
                    else
                    {
                        entry.LastReturnedFrame = RenderManager.FrameId;
                        bucket.Add(entry);
                    }
                }
            }

            // 兼容后台 ModSystem.Unload() 触发的归还：真正需要释放 GPU 资源时派发到主线程
            if (toDispose is not null && !toDispose.IsDisposed)
            {
                if (RenderManager.Shaders.IsOnMainThread)
                    toDispose.Dispose();
                else
                    Main.QueueMainThreadAction(() => { if (!toDispose.IsDisposed) toDispose.Dispose(); });
            }
        }

        internal void PopScope(GraphicsDevice gd, int expectedDepth, int expectedToken)
        {
            RenderManager.Shaders.AssertMainThread("RenderTargetModule.PopScope");

            if (expectedToken == 0 || expectedDepth < 0 || expectedDepth >= _scopeDepth)
                return;

            ref ScopeFrame targetFrame = ref _scopeStack[expectedDepth];
            if (targetFrame.Token != expectedToken)
                return;

            // 若内层作用域遗漏释放，自动回卷清理内层栈帧并恢复当前外层保存的绑定，避免整帧卡死
            if (expectedDepth < _scopeDepth - 1)
            {
#if DEBUG
                int leakedCount = (_scopeDepth - 1) - expectedDepth;
                _logger?.Warn($"[RenderTargetModule] 检测到 {leakedCount} 层嵌套 RenderTargetScope 未按序释放，已自动回卷恢复外层绑定。");
#endif
                for (int i = _scopeDepth - 1; i > expectedDepth; i--)
                {
                    _scopeStack[i].SavedBindings = null;
                    _scopeStack[i].Token = 0;
                }
            }

            RenderTargetBinding[]? saved = targetFrame.SavedBindings;
            targetFrame.SavedBindings = null;
            targetFrame.Token = 0;
            _scopeDepth = expectedDepth;

            if (!gd.IsDisposed)
            {
                // 若先前保存的外层目标已被释放（如中途切分辨率重建），安全回退到后备缓冲
                bool canRestore = saved is { Length: > 0 };
                if (canRestore)
                {
                    for (int i = 0; i < saved!.Length; i++)
                    {
                        if (saved[i].RenderTarget is null || saved[i].RenderTarget.IsDisposed)
                        {
                            canRestore = false;
                            break;
                        }
                    }
                }

                if (canRestore)
                    gd.SetRenderTargets(saved);
                else
                    gd.SetRenderTarget(null);
            }

            RenderManager.InvalidateViewContext();
        }

        internal void EndFrame()
        {
            RenderManager.Shaders.AssertMainThread("RenderTargetModule.EndFrame");

            // 1. 锁内摘取本帧活跃命名帧通道，锁外归还物理 RT
            _endFrameChannelScratch.Clear();
            lock (_lock)
            {
                if (_activeChannelsThisFrame.Count > 0)
                {
                    _endFrameChannelScratch.AddRange(_activeChannelsThisFrame);
                    _activeChannelsThisFrame.Clear();
                }
            }

            for (int i = 0; i < _endFrameChannelScratch.Count; i++)
                _endFrameChannelScratch[i].EndFrameReturn();
            _endFrameChannelScratch.Clear();

            ulong currentFrame = RenderManager.FrameId;
            ulong evictThreshold = (ulong)Volatile.Read(ref _idleEvictFrames);

            lock (_lock)
            {
                // 2. 淘汰连续闲置超过阈值的空闲 RT，并裁剪空桶避免字典膨胀
                foreach (List<PooledEntry> bucket in _idleBuckets.Values)
                {
                    for (int i = bucket.Count - 1; i >= 0; i--)
                    {
                        PooledEntry entry = bucket[i];
                        bool stale = currentFrame >= entry.LastReturnedFrame && (currentFrame - entry.LastReturnedFrame) > evictThreshold;
                        if (stale || entry.Target.IsDisposed)
                        {
                            bucket.RemoveAt(i);
                            DisposePooledEntryOnMainThreadLocked(entry);
                            _totalEvicted++;
                        }
                    }
                }
                PruneEmptyBucketsLocked();

                // 3. 统计跨帧未归还的瞬时 Rent 租借（命名帧通道已在步骤 1 归还，持久 RT 不在 _activeEntries 中）
                int transientLeaks = 0;
                for (int i = 0; i < _activeEntries.Count; i++)
                {
                    if (_activeEntries[i].IsTransientLease)
                        transientLeaks++;
                }

                _suspectedLeakCount = transientLeaks;
#if DEBUG
                if (transientLeaks > 0 && transientLeaks != _lastWarnedLeakCount)
                {
                    _lastWarnedLeakCount = transientLeaks;
                    _logger?.Warn($"[RenderTargetModule] 检测到 {transientLeaks} 个瞬时 Rent RenderTarget 跨帧未归还，请检查是否遗漏 using / Dispose()。");
                }
                else if (transientLeaks == 0)
                {
                    _lastWarnedLeakCount = 0;
                }
#endif
            }
        }

#if DEBUG
        internal void SetLogger(ILog? logger) => _logger = logger;
#endif

        internal void ResetForLoad()
        {
            lock (_lock)
            {
                if (_count == 0)
                    RegisterBuiltInsLocked();
            }
        }

        internal void Unload()
        {
            List<IRenderTarget>? targetsToDispose = null;
            List<RenderTarget2D>? poolRtsToDispose = null;

            lock (_lock)
            {
                Interlocked.Increment(ref _poolEpoch);
                _resolutionEpoch++;

                int count = _count;
                for (int i = 0; i < count; i++)
                {
                    IRenderTarget? target = _slots[i];
                    // PersistentRenderTarget 统一由 _persistentByKey 收集释放，避免重复入队
                    if (target is not null && !target.IsMainScreen && target is not PersistentRenderTarget)
                    {
                        targetsToDispose ??= new List<IRenderTarget>();
                        targetsToDispose.Add(target);
                    }
                    _slots[i] = null;
                }

                foreach (PersistentRenderTarget persistent in _persistentByKey.Values)
                {
                    targetsToDispose ??= new List<IRenderTarget>();
                    targetsToDispose.Add(persistent);
                }

                for (int i = 0; i < _activeEntries.Count; i++)
                {
                    PooledEntry entry = _activeEntries[i];
                    entry.InUse = false;
                    entry.ActiveIndex = -1;
                    entry.IsDisposed = true;
                    poolRtsToDispose ??= new List<RenderTarget2D>();
                    poolRtsToDispose.Add(entry.Target);
                }

                foreach (List<PooledEntry> bucket in _idleBuckets.Values)
                {
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        PooledEntry entry = bucket[i];
                        entry.IsDisposed = true;
                        poolRtsToDispose ??= new List<RenderTarget2D>();
                        poolRtsToDispose.Add(entry.Target);
                    }
                }

                _nameToHandle.Clear();
                _channelsByName.Clear();
                _persistentByKey.Clear();
                _activeEntries.Clear();
                _idleBuckets.Clear();
                _activeChannelsThisFrame.Clear();
                _emptyBucketKeys.Clear();
                Volatile.Write(ref _count, 0);

                _refWidth = 0;
                _refHeight = 0;
                _estimatedPoolVramBytes = 0;
                _totalCreated = 0;
                _totalReused = 0;
                _totalEvicted = 0;
                _resizeRebuilds = 0;
                _suspectedLeakCount = 0;
#if DEBUG
                _lastWarnedLeakCount = 0;
#endif
            }

            // 仅主线程访问的 _scopeStack / _mrt2..4 以及 GPU 释放全部派发至主线程执行，杜绝后台线程竞态
            void DisposeAndResetOnMainThread()
            {
                Array.Clear(_scopeStack);
                _scopeDepth = 0;
                Array.Clear(_mrt2);
                Array.Clear(_mrt3);
                Array.Clear(_mrt4);
                _endFrameChannelScratch.Clear();

                if (targetsToDispose is not null)
                {
                    for (int i = 0; i < targetsToDispose.Count; i++)
                    {
                        try { targetsToDispose[i].Dispose(); }
                        catch { /* 卸载期忽略清理异常 */ }
                    }
                }

                if (poolRtsToDispose is not null)
                {
                    for (int i = 0; i < poolRtsToDispose.Count; i++)
                    {
                        try
                        {
                            RenderTarget2D rt = poolRtsToDispose[i];
                            if (!rt.IsDisposed)
                                rt.Dispose();
                        }
                        catch { /* 卸载期忽略清理异常 */ }
                    }
                }
            }

            if (RenderManager.Shaders.IsOnMainThread)
                DisposeAndResetOnMainThread();
            else
                Main.QueueMainThreadAction(DisposeAndResetOnMainThread);
        }

        // ==================== 内部实现 ====================

        private RenderTargetHandle RegisterLocked(IRenderTarget target)
        {
            if (_nameToHandle.TryGetValue(target.Name, out RenderTargetHandle existing))
            {
                // 同名槽位若已被 ReleasePersistent 释放，直接覆写复用原 1B 槽位，避免泄漏 Handle ID
                if (_slots[existing.Id] is null)
                    Volatile.Write(ref _slots[existing.Id], target);

                return existing;
            }

            int slot = _count;
            if (slot >= MaxTargets)
                throw new InvalidOperationException($"渲染目标数量已达 1 字节 RenderTargetHandle 上限 ({MaxTargets})。");

            var handle = new RenderTargetHandle((byte)slot);
            Volatile.Write(ref _slots[slot], target);
            Volatile.Write(ref _count, slot + 1);
            _nameToHandle[target.Name] = handle;
            return handle;
        }

        private bool RentEntryInternal(GraphicsDevice gd, in RenderTargetDescriptor descriptor, bool isTransientLease, [NotNullWhen(true)] out PooledEntry? entry, out int epoch, out int token)
        {
            UpdateReferenceResolution(gd);

            lock (_lock)
            {
                if (RenderManager.IsUnloading || gd.IsDisposed)
                {
                    entry = null;
                    epoch = 0;
                    token = 0;
                    return false;
                }

                epoch = _poolEpoch;
                RenderTargetPoolKey key = descriptor.ResolveKey(Math.Max(1, _refWidth), Math.Max(1, _refHeight));

                // 优先从同规格空闲栈顶弹出（LIFO 保持热点对象在 GPU 显存中）
                if (_idleBuckets.TryGetValue(key, out List<PooledEntry>? bucket))
                {
                    while (bucket.Count > 0)
                    {
                        int lastIdx = bucket.Count - 1;
                        PooledEntry candidate = bucket[lastIdx];
                        bucket.RemoveAt(lastIdx);

                        if (candidate.IsDisposed || candidate.Target.IsDisposed)
                        {
                            DisposePooledEntryOnMainThreadLocked(candidate);
                            continue;
                        }

                        candidate.IsScreenRelative = descriptor.IsScreenRelative;
                        candidate.ResolutionEpoch = _resolutionEpoch;
                        candidate.InUse = true;
                        candidate.IsTransientLease = isTransientLease;
                        candidate.LeasedFrame = RenderManager.FrameId;
                        candidate.LeaseToken++;
                        AddActiveEntryLocked(candidate);

                        _totalReused++;
                        entry = candidate;
                        token = candidate.LeaseToken;
                        return true;
                    }
                }

                var rt = new RenderTarget2D(gd, key.Width, key.Height, key.MipMap, key.Format, key.DepthFormat, key.MultiSampleCount, key.Usage);
                var created = new PooledEntry(key, rt, descriptor.IsScreenRelative)
                {
                    ResolutionEpoch = _resolutionEpoch,
                    InUse = true,
                    IsTransientLease = isTransientLease,
                    LeasedFrame = RenderManager.FrameId,
                    LeaseToken = 1,
                };

                AddActiveEntryLocked(created);
                _totalCreated++;
                _estimatedPoolVramBytes += created.EstimatedBytes;

                entry = created;
                token = created.LeaseToken;
                return true;
            }
        }

        private void UpdateReferenceResolution(GraphicsDevice gd)
        {
            int width;
            int height;

            RenderTarget2D? screenTarget = Main.screenTarget;
            if (screenTarget is not null && !screenTarget.IsDisposed && screenTarget.Width > 0 && screenTarget.Height > 0)
            {
                width = screenTarget.Width;
                height = screenTarget.Height;
            }
            else
            {
                PresentationParameters pp = gd.PresentationParameters;
                width = Math.Max(1, pp.BackBufferWidth);
                height = Math.Max(1, pp.BackBufferHeight);
            }

            lock (_lock)
            {
                if (RenderManager.IsUnloading || (_refWidth == width && _refHeight == height))
                    return;

                bool wasInitialized = _refWidth > 0 && _refHeight > 0;
                _refWidth = width;
                _refHeight = height;

                if (!wasInitialized)
                    return;

                // 窗口分辨率变化时递增代际并立即清理旧尺寸的屏幕比例空闲 RT，固定像素 RT 保持不动
                _resolutionEpoch++;
                _resizeRebuilds++;
                foreach (List<PooledEntry> bucket in _idleBuckets.Values)
                {
                    for (int i = bucket.Count - 1; i >= 0; i--)
                    {
                        PooledEntry entry = bucket[i];
                        if (entry.IsScreenRelative)
                        {
                            bucket.RemoveAt(i);
                            DisposePooledEntryOnMainThreadLocked(entry);
                            _totalEvicted++;
                        }
                    }
                }
                PruneEmptyBucketsLocked();
            }
        }

        private RenderTargetScope PushScopeInternal(GraphicsDevice gd, int targetCount, RenderTarget2D? rt0, RenderTarget2D? rt1, RenderTarget2D? rt2, RenderTarget2D? rt3)
        {
            ArgumentNullException.ThrowIfNull(gd);
            RenderManager.Shaders.AssertMainThread("RenderTargetModule.Push");

            if (RenderManager.IsUnloading || gd.IsDisposed)
                return default;

            // 先校验全部目标有效性再操作 GPU 与栈状态，避免异常后栈深度损坏
            if (targetCount == 1)
            {
                if (rt0 is { IsDisposed: true })
                    throw new ObjectDisposedException(nameof(rt0), "无法绑定已释放的 RenderTarget2D。");
            }
            else
            {
                ValidateMrtTarget(rt0, nameof(rt0));
                ValidateMrtTarget(rt1, nameof(rt1));
                if (targetCount >= 3)
                    ValidateMrtTarget(rt2, nameof(rt2));
                if (targetCount >= 4)
                    ValidateMrtTarget(rt3, nameof(rt3));
            }

            RenderTargetBinding[] prevBindings = gd.GetRenderTargets();

            switch (targetCount)
            {
                case 1:
                    gd.SetRenderTarget(rt0);
                    break;
                case 2:
                    try
                    {
                        _mrt2[0] = new RenderTargetBinding(rt0!);
                        _mrt2[1] = new RenderTargetBinding(rt1!);
                        gd.SetRenderTargets(_mrt2);
                    }
                    finally { Array.Clear(_mrt2); }
                    break;
                case 3:
                    try
                    {
                        _mrt3[0] = new RenderTargetBinding(rt0!);
                        _mrt3[1] = new RenderTargetBinding(rt1!);
                        _mrt3[2] = new RenderTargetBinding(rt2!);
                        gd.SetRenderTargets(_mrt3);
                    }
                    finally { Array.Clear(_mrt3); }
                    break;
                case 4:
                    try
                    {
                        _mrt4[0] = new RenderTargetBinding(rt0!);
                        _mrt4[1] = new RenderTargetBinding(rt1!);
                        _mrt4[2] = new RenderTargetBinding(rt2!);
                        _mrt4[3] = new RenderTargetBinding(rt3!);
                        gd.SetRenderTargets(_mrt4);
                    }
                    finally { Array.Clear(_mrt4); }
                    break;
            }

            if (_scopeDepth >= _scopeStack.Length)
                Array.Resize(ref _scopeStack, _scopeStack.Length * 2);

            int depth = _scopeDepth++;
            int token = ++_nextScopeToken;
            if (token == 0)
                token = ++_nextScopeToken;

            _scopeStack[depth].SavedBindings = prevBindings;
            _scopeStack[depth].Token = token;

            RenderManager.InvalidateViewContext();
            return new RenderTargetScope(this, gd, depth, token);
        }

        private static void ValidateMrtTarget(RenderTarget2D? rt, string paramName)
        {
            ArgumentNullException.ThrowIfNull(rt, paramName);
            if (rt.IsDisposed)
                throw new ObjectDisposedException(paramName, "无法将已释放的 RenderTarget2D 绑定至 MRT。");
        }

        private void AddActiveEntryLocked(PooledEntry entry)
        {
            entry.ActiveIndex = _activeEntries.Count;
            _activeEntries.Add(entry);
        }

        private void RemoveActiveEntryLocked(PooledEntry entry)
        {
            int idx = entry.ActiveIndex;
            if (idx < 0 || idx >= _activeEntries.Count)
                return;

            int lastIdx = _activeEntries.Count - 1;
            if (idx != lastIdx)
            {
                PooledEntry last = _activeEntries[lastIdx];
                _activeEntries[idx] = last;
                last.ActiveIndex = idx;
            }

            _activeEntries.RemoveAt(lastIdx);
            entry.ActiveIndex = -1;
        }

        private RenderTarget2D? MarkEntryForDisposalLocked(PooledEntry entry)
        {
            if (entry.IsDisposed)
                return null;

            entry.IsDisposed = true;
            _estimatedPoolVramBytes = Math.Max(0, _estimatedPoolVramBytes - entry.EstimatedBytes);
            return entry.Target;
        }

        private void DisposePooledEntryOnMainThreadLocked(PooledEntry entry)
        {
            RenderTarget2D? rt = MarkEntryForDisposalLocked(entry);
            if (rt is not null && !rt.IsDisposed)
                rt.Dispose();
        }

        private void PruneEmptyBucketsLocked()
        {
            _emptyBucketKeys.Clear();
            foreach (KeyValuePair<RenderTargetPoolKey, List<PooledEntry>> kv in _idleBuckets)
            {
                if (kv.Value.Count == 0)
                    _emptyBucketKeys.Add(kv.Key);
            }

            for (int i = 0; i < _emptyBucketKeys.Count; i++)
                _idleBuckets.Remove(_emptyBucketKeys[i]);

            _emptyBucketKeys.Clear();
        }

        private static GraphicsDevice GetRequiredGraphicsDevice(string caller)
        {
            GraphicsDevice? gd = Main.graphics?.GraphicsDevice;
            if (gd is null || gd.IsDisposed)
                throw new InvalidOperationException($"{caller} 调用时 GraphicsDevice 尚未就绪或已释放。");

            return gd;
        }

        private void RegisterBuiltInsLocked()
        {
            RegisterFixedBuiltInLocked(BuiltInRenderTarget.PreDrawOffscreen, BuiltInRenderTargetNames.PreDrawOffscreen, RenderTiming.PreDrawOffscreen);
            RegisterFixedBuiltInLocked(BuiltInRenderTarget.AfterTiles, BuiltInRenderTargetNames.AfterTiles, RenderTiming.AfterTiles);
            RegisterFixedBuiltInLocked(BuiltInRenderTarget.BeforeProjectiles, BuiltInRenderTargetNames.BeforeProjectiles, RenderTiming.BeforeProjectiles);
            RegisterFixedBuiltInLocked(BuiltInRenderTarget.AfterProjectiles, BuiltInRenderTargetNames.AfterProjectiles, RenderTiming.AfterProjectiles);
            RegisterFixedBuiltInLocked(BuiltInRenderTarget.AfterDust, BuiltInRenderTargetNames.AfterDust, RenderTiming.AfterDust);
            RegisterFixedBuiltInLocked(BuiltInRenderTarget.AfterInterface, BuiltInRenderTargetNames.AfterInterface, RenderTiming.AfterInterface);
        }

        private void RegisterFixedBuiltInLocked(BuiltInRenderTarget id, string name, RenderTiming timing)
        {
            int slot = (byte)id;
            var target = new MainScreenRenderTarget(name, timing);
            Volatile.Write(ref _slots[slot], target);
            _nameToHandle[name] = new RenderTargetHandle((byte)slot);
            if (slot + 1 > _count)
                Volatile.Write(ref _count, slot + 1);
        }
    }
}
