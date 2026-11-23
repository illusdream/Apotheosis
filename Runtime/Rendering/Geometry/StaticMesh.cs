#nullable enable

using System;
using Apotheosis.Runtime.Foundation.Transform;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Geometry
{
    /// <summary>静态常驻 GPU 网格。</summary>
    public sealed class StaticMesh : IStaticGpuMesh
    {
        private readonly UnifiedVertex[] _vertices;
        private readonly int[] _indices;
        private VertexBuffer? _vertexBuffer;
        private IndexBuffer? _indexBuffer;
        private bool _disposed;

        /// <summary>可选绑定的 3D 变换节点句柄。</summary>
        public TransformHandle TransformNodeHandle { get; set; }

        /// <summary>可选的本地静态世界变换。</summary>
        public Transform3D LocalTransform { get; set; } = Transform3D.Identity;

        /// <summary>是否启用 <see cref="LocalTransform"/>。</summary>
        public bool UseLocalTransform { get; set; }

        public PrimitiveType PrimitiveType { get; }
        public bool IsEmpty => _vertices.Length == 0 || _indices.Length == 0;
        public bool IsStatic => true;

        public VertexBuffer? VertexBuffer => _vertexBuffer;
        public IndexBuffer? IndexBuffer => _indexBuffer;
        public int VertexCount => _vertices.Length;
        public int IndexCount => _indices.Length;

        public StaticMesh(ReadOnlySpan<UnifiedVertex> vertices, ReadOnlySpan<int> indices, PrimitiveType primitiveType = PrimitiveType.TriangleList)
        {
            _vertices = vertices.ToArray();
            _indices = indices.ToArray();
            PrimitiveType = primitiveType;
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

        public ReadOnlySpan<UnifiedVertex> GetVertices() => _vertices;
        public ReadOnlySpan<int> GetIndices() => _indices;

        public bool EnsureGpuReady(GraphicsDevice graphicsDevice)
        {
            if (_disposed || IsEmpty || graphicsDevice.IsDisposed)
                return false;

            if (_vertexBuffer is null || _vertexBuffer.IsDisposed)
            {
                _vertexBuffer = new VertexBuffer(graphicsDevice, UnifiedVertex.Declaration, _vertices.Length, BufferUsage.WriteOnly);
                _vertexBuffer.SetData(_vertices);
            }

            if (_indexBuffer is null || _indexBuffer.IsDisposed)
            {
                _indexBuffer = new IndexBuffer(graphicsDevice, IndexElementSize.ThirtyTwoBits, _indices.Length, BufferUsage.WriteOnly);
                _indexBuffer.SetData(_indices);
            }

            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _vertexBuffer?.Dispose();
            _vertexBuffer = null;
            _indexBuffer?.Dispose();
            _indexBuffer = null;
        }
    }

    /// <summary>
    /// 极薄静态网格实例包装：多个实体共享同一份 <see cref="StaticMesh"/> 的 GPU 缓冲（零顶点复制），
    /// 各自独立绑定 <see cref="TransformHandle"/> 或 <see cref="Transform3D"/> 驱动世界变换。
    /// </summary>
    public sealed class StaticMeshInstance : IStaticGpuMesh
    {
        private readonly StaticMesh _sharedMesh;

        public TransformHandle TransformNodeHandle { get; set; }
        public Transform3D Transform { get; set; } = Transform3D.Identity;
        public bool UseTransform { get; set; } = true;

        public StaticMeshInstance(StaticMesh sharedMesh, TransformHandle transformNodeHandle = default)
        {
            _sharedMesh = sharedMesh ?? throw new ArgumentNullException(nameof(sharedMesh));
            TransformNodeHandle = transformNodeHandle;
        }

        public PrimitiveType PrimitiveType => _sharedMesh.PrimitiveType;
        public bool IsEmpty => _sharedMesh.IsEmpty;
        public bool IsStatic => true;

        public VertexBuffer? VertexBuffer => _sharedMesh.VertexBuffer;
        public IndexBuffer? IndexBuffer => _sharedMesh.IndexBuffer;
        public int VertexCount => _sharedMesh.VertexCount;
        public int IndexCount => _sharedMesh.IndexCount;

        public bool TryGetWorldMatrix(out Matrix worldMatrix)
        {
            if (TransformNodeHandle.TryGetNode(out TransformNode? node))
            {
                worldMatrix = node.WorldMatrix;
                return true;
            }

            if (UseTransform)
            {
                worldMatrix = Transform.ToMatrix();
                return true;
            }

            worldMatrix = default;
            return false;
        }

        public ReadOnlySpan<UnifiedVertex> GetVertices() => _sharedMesh.GetVertices();
        public ReadOnlySpan<int> GetIndices() => _sharedMesh.GetIndices();
        public bool EnsureGpuReady(GraphicsDevice graphicsDevice) => _sharedMesh.EnsureGpuReady(graphicsDevice);

        // 仅包装共享几何，不释放底层共享 StaticMesh 的 GPU 缓冲
        public void Dispose() { }
    }
}
