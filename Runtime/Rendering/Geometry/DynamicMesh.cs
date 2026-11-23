#nullable enable

using System;
using System.Runtime.CompilerServices;
using Apotheosis.Runtime.Foundation.Transform;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Geometry
{
    /// <summary>通用可复用动态网格容器。</summary>
    public sealed class DynamicMesh : IMesh
    {
        private UnifiedVertex[] _vertices;
        private int[] _indices;
        private int _vertexCount;
        private int _indexCount;

        public PrimitiveType PrimitiveType { get; set; }

        /// <summary>可选绑定的 3D 变换节点句柄。</summary>
        public TransformHandle TransformNodeHandle { get; set; }

        /// <summary>可选局部变换。</summary>
        public Transform3D LocalTransform { get; set; } = Transform3D.Identity;

        /// <summary>是否启用 <see cref="LocalTransform"/>。</summary>
        public bool UseLocalTransform { get; set; }

        public bool IsEmpty => _vertexCount == 0 || _indexCount == 0;
        public bool IsStatic => false;

        public int VertexCount => _vertexCount;
        public int IndexCount => _indexCount;

        public DynamicMesh(int initialVertexCapacity = 64, int initialIndexCapacity = 192, PrimitiveType primitiveType = PrimitiveType.TriangleList)
        {
            _vertices = initialVertexCapacity > 0 ? new UnifiedVertex[initialVertexCapacity] : Array.Empty<UnifiedVertex>();
            _indices = initialIndexCapacity > 0 ? new int[initialIndexCapacity] : Array.Empty<int>();
            PrimitiveType = primitiveType;
        }

        /// <summary>重置当前写入计数（保留底层数组容量，零分配）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            _vertexCount = 0;
            _indexCount = 0;
        }

        /// <summary>归还对象池时重置计数与变换状态。</summary>
        internal void ResetForPool()
        {
            _vertexCount = 0;
            _indexCount = 0;
            PrimitiveType = PrimitiveType.TriangleList;
            TransformNodeHandle = default;
            LocalTransform = Transform3D.Identity;
            UseLocalTransform = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int AddVertex(in UnifiedVertex vertex)
        {
            EnsureVertexCapacity(_vertexCount + 1);
            int idx = _vertexCount++;
            _vertices[idx] = vertex;
            return idx;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddIndex(int index)
        {
            EnsureIndexCapacity(_indexCount + 1);
            _indices[_indexCount++] = index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddTriangleIndices(int i0, int i1, int i2)
        {
            EnsureIndexCapacity(_indexCount + 3);
            int dst = _indexCount;
            _indices[dst] = i0;
            _indices[dst + 1] = i1;
            _indices[dst + 2] = i2;
            _indexCount = dst + 3;
        }

        /// <summary>追加一个四边形（4 顶点 + 6 索引）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddQuadVertices(in UnifiedVertex v0, in UnifiedVertex v1, in UnifiedVertex v2, in UnifiedVertex v3)
        {
            EnsureVertexCapacity(_vertexCount + 4);
            EnsureIndexCapacity(_indexCount + 6);

            int baseV = _vertexCount;
            _vertices[baseV] = v0;
            _vertices[baseV + 1] = v1;
            _vertices[baseV + 2] = v2;
            _vertices[baseV + 3] = v3;
            _vertexCount = baseV + 4;

            int baseI = _indexCount;
            _indices[baseI] = baseV;
            _indices[baseI + 1] = baseV + 1;
            _indices[baseI + 2] = baseV + 2;
            _indices[baseI + 3] = baseV;
            _indices[baseI + 4] = baseV + 2;
            _indices[baseI + 5] = baseV + 3;
            _indexCount = baseI + 6;
        }

        /// <summary>预留连续顶点空间并返回可写 Span（返回起始顶点索引）。</summary>
        public int AllocateVertices(int count, out Span<UnifiedVertex> span)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count == 0)
            {
                span = Span<UnifiedVertex>.Empty;
                return _vertexCount;
            }

            EnsureVertexCapacity(_vertexCount + count);
            int start = _vertexCount;
            _vertexCount += count;
            span = _vertices.AsSpan(start, count);
            return start;
        }

        /// <summary>预留连续索引空间并返回可写 Span。</summary>
        public Span<int> AllocateIndices(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count == 0)
                return Span<int>.Empty;

            EnsureIndexCapacity(_indexCount + count);
            int start = _indexCount;
            _indexCount += count;
            return _indices.AsSpan(start, count);
        }

        /// <summary>将当前已构建好的几何数据一键烘焙为常驻 GPU 的 <see cref="StaticMesh"/>。</summary>
        public StaticMesh BakeToStatic()
        {
            return new StaticMesh(GetVertices(), GetIndices(), PrimitiveType);
        }

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

        public ReadOnlySpan<UnifiedVertex> GetVertices() => _vertices.AsSpan(0, _vertexCount);
        public ReadOnlySpan<int> GetIndices() => _indices.AsSpan(0, _indexCount);

        public void Dispose() { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureVertexCapacity(int required)
        {
            if (required <= _vertices.Length)
                return;

            int newCap = Math.Max(required, _vertices.Length == 0 ? 64 : _vertices.Length * 2);
            Array.Resize(ref _vertices, newCap);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureIndexCapacity(int required)
        {
            if (required <= _indices.Length)
                return;

            int newCap = Math.Max(required, _indices.Length == 0 ? 192 : _indices.Length * 2);
            Array.Resize(ref _indices, newCap);
        }
    }
}
