#nullable enable

using System;
using System.Runtime.InteropServices;

namespace Apotheosis.Runtime.Rendering.Targets
{
    /// <summary>
    /// 1 字节紧凑渲染目标句柄（<c>0..255</c>）：直接嵌入 8 字节 <c>RenderCommand</c> 最高字节（Bits 63..56），
    /// 对应 <see cref="RenderTargetModule"/> 中按固定顺序注册的 <see cref="IRenderTarget"/> 槽位。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 1)]
    public readonly struct RenderTargetHandle : IEquatable<RenderTargetHandle>
    {
        public readonly byte Id;

        public RenderTargetHandle(byte id) => Id = id;

        public static implicit operator RenderTargetHandle(BuiltInRenderTarget builtIn) => new((byte)builtIn);

        public bool Equals(RenderTargetHandle other) => Id == other.Id;
        public override bool Equals(object? obj) => obj is RenderTargetHandle other && Equals(other);
        public override int GetHashCode() => Id;
        public static bool operator ==(RenderTargetHandle left, RenderTargetHandle right) => left.Id == right.Id;
        public static bool operator !=(RenderTargetHandle left, RenderTargetHandle right) => left.Id != right.Id;
        public override string ToString() => $"RenderTargetHandle({Id})";
    }
}
