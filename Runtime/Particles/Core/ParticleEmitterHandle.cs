#nullable enable

using System;

namespace Apotheosis.Runtime.Particles.Core
{
    /// <summary>挂载在指定 <see cref="ParticleInstance"/> 中的单个粒子发射器实例句柄（零堆分配，用于独立控制单个发射器的速率倍率、启停与摘除）。</summary>
    public readonly struct ParticleEmitterHandle : IEquatable<ParticleEmitterHandle>
    {
        public static readonly ParticleEmitterHandle Invalid = default;

        /// <summary>所属小型独立粒子子系统的代际安全句柄。</summary>
        public readonly ParticleSystemHandle SystemHandle;

        /// <summary>子系统内唯一的发射器挂载标识（0 表示无效）。</summary>
        public readonly uint EmitterId;

        public bool IsValid => SystemHandle.IsValid && EmitterId != 0;

        internal ParticleEmitterHandle(ParticleSystemHandle systemHandle, uint emitterId)
        {
            SystemHandle = systemHandle;
            EmitterId = emitterId;
        }

        public bool Equals(ParticleEmitterHandle other) => SystemHandle == other.SystemHandle && EmitterId == other.EmitterId;
        public override bool Equals(object? obj) => obj is ParticleEmitterHandle other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(SystemHandle, EmitterId);
        public static bool operator ==(ParticleEmitterHandle left, ParticleEmitterHandle right) => left.Equals(right);
        public static bool operator !=(ParticleEmitterHandle left, ParticleEmitterHandle right) => !left.Equals(right);
        public override string ToString() => IsValid ? $"ParticleEmitterHandle({SystemHandle}, #{EmitterId})" : "ParticleEmitterHandle(Invalid)";
    }
}
