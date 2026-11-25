#nullable enable

using System;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Targets
{
    /// <summary>瞬时池化渲染目标租借句柄，释放时 O(1) 归还空闲池。</summary>
    public readonly struct PooledRenderTarget : IDisposable
    {
        private readonly RenderTargetModule? _owner;
        private readonly PooledEntry? _entry;
        private readonly int _poolEpoch;
        private readonly int _leaseToken;

        internal PooledRenderTarget(RenderTargetModule owner, PooledEntry entry, int poolEpoch, int leaseToken)
        {
            _owner = owner;
            _entry = entry;
            _poolEpoch = poolEpoch;
            _leaseToken = leaseToken;
        }

        /// <summary>当前租借是否仍然有效且未被归还或销毁。</summary>
        public bool IsValid => _owner is not null && _entry is not null && _owner.IsLeaseValid(_entry, _poolEpoch, _leaseToken);

        /// <summary>底层 GPU <see cref="RenderTarget2D"/>；已归还或无效时抛出异常。</summary>
        public RenderTarget2D Target => IsValid ? _entry!.Target : throw new ObjectDisposedException(nameof(PooledRenderTarget), "瞬时渲染目标租借已归还或失效。");

        /// <summary>安全获取底层 <see cref="RenderTarget2D"/>；无效时返回 <c>null</c>。</summary>
        public RenderTarget2D? TargetOrNull => IsValid ? _entry!.Target : null;

        public int Width => IsValid ? _entry!.Key.Width : 0;
        public int Height => IsValid ? _entry!.Key.Height : 0;

        /// <summary>将租借的渲染目标归还到空闲池（幂等，重复调用安全忽略）。</summary>
        public void Dispose()
        {
            if (_owner is not null && _entry is not null)
                _owner.ReturnLease(_entry, _poolEpoch, _leaseToken);
        }
    }
}
