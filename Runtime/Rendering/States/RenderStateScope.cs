#nullable enable

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.States
{
    /// <summary>保存并在退出作用域时自动恢复常用 GPU 管线状态。</summary>
    public readonly ref struct RenderStateScope
    {
        private readonly GraphicsDevice _graphicsDevice;
        private readonly BlendState _blendState;
        private readonly DepthStencilState _depthStencilState;
        private readonly RasterizerState _rasterizerState;
        private readonly Viewport _viewport;
        private readonly Rectangle _scissorRectangle;
        private readonly SamplerState? _sampler0;
        private readonly SamplerState? _sampler1;
        private readonly SamplerState? _sampler2;
        private readonly SamplerState? _sampler3;
        private readonly bool _valid;

        private RenderStateScope(GraphicsDevice gd)
        {
            _graphicsDevice = gd;
            _blendState = gd.BlendState;
            _depthStencilState = gd.DepthStencilState;
            _rasterizerState = gd.RasterizerState;
            _viewport = gd.Viewport;
            _scissorRectangle = gd.ScissorRectangle;
            _sampler0 = gd.SamplerStates[0];
            _sampler1 = gd.SamplerStates[1];
            _sampler2 = gd.SamplerStates[2];
            _sampler3 = gd.SamplerStates[3];
            _valid = true;
        }

        /// <summary>捕获当前 <see cref="GraphicsDevice"/> 状态快照。</summary>
        public static RenderStateScope Capture(GraphicsDevice gd) => new(gd);

        public void Dispose()
        {
            if (!_valid || _graphicsDevice.IsDisposed)
                return;

            _graphicsDevice.BlendState = _blendState;
            _graphicsDevice.DepthStencilState = _depthStencilState;
            _graphicsDevice.RasterizerState = _rasterizerState;
            _graphicsDevice.Viewport = _viewport;
            _graphicsDevice.ScissorRectangle = _scissorRectangle;

            if (_sampler0 is not null) _graphicsDevice.SamplerStates[0] = _sampler0;
            if (_sampler1 is not null) _graphicsDevice.SamplerStates[1] = _sampler1;
            if (_sampler2 is not null) _graphicsDevice.SamplerStates[2] = _sampler2;
            if (_sampler3 is not null) _graphicsDevice.SamplerStates[3] = _sampler3;
        }
    }
}
