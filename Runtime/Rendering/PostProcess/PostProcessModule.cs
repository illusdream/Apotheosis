#nullable enable

using System;
using System.Threading;
using Apotheosis.Runtime.Rendering.Shaders;
using Apotheosis.Runtime.Rendering.States;
using Apotheosis.Runtime.Rendering.Targets;
using Apotheosis.Shaders;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.Graphics.Capture;

namespace Apotheosis.Runtime.Rendering.PostProcess
{
    /// <summary>后处理管线核心模块，统一调度选择性金字塔 Bloom、全屏扭曲与最终调色合成。</summary>
    public sealed class PostProcessModule
    {
        private const int MinBloomMips = 2;
        private const int MaxBloomMips = 6;

        // 纯加法混合（One + One，不乘 SourceAlpha），供 FP16 扭曲偏移通道矢量叠加；内部持有防外部误改/误释放
        internal static readonly BlendState DistortionBlendState = new()
        {
            Name = "PostProcess.DistortionPureAdditive",
            ColorSourceBlend = Blend.One,
            ColorDestinationBlend = Blend.One,
            ColorBlendFunction = BlendFunction.Add,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.One,
            AlphaBlendFunction = BlendFunction.Add,
        };

        private static readonly SurfaceFormat[] SignedDistortionFormatCandidates =
        {
            SurfaceFormat.HalfVector4,
            SurfaceFormat.Vector4,
            SurfaceFormat.HalfVector2,
            SurfaceFormat.Vector2,
        };

        private readonly object _lock = new();
        private readonly FullscreenQuadRenderer _quad = new();
        private readonly PooledRenderTarget[] _downMips = new PooledRenderTarget[MaxBloomMips];
        private readonly PooledRenderTarget[] _upMips = new PooledRenderTarget[MaxBloomMips - 1];

        private RenderTargetHandle _bloomMaskHandle;
        private RenderTargetHandle _distortionMaskHandle;
        private bool _channelsRegistered;

        private bool _hdrProbed;
        private SurfaceFormat _effectiveHdrFormat = SurfaceFormat.HalfVector4;
        private SurfaceFormat _distortionFormat = SurfaceFormat.HalfVector4;
        private bool _distortionSupported = true;
        private SpriteEffects _lastPreDrawViewEffects;
        private PostProcessStats _lastStats = new(false, false, false, false, false, 0, 0, SurfaceFormat.HalfVector4);

        /// <summary>金字塔式选择性 Bloom 全局配置。</summary>
        public BloomSettings Bloom { get; } = new();

        /// <summary>全屏扭曲后处理全局配置。</summary>
        public DistortionSettings Distortion { get; } = new();

        /// <summary>最终颜色调整（Color Adjustments）全局配置。</summary>
        public ColorAdjustmentsSettings ColorAdjustments { get; } = new();

        /// <summary>分离色调（Split Toning）全局配置。</summary>
        public SplitToningSettings SplitToning { get; } = new();

        /// <summary>当前 GPU 是否支持有符号浮点扭曲通道。</summary>
        public bool IsDistortionSupported => Volatile.Read(ref _distortionSupported);

        /// <summary>选择性 Bloom 命名帧通道句柄。</summary>
        public RenderTargetHandle BloomMask
        {
            get
            {
                if (RenderManager.IsUnloading)
                    return default;
                EnsureChannelsRegistered();
                return _bloomMaskHandle;
            }
        }

        /// <summary>当帧全屏扭曲偏移通道句柄。</summary>
        public RenderTargetHandle DistortionMask
        {
            get
            {
                if (RenderManager.IsUnloading)
                    return default;
                EnsureChannelsRegistered();
                return _distortionMaskHandle;
            }
        }

        /// <summary>获取上一帧后处理执行遥测快照。</summary>
        public PostProcessStats GetLastStats()
        {
            lock (_lock)
            {
                return _lastStats;
            }
        }

        /// <summary>将全部后处理参数重置为默认值。</summary>
        public void ResetAllSettings()
        {
            Bloom.Reset();
            Distortion.Reset();
            ColorAdjustments.Reset();
            SplitToning.Reset();
        }

