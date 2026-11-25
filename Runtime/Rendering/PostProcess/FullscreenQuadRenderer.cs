#nullable enable

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

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

            GpuResourceHelper.RunOnMainThread(() =>
            {
                GpuResourceHelper.DisposeSafe(vb);
                GpuResourceHelper.DisposeSafe(ib);
            });
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
                if (_disposed || (_vertexBuffer is { IsDisposed: false } vb && ReferenceEquals(vb.GraphicsDevice, gd)
                    && _indexBuffer is { IsDisposed: false } ib && ReferenceEquals(ib.GraphicsDevice, gd)))
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

            VertexBuffer? newVb = null;
            IndexBuffer? newIb = null;
            try
            {
                newVb = new VertexBuffer(gd, VertexPositionTexture.VertexDeclaration, 4, BufferUsage.WriteOnly);
                newVb.SetData(vertices);

                newIb = new IndexBuffer(gd, IndexElementSize.SixteenBits, 6, BufferUsage.WriteOnly);
                newIb.SetData(indices);
            }
            catch
            {
                GpuResourceHelper.DisposeSafe(newVb);
                GpuResourceHelper.DisposeSafe(newIb);
                throw;
            }

            VertexBuffer? oldVb = null;
            IndexBuffer? oldIb = null;
            lock (_sync)
            {
                if (_disposed || RenderManager.IsUnloading)
                {
                    GpuResourceHelper.DisposeSafe(newVb);
                    GpuResourceHelper.DisposeSafe(newIb);
                    return;
                }

                oldVb = _vertexBuffer;
                oldIb = _indexBuffer;
                _vertexBuffer = newVb;
                _indexBuffer = newIb;
            }

            GpuResourceHelper.DisposeSafe(oldVb);
            GpuResourceHelper.DisposeSafe(oldIb);
        }
    }
}
