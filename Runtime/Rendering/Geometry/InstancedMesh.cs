#nullable enable

using System;
using System.Runtime.CompilerServices;
using Apotheosis.Runtime.Foundation.Collections;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Geometry
{
    /// <summary>单基础网格的硬件实例化容器。</summary>
    public sealed class InstancedMesh : IInstancedMesh
    {
        private ValueBuffer<InstanceData> _instances;

        public MeshHandle BaseMesh { get; set; }
        public int Count => _instances.Count;

        public PrimitiveType PrimitiveType => PrimitiveType.TriangleList;
        public bool IsEmpty => _instances.Count == 0 || !BaseMesh.IsValid;
        public bool IsStatic => false;
        public int GroupCount => 1;

        public InstancedMesh(MeshHandle baseMesh, int initialCapacity = 128)
        {
            BaseMesh = baseMesh;
            _instances = new ValueBuffer<InstanceData>(initialCapacity, defaultGrowCapacity: 128);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear() => _instances.Clear(clearReferences: false);

        internal void ResetForPool(MeshHandle baseMesh)
        {
            BaseMesh = baseMesh;
            Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(in InstanceData instance) => _instances.Add(in instance);

        public void AddRange(ReadOnlySpan<InstanceData> items) => _instances.AddRange(items);

        public MeshHandle GetGroupBaseMesh(int groupIndex) => BaseMesh;
        public ReadOnlySpan<InstanceData> GetGroupInstances(int groupIndex) => _instances.AsReadOnlySpan();

        public ReadOnlySpan<UnifiedVertex> GetVertices() => ReadOnlySpan<UnifiedVertex>.Empty;
        public ReadOnlySpan<int> GetIndices() => ReadOnlySpan<int>.Empty;
        public void Dispose() { }
    }

    /// <summary>多基础网格分组的硬件实例化容器（支持多 LOD 或多元件复用）。</summary>
    public sealed class MultiGroupInstancedMesh : IInstancedMesh
    {
        private struct GroupBucket
        {
            public MeshHandle BaseMesh;
            public ValueBuffer<InstanceData> Items;
        }

        private readonly GroupBucket[] _groups;

        public PrimitiveType PrimitiveType => PrimitiveType.TriangleList;
        public bool IsStatic => false;
        public int GroupCount => _groups.Length;

        public bool IsEmpty
        {
            get
            {
                for (int i = 0; i < _groups.Length; i++)
                {
                    if (_groups[i].Items.Count > 0 && _groups[i].BaseMesh.IsValid)
                        return false;
                }

                return true;
            }
        }

        public MultiGroupInstancedMesh(params MeshHandle[] baseMeshes) : this(64, baseMeshes) { }

        public MultiGroupInstancedMesh(int initialCapacityPerGroup, params MeshHandle[] baseMeshes)
        {
            if (baseMeshes is null || baseMeshes.Length == 0)
                throw new ArgumentException("至少需要指定一个基础网格句柄。", nameof(baseMeshes));

            _groups = new GroupBucket[baseMeshes.Length];
            for (int i = 0; i < baseMeshes.Length; i++)
            {
                _groups[i] = new GroupBucket
                {
                    BaseMesh = baseMeshes[i],
                    Items = new ValueBuffer<InstanceData>(initialCapacityPerGroup, defaultGrowCapacity: 64),
                };
            }
        }

        public void SetGroupBaseMesh(int groupIndex, MeshHandle baseMesh) => _groups[groupIndex].BaseMesh = baseMesh;

        public int GetGroupCount(int groupIndex) => _groups[groupIndex].Items.Count;

        public void ClearAll()
        {
            for (int i = 0; i < _groups.Length; i++)
                _groups[i].Items.Clear(clearReferences: false);
        }

        public void ClearGroup(int groupIndex) => _groups[groupIndex].Items.Clear(clearReferences: false);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(int groupIndex, in InstanceData instance) => _groups[groupIndex].Items.Add(in instance);

        public MeshHandle GetGroupBaseMesh(int groupIndex) => _groups[groupIndex].BaseMesh;

        public ReadOnlySpan<InstanceData> GetGroupInstances(int groupIndex) => _groups[groupIndex].Items.AsReadOnlySpan();

        public ReadOnlySpan<UnifiedVertex> GetVertices() => ReadOnlySpan<UnifiedVertex>.Empty;
        public ReadOnlySpan<int> GetIndices() => ReadOnlySpan<int>.Empty;
        public void Dispose() { }
    }
}
