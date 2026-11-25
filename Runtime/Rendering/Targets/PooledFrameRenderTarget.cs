#nullable enable

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Targets
{
    /// <summary>命名帧通道渲染目标：按帧按需从池租借物理 RT，同帧跨阶段共享，帧末自动归还池复用。</summary>
    public sealed class PooledFrameRenderTarget : IRenderTarget
    {
        private readonly RenderTargetModule _owner;
        private readonly RenderTargetDescriptor _descriptor;
        private readonly object _sync = new();

        private PooledEntry? _leasedEntry;
        private int _leasedEpoch;
        private int _leasedToken;
        private bool _clearedThisFrame;
        private bool _trackedThisFrame;
        private bool _disposed;

        internal PooledFrameRenderTarget(RenderTargetModule owner, string name, RenderTiming timing, in RenderTargetDescriptor descriptor, Color? autoClearColor)
        {
            _owner = owner;
            Name = name;
            Timing = timing;
            _descriptor = descriptor;
            AutoClearColor = autoClearColor;
        }

        public string Name { get; }
        public RenderTiming Timing { get; }
        public bool IsMainScreen => false;

        /// <summary>所属的 1 字节渲染目标句柄。</summary>
        public RenderTargetHandle Handle { get; internal set; }

        /// <summary>渲染目标规格描述符。</summary>
        public RenderTargetDescriptor Descriptor => _descriptor;

        /// <summary>当帧首次绑定时的自动清屏颜色；为 <c>null</c> 时跳过自动清屏。</summary>
        public Color? AutoClearColor { get; }

        /// <summary>本帧是否已经绑定并触发过首次清屏。</summary>
        public bool HasWrittenThisFrame
        {
            get
            {
                lock (_sync)
                {
                    return !_disposed && _clearedThisFrame && _leasedEntry is { IsDisposed: false, Target.IsDisposed: false };
                }
            }
        }

        /// <summary>当前帧已租借的底层纹理（本帧尚未写入或已归还时返回 <c>null</c>）。</summary>
        public RenderTarget2D? CurrentTexture
        {
            get
            {
                lock (_sync)
                {
                    return !_disposed && _leasedEntry is { IsDisposed: false, Target.IsDisposed: false } entry ? entry.Target : null;
                }
            }
        }

        /// <summary>按需租借本帧物理渲染目标（纯解析，不修改 GPU 绑定状态）。</summary>
        public RenderTarget2D? Resolve(GraphicsDevice graphicsDevice)
        {
            RenderManager.Shaders.AssertMainThread("PooledFrameRenderTarget.Resolve");

            if (RenderManager.IsUnloading || graphicsDevice.IsDisposed)
                return null;

            PooledEntry? stale = null;
            int staleEpoch = 0;
            int staleToken = 0;

            lock (_sync)
            {
                if (_disposed || RenderManager.IsUnloading)
                    return null;

                if (_leasedEntry is not null)
                {
                    if (!_leasedEntry.IsDisposed && !_leasedEntry.Target.IsDisposed)
                        return _leasedEntry.Target;

                    // 底层对象意外失效时先摘取旧租借，锁外归还清理池账本
                    stale = _leasedEntry;
                    staleEpoch = _leasedEpoch;
                    staleToken = _leasedToken;
                    _leasedEntry = null;
                    _clearedThisFrame = false;
                }
            }

            if (stale is not null)
                _owner.ReturnLease(stale, staleEpoch, staleToken);

            if (!_owner.TryRentForChannel(graphicsDevice, in _descriptor, out PooledEntry? entry, out int epoch, out int token))
                return null;

            bool returnImmediately;
            bool needTrack = false;
            lock (_sync)
            {
                if (_disposed || RenderManager.IsUnloading)
                {
                    returnImmediately = true;
                }
                else
                {
                    returnImmediately = false;
                    _leasedEntry = entry;
                    _leasedEpoch = epoch;
                    _leasedToken = token;
                    _clearedThisFrame = false;
                    if (!_trackedThisFrame)
                    {
                        _trackedThisFrame = true;
                        needTrack = true;
                    }
                }
            }

            if (returnImmediately)
            {
                _owner.ReturnLease(entry, epoch, token);
                return null;
            }

            if (needTrack)
                _owner.TrackActiveChannel(this);

            return entry.Target;
        }

        /// <summary>目标被绑定到 GPU 后执行当帧首次自动清屏。</summary>
        public void OnBound(GraphicsDevice graphicsDevice)
        {
            DepthFormat depth;
            lock (_sync)
            {
                if (_disposed || _clearedThisFrame)
                    return;

                PooledEntry? entry = _leasedEntry;
                if (entry is null || entry.IsDisposed || entry.Target.IsDisposed)
                    return;

                _clearedThisFrame = true;
                depth = entry.Key.DepthFormat;
            }

            if (AutoClearColor is not Color clearColor)
                return;

            ClearOptions options = ClearOptions.Target;
            if (depth == DepthFormat.Depth24Stencil8)
                options |= ClearOptions.DepthBuffer | ClearOptions.Stencil;
            else if (depth != DepthFormat.None)
                options |= ClearOptions.DepthBuffer;

            graphicsDevice.Clear(options, clearColor, 1f, 0);
        }

        internal void EndFrameReturn()
        {
            PooledEntry? entry;
            int epoch;
            int token;

            lock (_sync)
            {
                entry = _leasedEntry;
                epoch = _leasedEpoch;
                token = _leasedToken;
                _leasedEntry = null;
                _clearedThisFrame = false;
                _trackedThisFrame = false;
            }

            if (entry is not null)
                _owner.ReturnLease(entry, epoch, token);
        }

        /// <summary>释放通道状态并归还当前租借（物理 RT 归属池统一管理）。</summary>
        public void Dispose()
        {
            PooledEntry? entry;
            int epoch;
            int token;

            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                entry = _leasedEntry;
                epoch = _leasedEpoch;
                token = _leasedToken;
                _leasedEntry = null;
                _clearedThisFrame = false;
                _trackedThisFrame = false;
            }

            if (entry is not null)
                _owner.ReturnLease(entry, epoch, token);
        }
    }
}
