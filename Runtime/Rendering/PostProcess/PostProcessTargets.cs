#nullable enable

namespace Apotheosis.Runtime.Rendering.PostProcess
{
    /// <summary>后处理系统内置命名帧通道名称常量。</summary>
    public static class PostProcessTargets
    {
        /// <summary>选择性 Bloom 源通道（仅显式提交至此通道的特效参与 Bloom 金字塔）。</summary>
        public const string BloomMask = "PostProcess.BloomMask";

        /// <summary>当帧全屏扭曲偏移通道（FP16 RG 有符号偏移，Additive 叠加）。</summary>
        public const string DistortionMask = "PostProcess.DistortionMask";
    }
}
