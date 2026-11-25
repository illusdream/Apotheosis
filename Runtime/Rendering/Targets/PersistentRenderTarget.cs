#nullable enable

using System;
using Microsoft.Xna.Framework.Graphics;
using Terraria;

namespace Apotheosis.Runtime.Rendering.Targets
{
    /// <summary>跨帧持久渲染目标：独占物理显存以保留跨帧像素，支持自动缩放重建与可选接入 RenderCommand 管线。</summary>
    public sealed class PersistentRenderTarget : IRenderTarget
    {
        private readonly RenderTargetModule _owner;
        private readonly RenderTargetDescriptor _descriptor;
        private readonly object _sync = new();

        private RenderTarget2D? _target;
        private RenderTargetPoolKey _currentKey;
        private int _version;
        private bool _needsRedraw = true;
        private bool _disposed;

        internal PersistentRenderTarget(RenderTargetModule owner, string key, in RenderTargetDescriptor descriptor, RenderTiming timing)
        {
            _owner = owner;
            Name = key;
            _descriptor = descriptor;
            Timing = timing;
        }

        public string Name { get; }
        public string Key => Name;
        public RenderTiming Timing { get; }
        public bool IsMainScreen => false;

        /// <summary>渲染目标规格描述符。</summary>
        public RenderTargetDescriptor Descriptor => _descriptor;

        /// <summary>底层物理 RT 重建版本号（首次创建或尺寸重建时递增）。</summary>
        public int Version { get { lock (_sync) return _version; } }

        /// <summary>是否需要业务层重绘（首次创建、缩放重建、设备内容丢失或手动失效时为 <c>true</c>）。</summary>
        public bool NeedsRedraw
        {
            get
            {
                lock (_sync)
                {
                    return _needsRedraw || (_target is not null && (_target.IsDisposed || _target.IsContentLost));
                }
            }
        }

        /// <summary>底层 GPU 表面是否已触发内容丢失。</summary>
        public bool IsContentLost { get { lock (_sync) return _target is { IsContentLost: true }; } }

        /// <summary>当前底层纹理（尚未创建或已释放时返回 <c>null</c>）。</summary>
        public RenderTarget2D? CurrentTexture
        {
            get
            {
                lock (_sync)
                {
                    return !_disposed && _target is { IsDisposed: false } ? _target : null;
                }
            }
        }

        public int Width => CurrentTexture?.Width ?? 0;
        public int Height => CurrentTexture?.Height ?? 0;

        internal long EstimatedBytes
        {
            get
            {
                lock (_sync)
                {
                    return !_disposed && _target is { IsDisposed: false } ? _currentKey.EstimatedBytes : 0;
                }
            }
        }

        /// <summary>解析底层持久 <see cref="RenderTarget2D"/>（按需惰性创建并在窗口缩放时自动重建）。</summary>
        public RenderTarget2D? Resolve(GraphicsDevice graphicsDevice)
        {
            RenderManager.Shaders.AssertMainThread("PersistentRenderTarget.Resolve");

            if (RenderManager.IsUnloading || graphicsDevice.IsDisposed)
                return null;

            RenderTargetPoolKey desiredKey = _owner.ResolveDescriptorKey(graphicsDevice, in _descriptor);

            lock (_sync)
            {
                if (_disposed || RenderManager.IsUnloading)
                    return null;

                if (_target is not null && !_target.IsDisposed && _currentKey == desiredKey)
                {
                    if (_target.IsContentLost)
                        _needsRedraw = true;
                    return _target;
                }
            }

            // 在锁外创建 GPU 资源，随后于锁内复查卸载/释放标记，防止与后台 Dispose 竞态导致泄漏
            var newRt = new RenderTarget2D(graphicsDevice, desiredKey.Width, desiredKey.Height, desiredKey.MipMap,
                desiredKey.Format, desiredKey.DepthFormat, desiredKey.MultiSampleCount, desiredKey.Usage);

            RenderTarget2D? oldTarget;
            bool wasRebuild;
            lock (_sync)
            {
                if (_disposed || RenderManager.IsUnloading)
                {
                    newRt.Dispose();
                    return null;
                }

                oldTarget = _target;
                wasRebuild = oldTarget is not null;
                _target = newRt;
                _currentKey = desiredKey;
                _version++;
                _needsRedraw = true;
            }

            if (oldTarget is not null && !oldTarget.IsDisposed)
                oldTarget.Dispose();

            if (wasRebuild)
                _owner.NotifyPersistentRebuilt();

            return newRt;
        }

        /// <summary>消费并清除重绘标记；返回消费前的 <see cref="NeedsRedraw"/> 状态。</summary>
        public bool ConsumeNeedsRedraw()
        {
            lock (_sync)
            {
                bool needed = _needsRedraw || (_target is not null && (_target.IsDisposed || _target.IsContentLost));
                _needsRedraw = false;
                return needed;
            }
        }

        /// <summary>手动标记本持久目标需要重绘。</summary>
        public void Invalidate()
        {
            lock (_sync)
            {
                _needsRedraw = true;
            }
        }

        /// <summary>释放底层独占 <see cref="RenderTarget2D"/>（非主线程调用自动派发至主线程执行）。</summary>
        public void Dispose()
        {
            RenderTarget2D? toDispose;
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                toDispose = _target;
                _target = null;
            }

            if (toDispose is null || toDispose.IsDisposed)
                return;

            if (RenderManager.Shaders.IsOnMainThread)
                toDispose.Dispose();
            else
                Main.QueueMainThreadAction(() => { if (!toDispose.IsDisposed) toDispose.Dispose(); });
        }
    }
}