        // ==================== 内部管线协作 ====================

        internal bool ShouldCaptureScreenTarget()
        {
            // 游戏世界内任一后处理启用即请求捕获，确保首帧瞬时特效零延迟生效；无实际写入时 EndCapture 内仍零开销短路
            return Bloom.IsEffective || (Distortion.IsEffective && IsDistortionSupported) || ColorAdjustments.IsActive || SplitToning.IsActive;
        }

        internal bool ShouldCaptureInMenu()
        {
            // 主菜单仅在全局调色或分离色调处于非中性态时才请求捕获，避免默认空闲态无谓捕获主菜单画面
            return ColorAdjustments.IsActive || SplitToning.IsActive;
        }

        internal void NotifyPreDrawViewEffects(SpriteEffects effects)
        {
            _lastPreDrawViewEffects = effects;
        }

        internal void ResetForLoad()
        {
            lock (_lock)
            {
                _channelsRegistered = false;
                _bloomMaskHandle = default;
                _distortionMaskHandle = default;
                _hdrProbed = false;
                _effectiveHdrFormat = SurfaceFormat.HalfVector4;
                _distortionFormat = SurfaceFormat.HalfVector4;
                _distortionSupported = true;
                _lastPreDrawViewEffects = SpriteEffects.None;
                _lastStats = new PostProcessStats(false, false, false, false, false, 0, 0, SurfaceFormat.HalfVector4);
            }

            ResetAllSettings();
            _quad.ResetForLoad();
        }

        internal void InitializeOnMainThread()
        {
            if (RenderManager.IsUnloading || Main.dedServ)
                return;

            GraphicsDevice? gd = Main.graphics?.GraphicsDevice;
            if (gd is { IsDisposed: false })
            {
                EnsureHdrFormatProbed(gd);
                EnsureChannelsRegistered();
            }
        }

        internal void Unload()
        {
            lock (_lock)
            {
                _channelsRegistered = false;
                _bloomMaskHandle = default;
                _distortionMaskHandle = default;
                _hdrProbed = false;
                _lastPreDrawViewEffects = SpriteEffects.None;
                _lastStats = new PostProcessStats(false, false, false, false, false, 0, 0, _effectiveHdrFormat);
            }

            _quad.Dispose();
        }

