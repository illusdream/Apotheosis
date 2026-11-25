#nullable enable

using Apotheosis.Runtime.Foundation;
using Apotheosis.Runtime.Foundation.Collections;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Targets
{
    /// <summary>按解析后物理像素规格分桶的池键。</summary>
    internal readonly record struct RenderTargetPoolKey(int Width, int Height, SurfaceFormat Format, DepthFormat DepthFormat, int MultiSampleCount, bool MipMap, RenderTargetUsage Usage)
    {
        public long EstimatedBytes => RenderTargetDescriptor.EstimateBytes(in this);
    }

    /// <summary>RT 池物理条目内部节点。</summary>
    internal sealed class PooledEntry : IIndexedSwapItem
    {
        public readonly RenderTargetPoolKey Key;
        public readonly RenderTarget2D Target;
        public readonly long EstimatedBytes;
        public bool IsScreenRelative;
        public int LeaseToken;
        public int ActiveIndex = RuntimeConstants.InvalidIndex;
        public int ResolutionEpoch;
        public ulong LastReturnedFrame;
        public ulong LeasedFrame;
        public bool InUse;
        public bool IsTransientLease;
        public bool IsDisposed;

        int IIndexedSwapItem.SwapListIndex { get => ActiveIndex; set => ActiveIndex = value; }

        public PooledEntry(RenderTargetPoolKey key, RenderTarget2D target, bool isScreenRelative)
        {
            Key = key;
            Target = target;
            EstimatedBytes = key.EstimatedBytes;
            IsScreenRelative = isScreenRelative;
        }
    }
}
