#nullable enable

namespace Apotheosis.Runtime.Rendering.Targets
{
    /// <summary>
    /// 渲染目标的 Flush 时机（全部锚定在 <c>Main.spriteBatch</c> 已关闭的干净原版间隙，避免嵌套冲突与 Z 序错乱）。
    /// </summary>
    public enum RenderTiming : byte
    {
        /// <summary><c>On_Main.DoDraw_UpdateCameraPosition</c> 之后（主屏未绑定，适合离屏 RT / 3D 场景预渲染）。</summary>
        PreDrawOffscreen = 0,

        /// <summary><c>ModSystem.PostDrawTiles</c>（实体/玩家/弹幕之前、物块之后）。</summary>
        AfterTiles = 1,

        /// <summary><c>On_Main.DrawProjectiles</c> 之前。</summary>
        BeforeProjectiles = 2,

        /// <summary><c>On_Main.DrawProjectiles</c> 之后。</summary>
        AfterProjectiles = 3,

        /// <summary><c>On_Main.DrawDust</c> 之后（世界空间最顶层，仍会被后处理捕获）。</summary>
        AfterDust = 4,

        /// <summary><c>On_Main.DrawInterface</c> 之后（UI 之上，仍位于 DebugTool ImGui 之下）。</summary>
        AfterInterface = 5,
    }

    internal static class RenderTimingConstants
    {
        public const int Count = 6;
    }
}
