#nullable enable

using System;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Targets
{
    /// <summary>离屏渲染目标规格描述符，支持按屏幕比例或固定像素尺寸创建。</summary>
    public readonly struct RenderTargetDescriptor : IEquatable<RenderTargetDescriptor>
    {
        public readonly bool IsScreenRelative;
        public readonly float ScreenScale;
        public readonly int FixedWidth;
        public readonly int FixedHeight;
        public readonly SurfaceFormat Format;
        public readonly DepthFormat DepthFormat;
        public readonly int MultiSampleCount;
        public readonly bool MipMap;
        public readonly RenderTargetUsage Usage;

        private RenderTargetDescriptor(bool isScreenRelative, float screenScale, int fixedWidth, int fixedHeight, SurfaceFormat format, DepthFormat depthFormat, int multiSampleCount, bool mipMap, RenderTargetUsage usage)
        {
            IsScreenRelative = isScreenRelative;
            ScreenScale = screenScale;
            FixedWidth = fixedWidth;
            FixedHeight = fixedHeight;
            Format = format;
            DepthFormat = depthFormat;
            // XNA/FNA 中 0 与 1 均表示非 MSAA，归一化避免拆出两个物理桶
            MultiSampleCount = multiSampleCount <= 1 ? 0 : multiSampleCount;
            MipMap = mipMap;
            Usage = usage;
        }

        /// <summary>描述符是否为有效配置（避免误传 <c>default</c> 结构体）。</summary>
        public bool IsValid => IsScreenRelative ? (float.IsFinite(ScreenScale) && ScreenScale > 0f) : (FixedWidth > 0 && FixedHeight > 0);

        internal void Validate(string paramName)
        {
            if (!IsValid)
                throw new ArgumentException("渲染目标描述符无效，请使用 ScreenRelative(...) 或 Fixed(...) 构造。", paramName);
        }

        /// <summary>构造按当前屏幕参考分辨率缩放的渲染目标描述符。</summary>
        public static RenderTargetDescriptor ScreenRelative(float scale = 1f, SurfaceFormat format = SurfaceFormat.Color, DepthFormat depthFormat = DepthFormat.None,
            int multiSampleCount = 0, bool mipMap = false, RenderTargetUsage usage = RenderTargetUsage.DiscardContents)
        {
            if (!float.IsFinite(scale) || scale <= 0f)
                throw new ArgumentOutOfRangeException(nameof(scale), "屏幕比例必须为正有限值。");

            return new RenderTargetDescriptor(isScreenRelative: true, scale, 0, 0, format, depthFormat, multiSampleCount, mipMap, usage);
        }

        /// <summary>构造固定像素尺寸的渲染目标描述符。</summary>
        public static RenderTargetDescriptor Fixed(int width, int height, SurfaceFormat format = SurfaceFormat.Color, DepthFormat depthFormat = DepthFormat.None,
            int multiSampleCount = 0, bool mipMap = false, RenderTargetUsage usage = RenderTargetUsage.DiscardContents)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "固定宽度必须大于 0。");
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height), "固定高度必须大于 0。");

            return new RenderTargetDescriptor(isScreenRelative: false, 0f, width, height, format, depthFormat, multiSampleCount, mipMap, usage);
        }

        internal RenderTargetPoolKey ResolveKey(int referenceWidth, int referenceHeight)
        {
            int width = IsScreenRelative ? Math.Max(1, (int)MathF.Round(referenceWidth * ScreenScale)) : Math.Max(1, FixedWidth);
            int height = IsScreenRelative ? Math.Max(1, (int)MathF.Round(referenceHeight * ScreenScale)) : Math.Max(1, FixedHeight);
            return new RenderTargetPoolKey(width, height, Format, DepthFormat, MultiSampleCount, MipMap, Usage);
        }

        internal static long EstimateBytes(in RenderTargetPoolKey key)
        {
            int colorBpp = key.Format switch
            {
                SurfaceFormat.Alpha8 => 1,
                SurfaceFormat.Bgr565 or SurfaceFormat.Bgra5551 or SurfaceFormat.Bgra4444 or SurfaceFormat.HalfSingle or SurfaceFormat.NormalizedByte2 => 2,
                SurfaceFormat.HalfVector4 or SurfaceFormat.Vector2 or SurfaceFormat.Rgba64 or SurfaceFormat.HdrBlendable => 8,
                SurfaceFormat.Vector4 => 16,
                _ => 4,
            };

            int depthBpp = key.DepthFormat switch
            {
                DepthFormat.None => 0,
                DepthFormat.Depth16 => 2,
                _ => 4,
            };

            long pixels = (long)key.Width * key.Height;
            long samples = Math.Max(1, key.MultiSampleCount);
            long colorBytes = pixels * colorBpp;
            if (key.MipMap && samples == 1)
                colorBytes = colorBytes * 4 / 3;

            return (colorBytes + pixels * depthBpp) * samples;
        }

        public bool Equals(RenderTargetDescriptor other)
        {
            return IsScreenRelative == other.IsScreenRelative &&
                   ScreenScale.Equals(other.ScreenScale) &&
                   FixedWidth == other.FixedWidth &&
                   FixedHeight == other.FixedHeight &&
                   Format == other.Format &&
                   DepthFormat == other.DepthFormat &&
                   MultiSampleCount == other.MultiSampleCount &&
                   MipMap == other.MipMap &&
                   Usage == other.Usage;
        }

        public override bool Equals(object? obj) => obj is RenderTargetDescriptor other && Equals(other);

        public override int GetHashCode()
        {
            var hc = new HashCode();
            hc.Add(IsScreenRelative);
            hc.Add(ScreenScale);
            hc.Add(FixedWidth);
            hc.Add(FixedHeight);
            hc.Add(Format);
            hc.Add(DepthFormat);
            hc.Add(MultiSampleCount);
            hc.Add(MipMap);
            hc.Add(Usage);
            return hc.ToHashCode();
        }

        public static bool operator ==(RenderTargetDescriptor left, RenderTargetDescriptor right) => left.Equals(right);
        public static bool operator !=(RenderTargetDescriptor left, RenderTargetDescriptor right) => !left.Equals(right);
    }
}
