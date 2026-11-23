#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apotheosis.Runtime.Rendering.Geometry;
using Apotheosis.Runtime.Rendering.Materials;
using Apotheosis.Runtime.Rendering.Targets;

namespace Apotheosis.Runtime.Rendering.Commands
{
    /// <summary>8 字节紧凑渲染命令（即 64 位排序键）。</summary>
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public readonly struct RenderCommand : IComparable<RenderCommand>, IEquatable<RenderCommand>
    {
        // 位布局：[TargetId(8b) | SubLayer(8b) | SortOrder(16b) | MaterialId(16b) | MeshId(16b)]
        private const int TargetShift = 56;
        private const int SubLayerShift = 48;
        private const int SortOrderShift = 32;
        private const int MaterialShift = 16;

        /// <summary>合批判定掩码（仅保留 TargetId 与 MaterialId 位）。</summary>
        public const ulong BatchKeyMask = 0xFF00_0000_FFFF_0000UL;

        public readonly ulong Key;

        public RenderCommand(ulong key) => Key = key;

        public RenderTargetHandle Target => new((byte)(Key >> TargetShift));
        public byte SubLayer => (byte)(Key >> SubLayerShift);
        public short SortOrder => (short)(((ushort)(Key >> SortOrderShift)) ^ 0x8000);
        public MaterialHandle Material => new((ushort)(Key >> MaterialShift));
        public MeshHandle Mesh => new((ushort)Key);

        public ulong BatchKey => Key & BatchKeyMask;

        /// <summary>打包一条 8 字节渲染命令。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RenderCommand Pack(RenderTargetHandle target, byte subLayer, short sortOrder, MaterialHandle material, MeshHandle mesh)
        {
            // 有符号 short 异或 0x8000 映射为无符号升序，保证负 SortOrder 排在正值之前
            ulong biasedOrder = (ushort)(sortOrder ^ 0x8000);
            ulong key = ((ulong)target.Id << TargetShift)
                      | ((ulong)subLayer << SubLayerShift)
                      | (biasedOrder << SortOrderShift)
                      | ((ulong)material.Id << MaterialShift)
                      | mesh.Id;
            return new RenderCommand(key);
        }

        /// <summary>打包 2D 精灵内部合批桶键（保留高 48 位，低 16 位归零）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong PackSpriteBucketKey(RenderTargetHandle target, byte subLayer, short sortOrder, MaterialHandle material)
        {
            ulong biasedOrder = (ushort)(sortOrder ^ 0x8000);
            return ((ulong)target.Id << TargetShift)
                 | ((ulong)subLayer << SubLayerShift)
                 | (biasedOrder << SortOrderShift)
                 | ((ulong)material.Id << MaterialShift);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareTo(RenderCommand other) => Key.CompareTo(other.Key);

        public bool Equals(RenderCommand other) => Key == other.Key;
        public override bool Equals(object? obj) => obj is RenderCommand other && Key == other.Key;
        public override int GetHashCode() => Key.GetHashCode();
        public static bool operator ==(RenderCommand left, RenderCommand right) => left.Key == right.Key;
        public static bool operator !=(RenderCommand left, RenderCommand right) => left.Key != right.Key;
        public override string ToString()
            => $"RenderCommand(0x{Key:X16} | T={Target.Id} L={SubLayer} O={SortOrder} Mat={Material.Id} Mesh={Mesh.Id})";
    }
}
