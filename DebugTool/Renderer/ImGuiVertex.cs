#if DEBUG
#nullable enable

using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.DebugTool.Renderer
{
    /// <summary>
    /// 与 Dear ImGui 原生 <c>ImDrawVert</c> 逐字节对齐的 20 字节 FNA 顶点结构，支持零转换批量上传。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ImGuiVertex : IVertexType
    {
        public const int SizeInBytes = 20;

        public Vector2 Position;
        public Vector2 TextureCoordinate;
        public Color Color;

        public static readonly VertexDeclaration Declaration = new(
            SizeInBytes,
            new VertexElement(0, VertexElementFormat.Vector2, VertexElementUsage.Position, 0),
            new VertexElement(8, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(16, VertexElementFormat.Color, VertexElementUsage.Color, 0)
        );

        VertexDeclaration IVertexType.VertexDeclaration => Declaration;
    }
}
#endif
