namespace Apotheosis.ShaderTools;

/// <summary>
/// Direct3D 9 <c>D3DXPARAMETER_TYPE</c> 协议枚举。
/// </summary>
internal enum D3dxParameterType : uint
{
    Void = 0,
    Bool = 1,
    Int = 2,
    Float = 3,
    String = 4,
    Texture = 5,
    Texture1D = 6,
    Texture2D = 7,
    Texture3D = 8,
    TextureCube = 9,
    Sampler = 10,
    Sampler1D = 11,
    Sampler2D = 12,
    Sampler3D = 13,
    SamplerCube = 14,
    PixelShader = 15,
    VertexShader = 16,
}

/// <summary>
/// Direct3D 9 <c>D3DXPARAMETER_CLASS</c> 协议枚举。
/// </summary>
internal enum D3dxParameterClass : uint
{
    Scalar = 0,
    Vector = 1,
    MatrixRows = 2,
    MatrixColumns = 3,
    Object = 4,
    Struct = 5,
}

/// <summary>
/// XNA 4.0 XNB 与 Direct3D 9 <c>fx_2_0</c> 二进制协议常量。
/// </summary>
internal static class XnbFxFormatConstants
{
    // XNA 4.0 XNB 头格式
    public const int XnbHeaderSize = 10;
    public const int XnbMinValidFileSize = 14;
    public const byte XnbPlatformWindows = (byte)'w';
    public const byte Xna4FormatVersion = 5;
    public const byte XnbFlagUncompressed = 0x00;
    public const byte XnbFlagCompressedLzx = 0x80;
    public const byte XnbFlagCompressedLz4 = 0x40;

    // UTF-8 BOM
    public const byte Utf8BomByte0 = 0xEF;
    public const byte Utf8BomByte1 = 0xBB;
    public const byte Utf8BomByte2 = 0xBF;
    public const int Utf8BomLength = 3;

    // D3D9 fx_2_0 字节码与着色器版本 Token
    public const uint Fx20Magic = 0xFEFF0901u;
    public const int Fx20HeapBaseOffset = 8;
    public const int Fx20MinBytecodeLength = 24;
    public const uint ShaderStageMask = 0xFFFF0000u;
    public const uint VertexShaderVersionPrefix = 0xFFFE0000u;
    public const uint PixelShaderVersionPrefix = 0xFFFF0000u;
    public const uint TargetShaderModelMajor = 3u;

    // 7-bit 变长整数编码
    public const byte SevenBitContinuationMask = 0x80;
    public const byte SevenBitPayloadMask = 0x7F;
    public const int SevenBitShiftStep = 7;
    public const int SevenBitMaxShift = 35;

    // FNV-1a 32-bit 哈希常量
    public const uint Fnv1a32OffsetBasis = 2166136261u;
    public const uint Fnv1a32Prime = 16777619u;
}
