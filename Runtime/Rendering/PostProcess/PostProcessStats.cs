#nullable enable

using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.PostProcess
{
    /// <summary>后处理管线上帧执行遥测快照。</summary>
    public readonly struct PostProcessStats
    {
        public readonly bool ExecutedLastFrame;
        public readonly bool BloomExecuted;
        public readonly bool DistortionExecuted;
        public readonly bool ColorAdjustmentsExecuted;
        public readonly bool SplitToningExecuted;
        public readonly int BloomMipLevelsUsed;
        public readonly int PassCount;
        public readonly SurfaceFormat EffectiveHdrFormat;

        public PostProcessStats(bool executedLastFrame, bool bloomExecuted, bool distortionExecuted, bool colorAdjustmentsExecuted, bool splitToningExecuted,
            int bloomMipLevelsUsed, int passCount, SurfaceFormat effectiveHdrFormat)
        {
            ExecutedLastFrame = executedLastFrame;
            BloomExecuted = bloomExecuted;
            DistortionExecuted = distortionExecuted;
            ColorAdjustmentsExecuted = colorAdjustmentsExecuted;
            SplitToningExecuted = splitToningExecuted;
            BloomMipLevelsUsed = bloomMipLevelsUsed;
            PassCount = passCount;
            EffectiveHdrFormat = effectiveHdrFormat;
        }
    }
}
