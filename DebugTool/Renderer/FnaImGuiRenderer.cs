#if DEBUG
#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria.ModLoader;

namespace Apotheosis.DebugTool.Renderer
{
    /// <summary>
    /// 基于 tModLoader 原版 FNA (<see cref="GraphicsDevice"/> + <see cref="BasicEffect"/>) 的 Dear ImGui 1.92 渲染后端。
    /// <para>
    /// 支持动态字体与纹理更新（<see cref="ImGuiBackendFlags.RendererHasTextures"/>）以及大网格顶点偏移
    /// （<see cref="ImGuiBackendFlags.RendererHasVtxOffset"/>）。
    /// </para>
    /// </summary>
    [NoJIT]
    internal sealed unsafe class FnaImGuiRenderer : IDisposable
    {
        private readonly GraphicsDevice _graphicsDevice;
        private readonly BasicEffect _effect;
        private readonly RasterizerState _rasterizerState;

        private readonly Dictionary<ulong, Texture2D> _textures = new();
        private ulong _nextTextureId = 1;

        private VertexBuffer? _vertexBuffer;
        private int _vertexBufferSize;
        private ImGuiVertex[] _vertexStaging = Array.Empty<ImGuiVertex>();

        private IndexBuffer? _indexBuffer;
        private int _indexBufferSize;
        private ushort[] _indexStaging = Array.Empty<ushort>();

        private byte[] _pixelUploadBuffer = Array.Empty<byte>();
        private bool _disposed;

        public int BoundTextureCount => _textures.Count;
        public int LastVertexCount { get; private set; }
        public int LastIndexCount { get; private set; }
        public int LastDrawCalls { get; private set; }

        public FnaImGuiRenderer(GraphicsDevice graphicsDevice)
        {
            _graphicsDevice = graphicsDevice ?? throw new ArgumentNullException(nameof(graphicsDevice));

            _effect = new BasicEffect(_graphicsDevice)
            {
                World = Matrix.Identity,
                View = Matrix.Identity,
                TextureEnabled = true,
                VertexColorEnabled = true,
                LightingEnabled = false
            };

            _rasterizerState = new RasterizerState
            {
                CullMode = CullMode.None,
                DepthBias = 0f,
                FillMode = FillMode.Solid,
                MultiSampleAntiAlias = false,
                ScissorTestEnable = true,
                SlopeScaleDepthBias = 0f
            };
        }

