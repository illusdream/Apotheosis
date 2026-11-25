#nullable enable

using System;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Targets
{
    /// <summary>渲染目标切换作用域（支持 1~4 路 MRT 预分配压栈），退出时自动恢复先前绑定并刷新视图上下文。</summary>
    public readonly struct RenderTargetScope : IDisposable
    {
        private readonly RenderTargetModule? _owner;
        private readonly GraphicsDevice? _gd;
        private readonly int _depth;
        private readonly int _token;

        internal RenderTargetScope(RenderTargetModule owner, GraphicsDevice gd, int depth, int token)
        {
            _owner = owner;
            _gd = gd;
            _depth = depth;
            _token = token;
        }

        /// <summary>恢复压栈前保存的渲染目标绑定并通知视图上下文失效。</summary>
        public void Dispose()
        {
            if (_owner is not null && _gd is not null)
                _owner.PopScope(_gd, _depth, _token);
        }
    }
}
