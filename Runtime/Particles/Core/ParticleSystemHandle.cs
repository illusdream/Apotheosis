#nullable enable

using System;
using System.Runtime.InteropServices;
using Apotheosis.Runtime.Foundation;

namespace Apotheosis.Runtime.Particles.Core
{
    /// <summary>8 字节代际安全的小型独立粒子系统句柄（<c>SlotId == 0</c> 为无效）。</summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public readonly struct ParticleSystemHandle : IEquatable<ParticleSystemHandle>
    {
        public static readonly ParticleSystemHandle Invalid = default;

        public readonly ushort SlotId;
        private readonly ushort _reserved;
        public readonly uint Generation;

        public ParticleSystemHandle(ushort slotId, uint generation)
        {
            SlotId = slotId;
            _reserved = 0;
            Generation = generation;
        }

        public bool IsValid => SlotId != RuntimeConstants.IndexNone && Generation != RuntimeConstants.IndexNone;

        public bool Equals(ParticleSystemHandle other) => SlotId == other.SlotId && Generation == other.Generation;
        public override bool Equals(object? obj) => obj is ParticleSystemHandle other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(SlotId, Generation);
        public static bool operator ==(ParticleSystemHandle left, ParticleSystemHandle right) => left.Equals(right);
        public static bool operator !=(ParticleSystemHandle left, ParticleSystemHandle right) => !left.Equals(right);
        public override string ToString() => IsValid ? $"ParticleSystemHandle({SlotId}#{Generation})" : "ParticleSystemHandle(Invalid)";
    }
}