        /// <summary>
        /// 执行 Dear ImGui 渲染数据提交到 FNA <see cref="GraphicsDevice"/>。
        /// </summary>
        public void RenderDrawData(ImDrawDataPtr drawData)
        {
            if (_disposed || drawData.IsNull || !drawData.Valid)
                return;

            // 1. 先处理 Dear ImGui 1.92 动态纹理创建 / 增量更新 / 销毁请求
            ProcessTextureUpdates(drawData);

            int fbWidth = (int)(drawData.DisplaySize.X * drawData.FramebufferScale.X);
            int fbHeight = (int)(drawData.DisplaySize.Y * drawData.FramebufferScale.Y);
            if (fbWidth <= 0 || fbHeight <= 0 || drawData.TotalVtxCount <= 0 || drawData.TotalIdxCount <= 0)
            {
                LastVertexCount = 0;
                LastIndexCount = 0;
                LastDrawCalls = 0;
                return;
            }

            // 2. 合并上传全部顶点与索引缓冲区
            UploadBuffers(drawData);

            // 3. 备份 FNA GraphicsDevice 状态，确保对泰拉瑞亚渲染零副作用
            Viewport prevViewport = _graphicsDevice.Viewport;
            Rectangle prevScissor = _graphicsDevice.ScissorRectangle;
            BlendState prevBlend = _graphicsDevice.BlendState;
            DepthStencilState prevDepth = _graphicsDevice.DepthStencilState;
            RasterizerState prevRaster = _graphicsDevice.RasterizerState;
            SamplerState prevSampler = _graphicsDevice.SamplerStates[0];
            Texture? prevTexture = _graphicsDevice.Textures[0];
            IndexBuffer? prevIndices = _graphicsDevice.Indices;
            VertexBufferBinding[] prevVertexBuffers = _graphicsDevice.GetVertexBuffers();

            int drawCalls = 0;

            try
            {
                _graphicsDevice.Viewport = new Viewport(0, 0, fbWidth, fbHeight);
                _graphicsDevice.BlendState = BlendState.NonPremultiplied;
                _graphicsDevice.DepthStencilState = DepthStencilState.None;
                _graphicsDevice.RasterizerState = _rasterizerState;
                _graphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;

                _effect.Projection = Matrix.CreateOrthographicOffCenter(
                    drawData.DisplayPos.X,
                    drawData.DisplayPos.X + drawData.DisplaySize.X,
                    drawData.DisplayPos.Y + drawData.DisplaySize.Y,
                    drawData.DisplayPos.Y,
                    -1f,
                    1f);

                _graphicsDevice.SetVertexBuffer(_vertexBuffer);
                _graphicsDevice.Indices = _indexBuffer;

                System.Numerics.Vector2 clipOff = drawData.DisplayPos;
                System.Numerics.Vector2 clipScale = drawData.FramebufferScale;

                int globalVtxOffset = 0;
                int globalIdxOffset = 0;

                for (int n = 0; n < drawData.CmdListsCount; n++)
                {
                    ImDrawListPtr cmdList = drawData.CmdLists[n];

                    for (int cmdIdx = 0; cmdIdx < cmdList.CmdBuffer.Size; cmdIdx++)
                    {
                        ref ImDrawCmd cmd = ref cmdList.CmdBuffer.Data[cmdIdx];

                        if (cmd.UserCallback != null)
                            continue;

                        if (cmd.ElemCount == 0)
                            continue;

                        float clipMinX = (cmd.ClipRect.X - clipOff.X) * clipScale.X;
                        float clipMinY = (cmd.ClipRect.Y - clipOff.Y) * clipScale.Y;
                        float clipMaxX = (cmd.ClipRect.Z - clipOff.X) * clipScale.X;
                        float clipMaxY = (cmd.ClipRect.W - clipOff.Y) * clipScale.Y;

                        if (clipMaxX <= clipMinX || clipMaxY <= clipMinY)
                            continue;

                        int sx = Math.Clamp((int)MathF.Floor(clipMinX), 0, fbWidth);
                        int sy = Math.Clamp((int)MathF.Floor(clipMinY), 0, fbHeight);
                        int ex = Math.Clamp((int)MathF.Ceiling(clipMaxX), 0, fbWidth);
                        int ey = Math.Clamp((int)MathF.Ceiling(clipMaxY), 0, fbHeight);

                        if (ex <= sx || ey <= sy)
                            continue;

                        ulong texId = cmd.GetTexID().Handle;
                        if (!_textures.TryGetValue(texId, out Texture2D? texture) || texture.IsDisposed)
                            continue;

                        _graphicsDevice.ScissorRectangle = new Rectangle(sx, sy, ex - sx, ey - sy);
                        _effect.Texture = texture;

                        int baseVertex = globalVtxOffset + (int)cmd.VtxOffset;
                        int startIndex = globalIdxOffset + (int)cmd.IdxOffset;
                        int numVertices = cmdList.VtxBuffer.Size - (int)cmd.VtxOffset;
                        int primitiveCount = (int)(cmd.ElemCount / 3);

                        if (primitiveCount <= 0 || numVertices <= 0)
                            continue;

                        foreach (EffectPass pass in _effect.CurrentTechnique.Passes)
                        {
                            pass.Apply();
                            _graphicsDevice.DrawIndexedPrimitives(
                                PrimitiveType.TriangleList,
                                baseVertex,
                                0,
                                numVertices,
                                startIndex,
                                primitiveCount);
                        }

                        drawCalls++;
                    }

                    globalVtxOffset += cmdList.VtxBuffer.Size;
                    globalIdxOffset += cmdList.IdxBuffer.Size;
                }

                LastVertexCount = drawData.TotalVtxCount;
                LastIndexCount = drawData.TotalIdxCount;
                LastDrawCalls = drawCalls;
            }
            finally
            {
                _graphicsDevice.Viewport = prevViewport;
                _graphicsDevice.ScissorRectangle = prevScissor;
                _graphicsDevice.BlendState = prevBlend;
                _graphicsDevice.DepthStencilState = prevDepth;
                _graphicsDevice.RasterizerState = prevRaster;
                _graphicsDevice.SamplerStates[0] = prevSampler;
                _graphicsDevice.Textures[0] = prevTexture;
                _graphicsDevice.Indices = prevIndices;

                if (prevVertexBuffers.Length > 0)
                    _graphicsDevice.SetVertexBuffers(prevVertexBuffers);
                else
                    _graphicsDevice.SetVertexBuffer(null);
            }
        }

