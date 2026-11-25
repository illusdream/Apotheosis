#nullable enable

using System;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Rendering.PostProcess
{
    /// <summary>金字塔式选择性 Bloom 全局配置。</summary>
    public sealed class BloomSettings
    {
        private int _mipCount = 5;

        public bool Enabled { get; set; } = true;
        public float Intensity { get; set; } = 1.0f;
        public float Scatter { get; set; } = 0.68f;
        public float ClampMax { get; set; } = 16.0f;
        public Color Tint { get; set; } = Color.White;

        /// <summary>金字塔降采样级数（自动钳位至 2..6）。</summary>
        public int MipCount
        {
            get => _mipCount;
            set => _mipCount = Math.Clamp(value, 2, 6);
        }

        /// <summary>当前配置是否会产生可见 Bloom 叠加。</summary>
        public bool IsEffective => Enabled && Intensity > 1e-4f;

        /// <summary>重置为默认参数。</summary>
        public void Reset()
        {
            Enabled = true;
            Intensity = 1.0f;
            Scatter = 0.68f;
            _mipCount = 5;
            ClampMax = 16.0f;
            Tint = Color.White;
        }
    }

    /// <summary>全屏扭曲后处理全局配置。</summary>
    public sealed class DistortionSettings
    {
        public bool Enabled { get; set; } = true;
        public float Strength { get; set; } = 0.05f;
        public float MaxOffset { get; set; } = 0.15f;
        public float ChromaticAberration { get; set; } = 0.35f;

        /// <summary>当前配置是否会产生可见扭曲偏移。</summary>
        public bool IsEffective => Enabled && MathF.Abs(Strength) > 1e-5f && MaxOffset > 1e-5f;

        /// <summary>重置为默认参数。</summary>
        public void Reset()
        {
            Enabled = true;
            Strength = 0.05f;
            MaxOffset = 0.15f;
            ChromaticAberration = 0.35f;
        }
    }

    /// <summary>最终颜色调整（Color Adjustments）全局配置。</summary>
    public sealed class ColorAdjustmentsSettings
    {
        public bool Enabled { get; set; } = true;
        public float PostExposure { get; set; }
        public float Contrast { get; set; }
        public Color ColorFilter { get; set; } = Color.White;
        public float HueShift { get; set; }
        public float Saturation { get; set; }

        /// <summary>当前是否处于非默认调色状态（全默认时零开销跳过）。</summary>
        public bool IsActive => Enabled && (
            MathF.Abs(PostExposure) > 1e-4f ||
            MathF.Abs(Contrast) > 1e-3f ||
            ColorFilter.R != 255 || ColorFilter.G != 255 || ColorFilter.B != 255 ||
            MathF.Abs(HueShift) > 1e-3f ||
            MathF.Abs(Saturation) > 1e-3f);

        /// <summary>重置为中性默认参数。</summary>
        public void Reset()
        {
            Enabled = true;
            PostExposure = 0f;
            Contrast = 0f;
            ColorFilter = Color.White;
            HueShift = 0f;
            Saturation = 0f;
        }
    }

    /// <summary>分离色调（Split Toning）全局配置。</summary>
    public sealed class SplitToningSettings
    {
        public static readonly Color NeutralGray = new(128, 128, 128, 255);

        public bool Enabled { get; set; } = true;
        public Color Shadows { get; set; } = NeutralGray;
        public Color Highlights { get; set; } = NeutralGray;
        public float Balance { get; set; }

        /// <summary>当前是否处于非中性分离色调状态（暗亮 RGB 均为中性灰 128 时零开销跳过）。</summary>
        public bool IsActive => Enabled && (
            Shadows.R != 128 || Shadows.G != 128 || Shadows.B != 128 ||
            Highlights.R != 128 || Highlights.G != 128 || Highlights.B != 128);

        /// <summary>重置为中性无色偏参数。</summary>
        public void Reset()
        {
            Enabled = true;
            Shadows = NeutralGray;
            Highlights = NeutralGray;
            Balance = 0f;
        }
    }
}
