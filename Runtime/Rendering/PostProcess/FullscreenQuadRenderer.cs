#nullable enable

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;

namespace Apotheosis.Runtime.Rendering.PostProcess
{
    /// <summary>可复用的全屏四边形 GPU 几何绘制器（零每帧分配）。</summary>
    internal sealed class FullscreenQuadRenderer : IDisposable
    {
        private readonly object _sync = new();
        private VertexBuffer? _vertexBuffer;
        private IndexBuffer? _indexBuffer;
        private bool _disposed;

        /// <summary>在当前绑定的渲染目标与着色器 Pass 上绘制全屏四边形。</summary>
        public void Draw(GraphicsDevice graphicsDevice)
        {
            RenderManager.Shaders.AssertMainThread("FullscreenQuadRenderer.Draw");

            if (RenderManager.IsUnloading || graphicsDevice.IsDisposed)
                return;

            EnsureBuffers(graphicsDevice);

            VertexBuffer? vb;
            IndexBuffer? ib;
            lock (_sync)
            {
                if (_disposed || _vertexBuffer is null || _indexBuffer is null || _vertexBuffer.IsDisposed || _indexBuffer.IsDisposed)
                    return;

                vb = _vertexBuffer;
                ib = _indexBuffer;
            }

            try
            {
                graphicsDevice.SetVertexBuffer(vb);
                graphicsDevice.Indices = ib;
                graphicsDevice.DrawIndexedPrimitives(PrimitiveType.TriangleList, baseVertex: 0, minVertexIndex: 0, numVertices: 4, startIndex: 0, primitiveCount: 2);
            }
            finally
            {
                // 解绑顶点流与索引缓冲，避免污染后续原版绘制
                graphicsDevice.SetVertexBuffer(null);
                graphicsDevice.Indices = null;
            }
        }

        /// <summary>释放全屏四边形 GPU 缓冲（非主线程调用自动派发至主线程）。</summary>
        public void Dispose()
        {
            VertexBuffer? vb;
            IndexBuffer? ib;
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                vb = _vertexBuffer;
                ib = _indexBuffer;
                _vertexBuffer = null;
                _indexBuffer = null;
            }

            if (vb is null && ib is null)
                return;

            void DisposeBuffers()
            {
                try { if (vb is { IsDisposed: false }) vb.Dispose(); } catch { /* 卸载期忽略 */ }
                try { if (ib is { IsDisposed: false }) ib.Dispose(); } catch { /* 卸载期忽略 */ }
            }

            if (RenderManager.Shaders.IsOnMainThread)
                DisposeBuffers();
            else
                Main.QueueMainThreadAction(DisposeBuffers);
        }

        internal void ResetForLoad()
        {
            lock (_sync)
            {
                _disposed = false;
            }
        }

        private void EnsureBuffers(GraphicsDevice gd)
        {
            lock (_sync)
            {
                if (_disposed || (_vertexBuffer is { IsDisposed: false } && _indexBuffer is { IsDisposed: false }))
                    return;
            }

            // FNA NDC [-1..1] 直接对应 UV [0..1]，无需半像素偏移
            VertexPositionTexture[] vertices =
            {
                new(new Vector3(-1f,  1f, 0f), new Vector2(0f, 0f)),
                new(new Vector3( 1f,  1f, 0f), new Vector2(1f, 0f)),
                new(new Vector3( 1f, -1f, 0f), new Vector2(1f, 1f)),
                new(new Vector3(-1f, -1f, 0f), new Vector2(0f, 1f)),
            };
            short[] indices = { 0, 1, 2, 0, 2, 3 };

            var newVb = new VertexBuffer(gd, VertexPositionTexture.VertexDeclaration, 4, BufferUsage.WriteOnly);
            newVb.SetData(vertices);

            var newIb = new IndexBuffer(gd, IndexElementSize.SixteenBits, 6, BufferUsage.WriteOnly);
            newIb.SetData(indices);

            VertexBuffer? oldVb = null;
            IndexBuffer? oldIb = null;
            lock (_sync)
            {
                if (_disposed || RenderManager.IsUnloading)
                {
                    newVb.Dispose();
                    newIb.Dispose();
                    return;
                }

                oldVb = _vertexBuffer;
                oldIb = _indexBuffer;
                _vertexBuffer = newVb;
                _indexBuffer = newIb;
            }

            if (oldVb is { IsDisposed: false }) oldVb.Dispose();
            if (oldIb is { IsDisposed: false }) oldIb.Dispose();
        }
    }
}