        /// <summary>在原版 <c>FilterManager.EndCapture</c> 之后执行后处理并输出至主屏目标。</summary>
        internal void ExecuteAfterEndCapture(GraphicsDevice gd, RenderTarget2D? finalTexture)
        {
            if (RenderManager.IsUnloading || gd.IsDisposed)
                return;

            // 仅处理主屏输出（finalTexture == null）；跳过相机模式/外部非空目标，防止用 Main.finalScreenTarget 覆盖快照
            if (finalTexture is not null)
                return;

            RenderTarget2D? sceneTex = Main.finalScreenTarget;
            if (sceneTex is null || sceneTex.IsDisposed)
            {
                RecordStats(new PostProcessStats(false, false, false, false, false, 0, 0, _effectiveHdrFormat));
                return;
            }

            RenderManager.Shaders.AssertMainThread("PostProcessModule.ExecuteAfterEndCapture");
            EnsureHdrFormatProbed(gd);
            EnsureChannelsRegistered();

            RenderTarget2D? bloomSource = null;
            RenderTarget2D? distortionSource = null;
            bool bloomActive = Bloom.IsEffective && TryGetWrittenChannelTexture(_bloomMaskHandle, out bloomSource);
            bool distortionActive = Distortion.IsEffective && _distortionSupported && TryGetWrittenChannelTexture(_distortionMaskHandle, out distortionSource);
            bool colorActive = ColorAdjustments.IsActive;
            bool splitActive = SplitToning.IsActive;

            if (!bloomActive && !distortionActive && !colorActive && !splitActive)
            {
                RecordStats(new PostProcessStats(false, false, false, false, false, 0, 0, _effectiveHdrFormat));
                return;
            }

            if (!RenderManager.Shaders.TryGet(Assets.Shaders.PostProcess.UberPost, out ManagedShader? uberShader) || !uberShader.EnsureLoaded())
            {
                RecordStats(new PostProcessStats(false, false, false, false, false, 0, 0, _effectiveHdrFormat));
                return;
            }

            ManagedShader? bloomShader = null;
            if (bloomActive)
            {
                if (!RenderManager.Shaders.TryGet(Assets.Shaders.PostProcess.BloomPyramid, out bloomShader) || !bloomShader.EnsureLoaded())
                    bloomActive = false;
            }

            if (!bloomActive && !distortionActive && !colorActive && !splitActive)
            {
                RecordStats(new PostProcessStats(false, false, false, false, false, 0, 0, _effectiveHdrFormat));
                return;
            }

            int passCount = 0;
            int bloomMipsUsed = 0;
            bool isOnFinalTarget = true;
            bool switchedAnyTarget = false;

            // RenderStateScope 位于最外层，确保内层 finally 先恢复目标再恢复 Viewport/ScissorRectangle
            using (RenderStateScope.Capture(gd))
            {
                try
                {
                    gd.BlendState = BlendState.Opaque;
                    gd.DepthStencilState = DepthStencilState.None;
                    gd.RasterizerState = RasterizerState.CullNone;
                    gd.SamplerStates[0] = SamplerState.LinearClamp;
                    gd.SamplerStates[1] = SamplerState.LinearClamp;
                    gd.SamplerStates[2] = SamplerState.LinearClamp;

                    // 1. 金字塔式 Selective Bloom（仅对 BloomMask 通道降采样 + 上采样）
                    RenderTarget2D? bloomResultTex = null;
                    if (bloomActive && bloomSource is not null && bloomShader is not null)
                        bloomResultTex = ExecuteBloomPyramid(gd, bloomShader, bloomSource, out bloomMipsUsed, ref passCount, ref isOnFinalTarget, ref switchedAnyTarget);

                    if (bloomResultTex is null)
                        bloomActive = false;

                    if (!bloomActive && !distortionActive && !colorActive && !splitActive)
                    {
                        RecordStats(new PostProcessStats(false, false, false, false, false, 0, 0, _effectiveHdrFormat));
                        return;
                    }

                    // 2. 对照 EndCapture 实际垂直翻转状态与 PreDrawOffscreen 捕获状态，避免重力药水双重翻转
                    bool sceneFlipped = !Main.gameMenu && !CaptureManager.Instance.IsCapturing && Main.LocalPlayer is { active: true, gravDir: < 0f };
                    bool maskAlreadyFlipped = (_lastPreDrawViewEffects & SpriteEffects.FlipVertically) != 0;
                    bool flipMaskY = sceneFlipped != maskAlreadyFlipped;

                    // 3. 单次 UberPost 最终合成并直接写入 BackBuffer（后续 DrawInterface 叠在上方不受影响）
                    bool uberOk = ExecuteUberPost(gd, uberShader, finalTexture, sceneTex, distortionActive ? distortionSource : null,
                        bloomActive ? bloomResultTex : null, colorActive, splitActive, flipMaskY, sceneFlipped, ref isOnFinalTarget, ref switchedAnyTarget);

                    if (!uberOk)
                    {
                        RecordStats(new PostProcessStats(false, false, false, false, false, 0, 0, _effectiveHdrFormat));
                        return;
                    }

                    passCount++;
                    RecordStats(new PostProcessStats(true, bloomActive, distortionActive, colorActive, splitActive, bloomMipsUsed, passCount, _effectiveHdrFormat));
                }
                finally
                {
                    if (!gd.IsDisposed)
                    {
                        gd.Textures[0] = null;
                        gd.Textures[1] = null;
                        gd.Textures[2] = null;
                        if (!isOnFinalTarget)
                            gd.SetRenderTarget(finalTexture);
                    }

                    if (switchedAnyTarget)
                        RenderManager.InvalidateViewContext();

                    if (bloomShader is not null)
                    {
                        bloomShader.SetTexture(PostProcess_BloomPyramidShader.Slot_uImage0, null);
                        bloomShader.SetTexture(PostProcess_BloomPyramidShader.Slot_uImage1, null);
                    }

                    uberShader.SetTexture(PostProcess_UberPostShader.Slot_uImage0, null);
                    uberShader.SetTexture(PostProcess_UberPostShader.Slot_uImage1, null);
                    uberShader.SetTexture(PostProcess_UberPostShader.Slot_uImage2, null);

                    ReleaseBloomMips();
                }
            }
        }

