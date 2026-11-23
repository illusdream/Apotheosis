#nullable enable

using System;
using System.Runtime.InteropServices;

namespace Apotheosis.Runtime.Rendering.Geometry
{
    /// <summary>2 字节统一网格句柄（<c>0</c> 为无效）。</summary>
    [StructLayout(LayoutKind.Sequential, Size = 2)]
    public readonly struct MeshHandle : IEquatable<MeshHandle>
    {
        public static readonly MeshHandle Invalid = default;

        public readonly ushort Id;

        public MeshHandle(ushort id) => Id = id;

        public bool IsValid => Id != 0;

        public bool Equals(MeshHandle other) => Id == other.Id;
        public override bool Equals(object? obj) => obj is MeshHandle other && Equals(other);
        public override int GetHashCode() => Id;
        public static bool operator ==(MeshHandle left, MeshHandle right) => left.Equals(right);
        public static bool operator !=(MeshHandle left, MeshHandle right) => !left.Equals(right);
        public override string ToString() => IsValid ? $"MeshHandle({Id})" : "MeshHandle(Invalid)";
    }
}
