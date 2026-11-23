#nullable enable

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Geometry
{
    /// <summary>渲染管线统一网格接口。</summary>
    public interface IMesh : IDisposable
    {
        /// <summary>图元拓扑类型。</summary>
        PrimitiveType PrimitiveType { get; }

        /// <summary>是否为空网格。</summary>
        bool IsEmpty { get; }

        /// <summary>是否为静态常驻 GPU 网格。</summary>
        bool IsStatic { get; }

        /// <summary>尝试获取该网格的世界空间变换矩阵；返回 <c>false</c> 表示顶点已处于世界空间。</summary>
        bool TryGetWorldMatrix(out Matrix worldMatrix)
        {
            worldMatrix = default;
            return false;
        }

        /// <summary>绘制前按需准备顶点或实例数据的可选钩子。</summary>
        void PrepareForDraw() { }

        /// <summary>获取顶点数据切片。</summary>
        ReadOnlySpan<UnifiedVertex> GetVertices();

        /// <summary>获取局部 32 位索引数据切片。</summary>
        ReadOnlySpan<int> GetIndices();
    }

    /// <summary>静态 GPU 网格缓冲接口。</summary>
    public interface IStaticGpuMesh : IMesh
    {
        VertexBuffer? VertexBuffer { get; }
        IndexBuffer? IndexBuffer { get; }
        int VertexCount { get; }
        int IndexCount { get; }
        bool EnsureGpuReady(GraphicsDevice graphicsDevice);
    }

    /// <summary>硬件实例化网格接口（支持单组或多分组/LOD）。</summary>
    public interface IInstancedMesh : IMesh
    {
        /// <summary>实例化分组数量。</summary>
        int GroupCount { get; }

        /// <summary>获取指定分组对应的静态基础网格句柄。</summary>
        MeshHandle GetGroupBaseMesh(int groupIndex);

        /// <summary>获取指定分组本帧的实例数据切片。</summary>
        ReadOnlySpan<InstanceData> GetGroupInstances(int groupIndex);
    }
}
