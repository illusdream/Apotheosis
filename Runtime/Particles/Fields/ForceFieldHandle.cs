#nullable enable

using System;
using Apotheosis.Runtime.Particles.Core;

namespace Apotheosis.Runtime.Particles.Fields
{
    /// <summary>粒子力场句柄（统一标识全局世界力场与子系统局部力场，零堆分配）。</summary>
    public readonly struct ForceFieldHandle : IEquatable<ForceFieldHandle>
    {
        public static readonly ForceFieldHandle Invalid = default;

        /// <summary>所属子系统句柄（为 <see cref="ParticleSystemHandle.Invalid"/> 时表示全局世界力场）。</summary>
        public readonly ParticleSystemHandle SystemHandle;

        /// <summary>唯一力场编号（0 表示无效）。</summary>
        public readonly uint FieldId;

        public bool IsValid => FieldId != 0;
        public bool IsGlobal => FieldId != 0 && !SystemHandle.IsValid;
        public bool IsInstanceLocal => FieldId != 0 && SystemHandle.IsValid;

        internal ForceFieldHandle(ParticleSystemHandle systemHandle, uint fieldId)
        {
            SystemHandle = systemHandle;
            FieldId = fieldId;
        }

        public bool Equals(ForceFieldHandle other) => SystemHandle == other.SystemHandle && FieldId == other.FieldId;
        public override bool Equals(object? obj) => obj is ForceFieldHandle other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(SystemHandle, FieldId);
        public static bool operator ==(ForceFieldHandle left, ForceFieldHandle right) => left.Equals(right);
        public static bool operator !=(ForceFieldHandle left, ForceFieldHandle right) => !left.Equals(right);
        public override string ToString() => !IsValid
            ? "ForceFieldHandle(Invalid)"
            : (IsGlobal ? $"ForceFieldHandle(Global, #{FieldId})" : $"ForceFieldHandle({SystemHandle}, #{FieldId})");
    }
}