        private void ProcessTextureUpdates(ImDrawDataPtr drawData)
        {
            if (drawData.Handle->Textures == null)
                return;

            ImVector<ImTextureDataPtr> texturesVec = *drawData.Handle->Textures;
            if (texturesVec.Data == null || texturesVec.Size <= 0)
                return;

            for (int i = 0; i < texturesVec.Size; i++)
            {
                ImTextureDataPtr tex = texturesVec[i];
                if (tex.IsNull || tex.Status == ImTextureStatus.Ok || tex.Status == ImTextureStatus.Destroyed)
                    continue;

                switch (tex.Status)
                {
                    case ImTextureStatus.WantCreate:
                        CreateBackendTexture(tex);
                        break;

                    case ImTextureStatus.WantUpdates:
                        UpdateBackendTexture(tex);
                        break;

                    case ImTextureStatus.WantDestroy:
                        DestroyBackendTexture(tex);
                        break;
                }
            }
        }

        private void CreateBackendTexture(ImTextureDataPtr tex)
        {
            int width = tex.Width;
            int height = tex.Height;
            if (width <= 0 || height <= 0 || tex.Pixels == null)
                return;

            var texture = new Texture2D(_graphicsDevice, width, height, false, SurfaceFormat.Color);
            UploadSubRect(texture, tex, 0, 0, width, height);

            ulong id = _nextTextureId++;
            _textures[id] = texture;

            tex.SetTexID(new ImTextureID(id));
            tex.SetStatus(ImTextureStatus.Ok);
        }

        private void UpdateBackendTexture(ImTextureDataPtr tex)
        {
            ulong id = tex.TexID.Handle;
            if (!_textures.TryGetValue(id, out Texture2D? texture) || texture.IsDisposed)
            {
                CreateBackendTexture(tex);
                return;
            }

            if (texture.Width != tex.Width || texture.Height != tex.Height)
            {
                DestroyBackendTexture(tex);
                CreateBackendTexture(tex);
                return;
            }

            if (tex.Updates.Size > 0)
            {
                for (int r = 0; r < tex.Updates.Size; r++)
                {
                    ImTextureRect rect = tex.Updates[r];
                    if (rect.W > 0 && rect.H > 0)
                        UploadSubRect(texture, tex, rect.X, rect.Y, rect.W, rect.H);
                }
            }
            else if (tex.UpdateRect.W > 0 && tex.UpdateRect.H > 0)
            {
                ImTextureRect rect = tex.UpdateRect;
                UploadSubRect(texture, tex, rect.X, rect.Y, rect.W, rect.H);
            }

            tex.SetStatus(ImTextureStatus.Ok);
        }

        private void DestroyBackendTexture(ImTextureDataPtr tex)
        {
            ulong id = tex.TexID.Handle;
            if (id != 0 && _textures.Remove(id, out Texture2D? texture) && !texture.IsDisposed)
                texture.Dispose();

            tex.SetTexID(ImTextureID.Null);
            tex.SetStatus(ImTextureStatus.Destroyed);
        }

