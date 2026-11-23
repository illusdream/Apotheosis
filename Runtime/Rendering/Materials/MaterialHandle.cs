#nullable enable

using System;
using System.Runtime.InteropServices;

namespace Apotheosis.Runtime.Rendering.Materials
{
    /// <summary>2 字节材质句柄（<c>0</c> 为无效）。</summary>
    [StructLayout(LayoutKind.Sequential, Size = 2)]
    public readonly struct MaterialHandle : IEquatable<MaterialHandle>
    {
        public static readonly MaterialHandle Invalid = default;

        public readonly ushort Id;

        public MaterialHandle(ushort id) => Id = id;

        public bool IsValid => Id != 0;

        public bool Equals(MaterialHandle other) => Id == other.Id;
        public override bool Equals(object? obj) => obj is MaterialHandle other && Equals(other);
        public override int GetHashCode() => Id;
        public static bool operator ==(MaterialHandle left, MaterialHandle right) => left.Equals(right);
        public static bool operator !=(MaterialHandle left, MaterialHandle right) => !left.Equals(right);
        public override string ToString() => IsValid ? $"MaterialHandle({Id})" : "MaterialHandle(Invalid)";
    }
}
