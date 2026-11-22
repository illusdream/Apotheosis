#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apotheosis.Runtime.Foundation;

namespace Apotheosis.Runtime.Rendering.Shaders
{
    /// <summary>
    /// 2 字节零分配着色器句柄（内部仅封装单个 <see cref="ushort"/>）。
    /// <para>
    /// 位布局：低 8 位为槽位索引 <see cref="SlotIndex"/>（1..255，0 表示无效），
    /// 高 8 位为代际号 <see cref="Generation"/>（1..255，用于模组卸载/重载后自动识别过期句柄）。
    /// 可与 <c>TechniqueIndex</c>/<c>PassIndex</c> 紧凑对齐进后续合批的 <c>RenderCommand</c>，且仅占用 8 位 <c>SortKey</c>。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 2)]
    public readonly struct ShaderHandle : IEquatable<ShaderHandle>
    {
        private const int GenerationShift = 8;
        private const int SlotIndexMask = 0xFF;

        private readonly ushort _value;

        /// <summary>空/无效着色器句柄。</summary>
        public static ShaderHandle Invalid => default;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ShaderHandle(byte slotIndex, byte generation)
        {
            _value = (ushort)((generation << GenerationShift) | slotIndex);
        }

        /// <summary>原始 16 位打包值（0 表示无效）。</summary>
        public ushort RawValue
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value;
        }

        /// <summary>槽位索引（低 8 位，1..255；可直接作为后续 <c>SortKey</c> 的 8 位 ShaderID 与数组下标）。</summary>
        public byte SlotIndex
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (byte)(_value & SlotIndexMask);
        }

        /// <summary>代际版本号（高 8 位，1..255）。</summary>
        public byte Generation
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (byte)(_value >> GenerationShift);
        }

        /// <summary>当前句柄在 <see cref="RenderManager.Shaders"/> 中是否有效。</summary>
        public bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value != RuntimeConstants.IndexNone && RenderManager.Shaders.IsValid(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(ShaderHandle other) => _value == other._value;

        public override bool Equals(object? obj) => obj is ShaderHandle other && Equals(other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() => _value;

        public override string ToString()
            => _value == RuntimeConstants.IndexNone ? "ShaderHandle(Invalid)" : $"ShaderHandle(Slot={SlotIndex}, Gen={Generation})";

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(ShaderHandle left, ShaderHandle right) => left._value == right._value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(ShaderHandle left, ShaderHandle right) => left._value != right._value;
    }
}