        private void UploadSubRect(Texture2D texture, ImTextureDataPtr tex, int x, int y, int w, int h)
        {
            int requiredBytes = w * h * 4;
            if (_pixelUploadBuffer.Length < requiredBytes)
                _pixelUploadBuffer = new byte[Math.Max(requiredBytes, _pixelUploadBuffer.Length * 2)];

            int srcPitch = tex.GetPitch();
            int bpp = tex.BytesPerPixel;
            byte* srcBase = (byte*)tex.GetPixelsAt(x, y);

            fixed (byte* dstBase = _pixelUploadBuffer)
            {
                if (tex.Format == ImTextureFormat.Rgba32 && bpp == 4)
                {
                    int rowBytes = w * 4;
                    for (int row = 0; row < h; row++)
                    {
                        Buffer.MemoryCopy(
                            srcBase + (long)row * srcPitch,
                            dstBase + (long)row * rowBytes,
                            rowBytes,
                            rowBytes);
                    }
                }
                else
                {
                    // Alpha8 兼容路径：展开为纯白 + Alpha 的 RGBA32
                    for (int row = 0; row < h; row++)
                    {
                        byte* srcRow = srcBase + (long)row * srcPitch;
                        byte* dstRow = dstBase + (long)row * w * 4;
                        for (int col = 0; col < w; col++)
                        {
                            int o = col * 4;
                            dstRow[o] = 255;
                            dstRow[o + 1] = 255;
                            dstRow[o + 2] = 255;
                            dstRow[o + 3] = srcRow[col];
                        }
                    }
                }
            }

            texture.SetData(0, new Rectangle(x, y, w, h), _pixelUploadBuffer, 0, requiredBytes);
        }

        private void UploadBuffers(ImDrawDataPtr drawData)
        {
            int totalVtx = drawData.TotalVtxCount;
            int totalIdx = drawData.TotalIdxCount;

            if (_vertexBuffer is null || _vertexBuffer.IsDisposed || totalVtx > _vertexBufferSize)
            {
                _vertexBuffer?.Dispose();
                _vertexBufferSize = Math.Max(totalVtx + 2048, (int)(_vertexBufferSize * 1.5f));
                _vertexBuffer = new DynamicVertexBuffer(
                    _graphicsDevice,
                    ImGuiVertex.Declaration,
                    _vertexBufferSize,
                    BufferUsage.WriteOnly);
                _vertexStaging = new ImGuiVertex[_vertexBufferSize];
            }

            if (_indexBuffer is null || _indexBuffer.IsDisposed || totalIdx > _indexBufferSize)
            {
                _indexBuffer?.Dispose();
                _indexBufferSize = Math.Max(totalIdx + 4096, (int)(_indexBufferSize * 1.5f));
                _indexBuffer = new DynamicIndexBuffer(
                    _graphicsDevice,
                    IndexElementSize.SixteenBits,
                    _indexBufferSize,
                    BufferUsage.WriteOnly);
                _indexStaging = new ushort[_indexBufferSize];
            }

            int vtxCursor = 0;
            int idxCursor = 0;

            fixed (ImGuiVertex* dstVtx = _vertexStaging)
            fixed (ushort* dstIdx = _indexStaging)
            {
                for (int n = 0; n < drawData.CmdListsCount; n++)
                {
                    ImDrawListPtr cmdList = drawData.CmdLists[n];

                    int vtxCount = cmdList.VtxBuffer.Size;
                    if (vtxCount > 0)
                    {
                        long vtxBytes = (long)vtxCount * ImGuiVertex.SizeInBytes;
                        Buffer.MemoryCopy(
                            cmdList.VtxBuffer.Data,
                            dstVtx + vtxCursor,
                            vtxBytes,
                            vtxBytes);
                        vtxCursor += vtxCount;
                    }

                    int idxCount = cmdList.IdxBuffer.Size;
                    if (idxCount > 0)
                    {
                        long idxBytes = (long)idxCount * sizeof(ushort);
                        Buffer.MemoryCopy(
                            cmdList.IdxBuffer.Data,
                            dstIdx + idxCursor,
                            idxBytes,
                            idxBytes);
                        idxCursor += idxCount;
                    }
                }
            }

            ((DynamicVertexBuffer)_vertexBuffer).SetData(
                0,
                _vertexStaging,
                0,
                vtxCursor,
                ImGuiVertex.SizeInBytes,
                SetDataOptions.Discard);

            ((DynamicIndexBuffer)_indexBuffer).SetData(
                0,
                _indexStaging,
                0,
                idxCursor,
                SetDataOptions.Discard);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            foreach (Texture2D tex in _textures.Values)
            {
                if (!tex.IsDisposed)
                    tex.Dispose();
            }

            _textures.Clear();

            _vertexBuffer?.Dispose();
            _vertexBuffer = null;

            _indexBuffer?.Dispose();
            _indexBuffer = null;

            _effect.Dispose();
            _rasterizerState.Dispose();
        }
    }
}
#endif
