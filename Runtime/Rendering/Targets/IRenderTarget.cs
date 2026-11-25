#nullable enable

using System;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Targets
{
    /// <summary>统一渲染目标抽象接口。</summary>
    public interface IRenderTarget : IDisposable
    {
        /// <summary>全局唯一目标名称。</summary>
        string Name { get; }

        /// <summary>所属的原版 Flush 时机。</summary>
        RenderTiming Timing { get; }

        /// <summary>是否为当前主屏幕目标。</summary>
        bool IsMainScreen { get; }

        /// <summary>解析底层 GPU <see cref="RenderTarget2D"/>（主屏幕目标返回 <c>null</c> 表示沿用当前绑定）。</summary>
        RenderTarget2D? Resolve(GraphicsDevice graphicsDevice);

        /// <summary>渲染目标被绑定到 GPU 后触发的钩子（例如命名帧通道首次绑定自动清屏）。</summary>
        void OnBound(GraphicsDevice graphicsDevice) { }
    }

    /// <summary>默认主屏幕目标实现。</summary>
    internal sealed class MainScreenRenderTarget : IRenderTarget
    {
        public string Name { get; }
        public RenderTiming Timing { get; }
        public bool IsMainScreen => true;

        public MainScreenRenderTarget(string name, RenderTiming timing)
        {
            Name = name;
            Timing = timing;
        }

        public RenderTarget2D? Resolve(GraphicsDevice graphicsDevice) => null;

        public void Dispose() { }
    }
}
