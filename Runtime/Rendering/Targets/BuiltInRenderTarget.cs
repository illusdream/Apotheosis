#nullable enable

namespace Apotheosis.Runtime.Rendering.Targets
{
    /// <summary>
    /// 内置渲染目标枚举：按固定顺序（0..5）在 <see cref="RenderTargetModule"/> 初始化时自动注册，
    /// 底层均指向 tModLoader 主屏幕目标，区别在于各自的 <see cref="RenderTiming"/> Flush 时机。
    /// </summary>
    public enum BuiltInRenderTarget : byte
    {
        PreDrawOffscreen = 0,
        AfterTiles = 1,
        BeforeProjectiles = 2,
        AfterProjectiles = 3,
        AfterDust = 4,
        AfterInterface = 5,
    }

    /// <summary>
    /// 内置渲染目标的标准常量名（配合 <see cref="RenderTargetModule.Get(string)"/> 使用，杜绝手写拼错）。
    /// </summary>
    public static class BuiltInRenderTargetNames
    {
        public const string PreDrawOffscreen = "BuiltIn.PreDrawOffscreen";
        public const string AfterTiles = "BuiltIn.AfterTiles";
        public const string BeforeProjectiles = "BuiltIn.BeforeProjectiles";
        public const string AfterProjectiles = "BuiltIn.AfterProjectiles";
        public const string AfterDust = "BuiltIn.AfterDust";
        public const string AfterInterface = "BuiltIn.AfterInterface";
    }
}
