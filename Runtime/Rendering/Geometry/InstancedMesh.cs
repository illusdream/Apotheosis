#nullable enable

using System;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Geometry
{
    /// <summary>单基础网格的硬件实例化容器。</summary>
    public sealed class InstancedMesh : IInstancedMesh
    {
        private InstanceData[] _instances;
        private int _count;

        public MeshHandle BaseMesh { get; set; }
        public int Count => _count;

        public PrimitiveType PrimitiveType => PrimitiveType.TriangleList;
        public bool IsEmpty => _count == 0 || !BaseMesh.IsValid;
        public bool IsStatic => false;
        public int GroupCount => 1;

        public InstancedMesh(MeshHandle baseMesh, int initialCapacity = 128)
        {
            BaseMesh = baseMesh;
            _instances = initialCapacity > 0 ? new InstanceData[initialCapacity] : Array.Empty<InstanceData>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear() => _count = 0;

        internal void ResetForPool(MeshHandle baseMesh)
        {
            BaseMesh = baseMesh;
            _count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(in InstanceData instance)
        {
            if (_count >= _instances.Length)
            {
                int newCap = _instances.Length == 0 ? 128 : _instances.Length * 2;
                Array.Resize(ref _instances, newCap);
            }

            _instances[_count++] = instance;
        }

        public void AddRange(ReadOnlySpan<InstanceData> items)
        {
            if (items.IsEmpty)
                return;

            int required = _count + items.Length;
            if (required > _instances.Length)
            {
                int newCap = Math.Max(required, _instances.Length == 0 ? 128 : _instances.Length * 2);
                Array.Resize(ref _instances, newCap);
            }

            items.CopyTo(_instances.AsSpan(_count));
            _count = required;
        }

        public MeshHandle GetGroupBaseMesh(int groupIndex) => BaseMesh;
        public ReadOnlySpan<InstanceData> GetGroupInstances(int groupIndex) => _instances.AsSpan(0, _count);

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
            public InstanceData[] Items;
            public int Count;
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
                    if (_groups[i].Count > 0 && _groups[i].BaseMesh.IsValid)
                        return false;
                }

                return true;
            }
        }

        public MultiGroupInstancedMesh(params MeshHandle[] baseMeshes)
            : this(64, baseMeshes)
        {
        }

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
                    Items = initialCapacityPerGroup > 0 ? new InstanceData[initialCapacityPerGroup] : Array.Empty<InstanceData>(),
                    Count = 0,
                };
            }
        }

        public void SetGroupBaseMesh(int groupIndex, MeshHandle baseMesh)
            => _groups[groupIndex].BaseMesh = baseMesh;

        public int GetGroupCount(int groupIndex) => _groups[groupIndex].Count;

        public void ClearAll()
        {
            for (int i = 0; i < _groups.Length; i++)
                _groups[i].Count = 0;
        }

        public void ClearGroup(int groupIndex) => _groups[groupIndex].Count = 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(int groupIndex, in InstanceData instance)
        {
            ref GroupBucket group = ref _groups[groupIndex];
            if (group.Count >= group.Items.Length)
            {
                int newCap = group.Items.Length == 0 ? 64 : group.Items.Length * 2;
                Array.Resize(ref group.Items, newCap);
            }

            group.Items[group.Count++] = instance;
        }

        public MeshHandle GetGroupBaseMesh(int groupIndex) => _groups[groupIndex].BaseMesh;

        public ReadOnlySpan<InstanceData> GetGroupInstances(int groupIndex)
        {
            ref GroupBucket group = ref _groups[groupIndex];
            return group.Items.AsSpan(0, group.Count);
        }

        public ReadOnlySpan<UnifiedVertex> GetVertices() => ReadOnlySpan<UnifiedVertex>.Empty;
        public ReadOnlySpan<int> GetIndices() => ReadOnlySpan<int>.Empty;
        public void Dispose() { }
    }
}