        // ==================== 内部渲染步骤 ====================

        private RenderTarget2D? ExecuteBloomPyramid(GraphicsDevice gd, ManagedShader bloomShader, RenderTarget2D bloomSource, out int actualMips,
            ref int passCount, ref bool isOnFinalTarget, ref bool switchedAnyTarget)
        {
            int requestedMips = Math.Clamp(Bloom.MipCount, MinBloomMips, MaxBloomMips);
            float clampMax = Math.Max(0.1f, Bloom.ClampMax);
            float scatter = Math.Clamp(Bloom.Scatter, 0.01f, 0.99f);
            RenderFrameContext unusedContext = default;

            bloomShader.SetFloat(PostProcess_BloomPyramidShader.Slot_uClampMax, clampMax);
            bloomShader.SetFloat(PostProcess_BloomPyramidShader.Slot_uScatter, scatter);

            // 1. 降采样链：1/2 -> 1/4 -> ... -> 1/2^N
            actualMips = 0;
            RenderTarget2D prevSource = bloomSource;
            for (int i = 0; i < requestedMips; i++)
            {
                float mipScale = 1f / (2 << i);
                RenderTargetDescriptor mipDesc = RenderTargetDescriptor.ScreenRelative(mipScale, _effectiveHdrFormat);
                PooledRenderTarget leased = RenderManager.Targets.Rent(gd, in mipDesc);
                if (!leased.IsValid)
                    break;

                _downMips[i] = leased;
                RenderTarget2D dst = leased.Target;

                gd.Textures[0] = null;
                gd.Textures[1] = null;
                gd.SetRenderTarget(dst);
                isOnFinalTarget = false;
                switchedAnyTarget = true;

                bloomShader.SetVector2(PostProcess_BloomPyramidShader.Slot_uTexelSize, new Vector2(1f / prevSource.Width, 1f / prevSource.Height));
                bloomShader.SetTexture(PostProcess_BloomPyramidShader.Slot_uImage0, prevSource);

                int tech = i == 0 ? PostProcess_BloomPyramidShader.Technique_DownsampleFirstTech : PostProcess_BloomPyramidShader.Technique_DownsampleTech;
                if (!bloomShader.TryApply(in unusedContext, tech, 0))
                    return null;

                _quad.Draw(gd);
                passCount++;

                actualMips = i + 1;
                prevSource = dst;

                if (dst.Width <= 2 && dst.Height <= 2)
                    break;
            }

            if (actualMips == 0)
                return null;

            if (actualMips == 1)
                return _downMips[0].Target;

            // 2. 上采样链：从最底层逐级 Tent 滤波上采样并与对应降采样级做 Scatter 混合
            RenderTarget2D prevUp = _downMips[actualMips - 1].Target;
            for (int i = actualMips - 2; i >= 0; i--)
            {
                RenderTargetDescriptor upDesc = RenderTargetDescriptor.ScreenRelative(1f / (2 << i), _effectiveHdrFormat);
                PooledRenderTarget leasedUp = RenderManager.Targets.Rent(gd, in upDesc);
                if (!leasedUp.IsValid)
                    return prevUp;

                _upMips[i] = leasedUp;
                RenderTarget2D dst = leasedUp.Target;
                RenderTarget2D highMip = _downMips[i].Target;

                gd.Textures[0] = null;
                gd.Textures[1] = null;
                gd.SetRenderTarget(dst);
                isOnFinalTarget = false;
                switchedAnyTarget = true;

                bloomShader.SetVector2(PostProcess_BloomPyramidShader.Slot_uTexelSize, new Vector2(1f / prevUp.Width, 1f / prevUp.Height));
                bloomShader.SetTexture(PostProcess_BloomPyramidShader.Slot_uImage0, prevUp);
                bloomShader.SetTexture(PostProcess_BloomPyramidShader.Slot_uImage1, highMip);

                if (!bloomShader.TryApply(in unusedContext, PostProcess_BloomPyramidShader.Technique_UpsampleTech, 0))
                    return null;

                _quad.Draw(gd);
                passCount++;

                prevUp = dst;
            }

            return prevUp;
        }

