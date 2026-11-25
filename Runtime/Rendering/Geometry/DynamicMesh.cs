#nullable enable

using System;
using System.Runtime.CompilerServices;
using Apotheosis.Runtime.Foundation.Collections;
using Apotheosis.Runtime.Foundation.Transform;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Geometry
{
    /// <summary>通用可复用动态网格容器。</summary>
    public sealed class DynamicMesh : IMesh
    {
        private ValueBuffer<UnifiedVertex> _vertices;
        private ValueBuffer<int> _indices;

        public PrimitiveType PrimitiveType { get; set; }

        /// <summary>可选绑定的 3D 变换节点句柄。</summary>
        public TransformHandle TransformNodeHandle { get; set; }

        /// <summary>可选局部变换。</summary>
        public Transform3D LocalTransform { get; set; } = Transform3D.Identity;

        /// <summary>是否启用 <see cref="LocalTransform"/>。</summary>
        public bool UseLocalTransform { get; set; }

        public bool IsEmpty => _vertices.Count == 0 || _indices.Count == 0;
        public bool IsStatic => false;

        public int VertexCount => _vertices.Count;
        public int IndexCount => _indices.Count;

        public DynamicMesh(int initialVertexCapacity = 64, int initialIndexCapacity = 192, PrimitiveType primitiveType = PrimitiveType.TriangleList)
        {
            _vertices = new ValueBuffer<UnifiedVertex>(initialVertexCapacity, defaultGrowCapacity: 64);
            _indices = new ValueBuffer<int>(initialIndexCapacity, defaultGrowCapacity: 192);
            PrimitiveType = primitiveType;
        }

        /// <summary>重置当前写入计数（保留底层数组容量，零分配）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            _vertices.Clear(clearReferences: false);
            _indices.Clear(clearReferences: false);
        }

        /// <summary>归还对象池时重置计数与变换状态。</summary>
        internal void ResetForPool()
        {
            Clear();
            PrimitiveType = PrimitiveType.TriangleList;
            TransformNodeHandle = default;
            LocalTransform = Transform3D.Identity;
            UseLocalTransform = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int AddVertex(in UnifiedVertex vertex) => _vertices.Add(in vertex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddIndex(int index) => _indices.Add(in index);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddTriangleIndices(int i0, int i1, int i2)
        {
            Span<int> dst = _indices.AllocateSpan(3);
            dst[0] = i0;
            dst[1] = i1;
            dst[2] = i2;
        }

        /// <summary>追加一个四边形（4 顶点 + 6 索引）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddQuadVertices(in UnifiedVertex v0, in UnifiedVertex v1, in UnifiedVertex v2, in UnifiedVertex v3)
        {
            int baseV = _vertices.Allocate(4, out Span<UnifiedVertex> verts);
            verts[0] = v0;
            verts[1] = v1;
            verts[2] = v2;
            verts[3] = v3;

            Span<int> idx = _indices.AllocateSpan(6);
            idx[0] = baseV;
            idx[1] = baseV + 1;
            idx[2] = baseV + 2;
            idx[3] = baseV;
            idx[4] = baseV + 2;
            idx[5] = baseV + 3;
        }

        /// <summary>预留连续顶点空间并返回可写 Span（返回起始顶点索引）。</summary>
        public int AllocateVertices(int count, out Span<UnifiedVertex> span) => _vertices.Allocate(count, out span);

        /// <summary>预留连续索引空间并返回可写 Span。</summary>
        public Span<int> AllocateIndices(int count) => _indices.AllocateSpan(count);

        /// <summary>将当前已构建好的几何数据一键烘焙为常驻 GPU 的 <see cref="StaticMesh"/>。</summary>
        public StaticMesh BakeToStatic() => new(GetVertices(), GetIndices(), PrimitiveType);

        public bool TryGetWorldMatrix(out Matrix worldMatrix)
        {
            if (TransformNodeHandle.TryGetNode(out TransformNode? node))
            {
                worldMatrix = node.WorldMatrix;
                return true;
            }

            if (UseLocalTransform)
            {
                worldMatrix = LocalTransform.ToMatrix();
                return true;
            }

            worldMatrix = default;
            return false;
        }

        public ReadOnlySpan<UnifiedVertex> GetVertices() => _vertices.AsReadOnlySpan();
        public ReadOnlySpan<int> GetIndices() => _indices.AsReadOnlySpan();

        public void Dispose() { }
    }
}
