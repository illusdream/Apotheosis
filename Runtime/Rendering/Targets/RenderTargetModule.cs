#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Terraria;

namespace Apotheosis.Runtime.Rendering.Targets
{
    /// <summary>渲染目标注册与解析模块。</summary>
    public sealed class RenderTargetModule
    {
        private const int MaxTargets = byte.MaxValue + 1;

        private readonly IRenderTarget?[] _slots = new IRenderTarget?[MaxTargets];
        private readonly Dictionary<string, RenderTargetHandle> _nameToHandle = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        private int _count;

        public RenderTargetModule()
        {
            RegisterBuiltInsLocked();
        }

        public RenderTargetHandle PreDrawOffscreen => new((byte)BuiltInRenderTarget.PreDrawOffscreen);
        public RenderTargetHandle AfterTiles => new((byte)BuiltInRenderTarget.AfterTiles);
        public RenderTargetHandle BeforeProjectiles => new((byte)BuiltInRenderTarget.BeforeProjectiles);
        public RenderTargetHandle AfterProjectiles => new((byte)BuiltInRenderTarget.AfterProjectiles);
        public RenderTargetHandle AfterDust => new((byte)BuiltInRenderTarget.AfterDust);
        public RenderTargetHandle AfterInterface => new((byte)BuiltInRenderTarget.AfterInterface);

        public int RegisteredCount => Volatile.Read(ref _count);

        /// <summary>通过强类型枚举获取内置渲染目标句柄。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public RenderTargetHandle Get(BuiltInRenderTarget builtIn) => new((byte)builtIn);

        /// <summary>通过全局唯一名称常量获取渲染目标句柄；未注册时抛出异常，防止静默画错目标。</summary>
        public RenderTargetHandle Get(string name)
        {
            lock (_lock)
            {
                if (_nameToHandle.TryGetValue(name, out RenderTargetHandle handle))
                    return handle;
            }

            throw new KeyNotFoundException($"未找到名为 \"{name}\" 的已注册渲染目标。");
        }

        /// <summary>尝试通过名称获取渲染目标句柄。</summary>
        public bool TryGetHandle(string name, out RenderTargetHandle handle)
        {
            lock (_lock)
            {
                return _nameToHandle.TryGetValue(name, out handle);
            }
        }

        /// <summary>
        /// 注册自定义 <see cref="IRenderTarget"/>（供后续 RT 池与离屏/3D 通道扩展使用）。
        /// 同名已注册时返回已有句柄，确保确定性顺序与去重。
        /// </summary>
        public RenderTargetHandle Register(IRenderTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentException.ThrowIfNullOrWhiteSpace(target.Name);

            lock (_lock)
            {
                if (_nameToHandle.TryGetValue(target.Name, out RenderTargetHandle existing))
                    return existing;

                int slot = _count;
                if (slot >= MaxTargets)
                    throw new InvalidOperationException($"渲染目标数量已达 1 字节 RenderTargetHandle 上限 ({MaxTargets})。");

                var handle = new RenderTargetHandle((byte)slot);
                Volatile.Write(ref _slots[slot], target);
                Volatile.Write(ref _count, slot + 1);
                _nameToHandle[target.Name] = handle;
                return handle;
            }
        }

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
            List<IRenderTarget>? toDispose = null;

            lock (_lock)
            {
                int count = _count;
                for (int i = 0; i < count; i++)
                {
                    IRenderTarget? target = _slots[i];
                    if (target is not null && !target.IsMainScreen)
                    {
                        toDispose ??= new List<IRenderTarget>();
                        toDispose.Add(target);
                    }
                    _slots[i] = null;
                }

                _nameToHandle.Clear();
                Volatile.Write(ref _count, 0);
            }

            if (toDispose is { Count: > 0 })
            {
                void DisposeAll()
                {
                    for (int i = 0; i < toDispose.Count; i++)
                    {
                        try { toDispose[i].Dispose(); }
                        catch { /* 卸载期忽略清理异常 */ }
                    }
                }

                if (RenderManager.Shaders.IsOnMainThread)
                    DisposeAll();
                else
                    Main.QueueMainThreadAction(DisposeAll);
            }
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
