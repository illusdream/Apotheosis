#nullable enable

using System;
using System.Runtime.InteropServices;
using Apotheosis.Runtime.Foundation.Transform;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Geometry
{
    /// <summary>渲染管线统一顶点结构（52 字节）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct UnifiedVertex : IVertexType
    {
        public static readonly VertexDeclaration Declaration = new(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
            new VertexElement(24, VertexElementFormat.Color, VertexElementUsage.Color, 0),
            new VertexElement(28, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(36, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 1));

        /// <summary>空间坐标（2D 时 Z=0，与 <see cref="Transform3D"/> 坐标系一致）。</summary>
        public Vector3 Position;

        /// <summary>表面法线（3D 光照法线；2D 可为 <c>(0, 0, -1)</c> 或切线方向）。</summary>
        public Vector3 Normal;

        /// <summary>RGBA 顶点色（染色、透明度淡出、受击闪白）。</summary>
        public Color Color;

        /// <summary>主贴图 UV 坐标（<c>TEXCOORD0</c>）。</summary>
        public Vector2 TexCoord;

        /// <summary>通用 4 通道扩展槽（<c>TEXCOORD1</c>：进度/线宽/溶解/闪白/切线等，由具体 Shader 自由解释）。</summary>
        public Vector4 Custom;

        public UnifiedVertex(Vector3 position, Color color, Vector2 texCoord)
            : this(position, new Vector3(0f, 0f, -1f), color, texCoord, Vector4.Zero)
        {
        }

        public UnifiedVertex(Vector2 position2D, Color color, Vector2 texCoord)
            : this(new Vector3(position2D, 0f), new Vector3(0f, 0f, -1f), color, texCoord, Vector4.Zero)
        {
        }

        public UnifiedVertex(Vector2 position2D, Color color, Vector2 texCoord, Vector4 custom)
            : this(new Vector3(position2D, 0f), new Vector3(0f, 0f, -1f), color, texCoord, custom)
        {
        }

        public UnifiedVertex(Vector3 position, Vector3 normal, Color color, Vector2 texCoord, Vector4 custom)
        {
            Position = position;
            Normal = normal;
            Color = color;
            TexCoord = texCoord;
            Custom = custom;
        }

        VertexDeclaration IVertexType.VertexDeclaration => Declaration;
    }

    /// <summary>2D/3D 通用硬件实例化数据（80 字节）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct InstanceData : IVertexType
    {
        public static readonly Vector4 FullUvRect = new(0f, 0f, 1f, 1f);

        public static readonly VertexDeclaration Declaration = new(
            new VertexElement(0, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 2),
            new VertexElement(16, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 3),
            new VertexElement(32, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 4),
            new VertexElement(48, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 5),
            new VertexElement(64, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 6));

        public Vector4 TransformCol0;
        public Vector4 TransformCol1;
        public Vector4 TransformCol2;
        public Vector4 Color;

        /// <summary>自定义 4 通道数据（默认 <c>(0, 0, 1, 1)</c>，2D 实例化时可作为图集 UV 矩形）。</summary>
        public Vector4 Custom;

        VertexDeclaration IVertexType.VertexDeclaration => Declaration;

        /// <summary>从 2D 位置、尺寸、旋转与颜色构建实例数据。</summary>
        public static InstanceData From2D(Vector2 position, Vector2 size, float rotation, Color color, Vector4? uvRectOrCustom = null, float depthZ = 0f)
        {
            float cos = MathF.Cos(rotation);
            float sin = MathF.Sin(rotation);

            // XNA 行向量 S(size) * Rz(rotation) * T(position):
            // M11 = sx*cos, M12 = sx*sin
            // M21 = -sy*sin, M22 = sy*cos
            return new InstanceData
            {
                TransformCol0 = new Vector4(size.X * cos, -size.Y * sin, 0f, position.X),
                TransformCol1 = new Vector4(size.X * sin, size.Y * cos, 0f, position.Y),
                TransformCol2 = new Vector4(0f, 0f, 1f, depthZ),
                Color = color.ToVector4(),
                Custom = uvRectOrCustom ?? FullUvRect,
            };
        }

        /// <summary>从 <see cref="Transform3D"/> 构建 3D/2D 通用实例数据。</summary>
        public static InstanceData FromTransform(in Transform3D transform, Color color, Vector4? custom = null)
        {
            Matrix m = transform.ToMatrix();
            return FromMatrix(in m, color, custom);
        }

        /// <summary>从任意 XNA 行向量仿射矩阵构建实例数据。</summary>
        public static InstanceData FromMatrix(in Matrix m, Color color, Vector4? custom = null)
        {
            return new InstanceData
            {
                TransformCol0 = new Vector4(m.M11, m.M21, m.M31, m.M41),
                TransformCol1 = new Vector4(m.M12, m.M22, m.M32, m.M42),
                TransformCol2 = new Vector4(m.M13, m.M23, m.M33, m.M43),
                Color = color.ToVector4(),
                Custom = custom ?? FullUvRect,
            };
        }
    }
}