        private bool ExecuteUberPost(GraphicsDevice gd, ManagedShader uberShader, RenderTarget2D? destRt, RenderTarget2D sceneTex,
            RenderTarget2D? distortionTex, RenderTarget2D? bloomTex, bool colorActive, bool splitActive, bool flipMaskY, bool flipOffsetY,
            ref bool isOnFinalTarget, ref bool switchedAnyTarget)
        {
            Texture2D? fallbackTex = TextureAssets.MagicPixel?.Value;
            RenderFrameContext unusedContext = default;

            bool distortionOn = distortionTex is { IsDisposed: false };
            bool bloomOn = bloomTex is { IsDisposed: false };
            float aspectInv = sceneTex.Height / (float)Math.Max(1, sceneTex.Width);

            uberShader.SetVector4(PostProcess_UberPostShader.Slot_uDistortionParams,
                new Vector4(distortionOn ? 1f : 0f, Distortion.Strength, Math.Max(0f, Distortion.MaxOffset), flipMaskY ? 1f : 0f));

            uberShader.SetVector4(PostProcess_UberPostShader.Slot_uBloomParams,
                new Vector4(bloomOn ? 1f : 0f, Math.Max(0f, Bloom.Intensity), aspectInv, flipOffsetY ? 1f : 0f));

            Color bloomTint = Bloom.Tint;
            uberShader.SetVector4(PostProcess_UberPostShader.Slot_uBloomTint,
                new Vector4(bloomTint.R / 255f, bloomTint.G / 255f, bloomTint.B / 255f, Math.Max(0f, Distortion.ChromaticAberration)));

            float exposureScale = MathF.Pow(2f, Math.Clamp(ColorAdjustments.PostExposure, -10f, 10f));
            float contrastScale = Math.Max(0f, 1f + ColorAdjustments.Contrast * 0.01f);
            float saturationScale = Math.Max(0f, 1f + ColorAdjustments.Saturation * 0.01f);
            uberShader.SetVector4(PostProcess_UberPostShader.Slot_uColorAdjustParams,
                new Vector4(colorActive ? 1f : 0f, exposureScale, contrastScale, saturationScale));

            Color filter = ColorAdjustments.ColorFilter;
            uberShader.SetVector4(PostProcess_UberPostShader.Slot_uColorFilterHue,
                new Vector4(filter.R / 255f, filter.G / 255f, filter.B / 255f, ColorAdjustments.HueShift / 360f));

            Color shadows = SplitToning.Shadows;
            Color highlights = SplitToning.Highlights;
            uberShader.SetVector4(PostProcess_UberPostShader.Slot_uSplitShadows,
                new Vector4(ToSplitChannel(shadows.R), ToSplitChannel(shadows.G), ToSplitChannel(shadows.B), splitActive ? 1f : 0f));
            uberShader.SetVector4(PostProcess_UberPostShader.Slot_uSplitHighlights,
                new Vector4(ToSplitChannel(highlights.R), ToSplitChannel(highlights.G), ToSplitChannel(highlights.B), Math.Clamp(SplitToning.Balance, -1f, 1f)));

            uberShader.SetTexture(PostProcess_UberPostShader.Slot_uImage0, sceneTex);
            uberShader.SetTexture(PostProcess_UberPostShader.Slot_uImage1, distortionOn ? distortionTex : fallbackTex);
            uberShader.SetTexture(PostProcess_UberPostShader.Slot_uImage2, bloomOn ? bloomTex : fallbackTex);

            gd.Textures[0] = null;
            gd.Textures[1] = null;
            gd.Textures[2] = null;

            if (!isOnFinalTarget)
            {
                gd.SetRenderTarget(destRt);
                isOnFinalTarget = true;
                switchedAnyTarget = true;
            }

            if (!uberShader.TryApply(in unusedContext, PostProcess_UberPostShader.Technique_UberPostTech, PostProcess_UberPostShader.Pass_UberPostTech_DefaultPass))
                return false;

            _quad.Draw(gd);
            return true;
        }

