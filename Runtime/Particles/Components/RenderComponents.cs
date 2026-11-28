#nullable enable

using Apotheosis.Runtime.Rendering.Geometry;
using Apotheosis.Runtime.Rendering.Materials;
using Apotheosis.Runtime.Rendering.Targets;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Components
{
    /// <summary>所有可渲染粒子共用的基础渲染数据组件（渲染路由与通用基础视觉数值）。</summary>
    public struct ParticleRenderBase : IComponent
    {
        /// <summary>提交的目标渲染目标句柄（决定所属 <c>RenderTiming</c> 或离屏通道如 <c>BloomMask</c>）。</summary>
        public RenderTargetHandle Target;

        /// <summary>同一渲染目标内的子图层编号（升序绘制）。</summary>
        public byte SubLayer;

        /// <summary>同一子图层内的排序权重（有符号升序绘制）。</summary>
        public short SortOrder;

        /// <summary>粒子使用的受管材质句柄。</summary>
        public MaterialHandle Material;

        /// <summary>HDR 乘法染色（各通道可大于 <c>1.0</c> 直接驱动选择性 Bloom）。</summary>
        public Vector4 Color;

        /// <summary>粒子未缩放时的基准像素宽高。</summary>
        public Vector2 BaseSize;

        /// <summary>相对面片中心的归一化锚点偏移（默认 <c>(0, 0)</c> 表示中心对齐）。</summary>
        public Vector2 Pivot;

        public ParticleRenderBase(RenderTargetHandle target, MaterialHandle material, Vector2 baseSize, Vector4? color = null, byte subLayer = 0, short sortOrder = 0, Vector2 pivot = default)
        {
            Target = target;
            SubLayer = subLayer;
            SortOrder = sortOrder;
            Material = material;
            Color = color ?? Vector4.One;
            BaseSize = baseSize;
            Pivot = pivot;
        }

        public ParticleRenderBase(BuiltInRenderTarget target, MaterialHandle material, Vector2 baseSize, Color color, byte subLayer = 0, short sortOrder = 0, Vector2 pivot = default)
            : this((RenderTargetHandle)target, material, baseSize, color.ToVector4(), subLayer, sortOrder, pivot)
        {
        }
    }

    /// <summary>基础图集 2D 面片 Shader（<c>DefaultBatch.fx / InstancedTech</c>）专属渲染组件。</summary>
    public struct QuadAtlasRenderData : IComponent
    {
        /// <summary>归一化图集 UV 矩形 <c>(uOffset, vOffset, uScale, vScale)</c>，直接映射至 <see cref="InstanceData.Custom"/>。</summary>
        public Vector4 UvRect;

        public QuadAtlasRenderData() => UvRect = InstanceData.FullUvRect;

        public QuadAtlasRenderData(Vector4 uvRect) => UvRect = uvRect;

        /// <summary>按图集总像素尺寸与子矩形构建归一化 UV 矩形。</summary>
        public static QuadAtlasRenderData FromPixels(int atlasWidth, int atlasHeight, Rectangle sourceRect)
        {
            if (atlasWidth <= 0 || atlasHeight <= 0)
                return new QuadAtlasRenderData(InstanceData.FullUvRect);

            float invW = 1f / atlasWidth;
            float invH = 1f / atlasHeight;
            return new QuadAtlasRenderData(new Vector4(sourceRect.X * invW, sourceRect.Y * invH, sourceRect.Width * invW, sourceRect.Height * invH));
        }

        /// <summary>按行列网格与帧索引计算对应单元格的归一化 UV 矩形。</summary>
        public static QuadAtlasRenderData FromGridCell(int columns, int rows, int frameIndex)
        {
            if (columns <= 0 || rows <= 0)
                return new QuadAtlasRenderData(InstanceData.FullUvRect);

            long total = (long)columns * rows;
            long idx = ((frameIndex % total) + total) % total;
            int col = (int)(idx % columns);
            int row = (int)(idx / columns);
            float cellW = 1f / columns;
            float cellH = 1f / rows;
            return new QuadAtlasRenderData(new Vector4(col * cellW, row * cellH, cellW, cellH));
        }
    }
}