        private void ReleaseBloomMips()
        {
            for (int i = 0; i < _downMips.Length; i++)
            {
                _downMips[i].Dispose();
                _downMips[i] = default;
            }

            for (int i = 0; i < _upMips.Length; i++)
            {
                _upMips[i].Dispose();
                _upMips[i] = default;
            }
        }

        private void EnsureChannelsRegistered()
        {
            if (RenderManager.IsUnloading || Volatile.Read(ref _channelsRegistered))
                return;

            lock (_lock)
            {
                if (RenderManager.IsUnloading || _channelsRegistered)
                    return;

                // 必须挂在 PreDrawOffscreen（FilterManager.BeginCapture 之前），避免世界绘制中途重绑 DiscardContents 的 Main.screenTarget 触发 FNA 清黑
                RenderTargetDescriptor bloomDesc = RenderTargetDescriptor.ScreenRelative(scale: 1f, format: _effectiveHdrFormat);
                RenderTargetDescriptor distortionDesc = RenderTargetDescriptor.ScreenRelative(scale: 1f, format: _distortionFormat);
                _bloomMaskHandle = RenderManager.Targets.RegisterPooledChannel(PostProcessTargets.BloomMask, RenderTiming.PreDrawOffscreen, in bloomDesc, Color.Transparent);
                _distortionMaskHandle = RenderManager.Targets.RegisterPooledChannel(PostProcessTargets.DistortionMask, RenderTiming.PreDrawOffscreen, in distortionDesc, Color.Transparent);
                Volatile.Write(ref _channelsRegistered, true);
            }
        }

        private void EnsureHdrFormatProbed(GraphicsDevice gd)
        {
            if (_hdrProbed || gd.IsDisposed)
                return;

            _hdrProbed = true;

            // FNA 的 RenderTarget2D 构造可能静默降级 preferredFormat 而不抛异常，必须实测检查 probe.Format
            _effectiveHdrFormat = TryProbeExactFormat(gd, SurfaceFormat.HalfVector4) ? SurfaceFormat.HalfVector4 : SurfaceFormat.Color;

            // 扭曲通道必须支持有符号浮点格式；若硬件全不支持则禁用扭曲，避免落到无符号 Color 截断负偏移
            bool foundSigned = false;
            for (int i = 0; i < SignedDistortionFormatCandidates.Length; i++)
            {
                SurfaceFormat candidate = SignedDistortionFormatCandidates[i];
                if (TryProbeExactFormat(gd, candidate))
                {
                    _distortionFormat = candidate;
                    foundSigned = true;
                    break;
                }
            }

            Volatile.Write(ref _distortionSupported, foundSigned);
            if (!foundSigned)
                _distortionFormat = SurfaceFormat.Color;
        }

        private static bool TryProbeExactFormat(GraphicsDevice gd, SurfaceFormat format)
        {
            try
            {
                using var probe = new RenderTarget2D(gd, 4, 4, mipMap: false, format, DepthFormat.None);
                return !probe.IsDisposed && probe.Format == format;
            }
            catch
            {
                return false;
            }
        }

        private static float ToSplitChannel(byte value) => value == 128 ? 0.5f : value / 255f;

        private static bool TryGetWrittenChannelTexture(RenderTargetHandle handle, out RenderTarget2D? texture)
        {
            if (RenderManager.Targets.TryGetChannel(handle, out PooledFrameRenderTarget? channel) && channel.HasWrittenThisFrame)
            {
                RenderTarget2D? tex = channel.CurrentTexture;
                if (tex is { IsDisposed: false })
                {
                    texture = tex;
                    return true;
                }
            }

            texture = null;
            return false;
        }

        private void RecordStats(in PostProcessStats stats)
        {
            lock (_lock)
            {
                _lastStats = stats;
            }
        }
    }
}
