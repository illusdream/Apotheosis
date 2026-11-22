using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Apotheosis.ShaderTools;

internal sealed class ShaderParameterMeta
{
    public string Name { get; set; } = string.Empty;
    public string Semantic { get; set; } = string.Empty;
    public uint ParamType { get; set; }
    public uint ParamClass { get; set; }
    public uint Rows { get; set; }
    public uint Columns { get; set; }
    public uint Elements { get; set; }
}

internal sealed class ShaderTechniqueMeta
{
    public string Name { get; set; } = string.Empty;
    public List<string> Passes { get; set; } = new();
}

internal sealed class ShaderEffectMeta
{
    public string ModAssetPath { get; set; } = string.Empty;
    public string RelativeFilePath { get; set; } = string.Empty;
    public bool HasNonSm3Shader { get; set; }
    public List<string> ShaderModels { get; set; } = new();
    public List<ShaderParameterMeta> Parameters { get; set; } = new();
    public List<ShaderTechniqueMeta> Techniques { get; set; } = new();
}

/// <summary>
/// 解析 XNA 4.0 未压缩 Effect <c>.xnb</c> 文件以及内部 Direct3D 9 <c>fx_2_0</c>（魔数 <c>0xFEFF0901</c>）字节码，
/// 提取经 HLSL 编译器优化后的真实参数表、Technique/Pass 列表与着色器模型版本。
/// </summary>
internal static class XnbEffectParser
{
    public static ShaderEffectMeta ParseXnbFile(string xnbPath, string modAssetPath, string relativeFilePath)
    {
        byte[] xnbBytes = File.ReadAllBytes(xnbPath);
        byte[] fxBytecode = ExtractEffectBytecodeFromXnb(xnbBytes, xnbPath);
        return ParseFx20Bytecode(fxBytecode, modAssetPath, relativeFilePath, xnbPath);
    }

    public static byte[] ExtractEffectBytecodeFromXnb(byte[] xnbBytes, string sourceLabel)
    {
        if (xnbBytes.Length < XnbFxFormatConstants.XnbMinValidFileSize
            || xnbBytes[0] != (byte)'X'
            || xnbBytes[1] != (byte)'N'
            || xnbBytes[2] != (byte)'B')
        {
            throw new InvalidDataException($"{sourceLabel}: 不是有效的 XNB 文件（缺少 XNB 魔数头）。");
        }

        byte version = xnbBytes[4];
        if (version != XnbFxFormatConstants.Xna4FormatVersion)
        {
            throw new InvalidDataException(
                $"{sourceLabel}: 仅支持 XNA 4.0 (version={XnbFxFormatConstants.Xna4FormatVersion}) XNB，当前 version={version}。");
        }

        byte flags = xnbBytes[5];
        if ((flags & (XnbFxFormatConstants.XnbFlagCompressedLzx | XnbFxFormatConstants.XnbFlagCompressedLz4)) != 0)
        {
            throw new InvalidDataException(
                $"{sourceLabel}: 暂不支持压缩的 XNB 文件 (flags=0x{flags:X2})；请使用未压缩 XNB 或通过内置 .fx 自动编译产出。");
        }

        using var ms = new MemoryStream(
            xnbBytes,
            XnbFxFormatConstants.XnbHeaderSize,
            xnbBytes.Length - XnbFxFormatConstants.XnbHeaderSize,
            writable: false);
        using var reader = new BinaryReader(ms, Encoding.UTF8);

        int readerCount = Read7BitEncodedInt(reader);
        if (readerCount <= 0)
            throw new InvalidDataException($"{sourceLabel}: XNB 缺少 TypeReader 清单。");

        string[] readerNames = new string[readerCount];
        for (int i = 0; i < readerCount; i++)
        {
            readerNames[i] = reader.ReadString();
            _ = reader.ReadInt32(); // reader version
        }

        int sharedResourceCount = Read7BitEncodedInt(reader);
        if (sharedResourceCount != 0)
        {
            throw new InvalidDataException($"{sourceLabel}: Effect XNB 不应包含共享资源（count={sharedResourceCount}）。");
        }

        int primaryReaderIndex = Read7BitEncodedInt(reader);
        if (primaryReaderIndex <= 0 || primaryReaderIndex > readerCount)
        {
            throw new InvalidDataException($"{sourceLabel}: 无效的主资源 Reader 索引 {primaryReaderIndex}。");
        }

        string primaryReaderName = readerNames[primaryReaderIndex - 1];
        if (!primaryReaderName.Contains("EffectReader", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{sourceLabel}: 该 XNB 不是 Effect 资源（Reader={primaryReaderName}）。");
        }

        int bytecodeLength = reader.ReadInt32();
        if (bytecodeLength <= 0 || bytecodeLength > ms.Length - ms.Position)
        {
            throw new InvalidDataException($"{sourceLabel}: Effect 字节码长度无效 ({bytecodeLength})。");
        }

        return reader.ReadBytes(bytecodeLength);
    }

    public static ShaderEffectMeta ParseFx20Bytecode(
        byte[] data,
        string modAssetPath,
        string relativeFilePath,
        string sourceLabel)
    {
        if (data.Length < XnbFxFormatConstants.Fx20MinBytecodeLength)
            throw new InvalidDataException($"{sourceLabel}: fx_2_0 字节码过短 ({data.Length} bytes)。");

        uint magic = BitConverter.ToUInt32(data, 0);
        if (magic != XnbFxFormatConstants.Fx20Magic)
        {
            throw new InvalidDataException(
                $"{sourceLabel}: 无效的 fx_2_0 魔数 0x{magic:X8}（期望 0x{XnbFxFormatConstants.Fx20Magic:X8}）。");
        }

        const int heapBase = XnbFxFormatConstants.Fx20HeapBaseOffset;
        uint bodyRelOffset = BitConverter.ToUInt32(data, 4);
        int pos = checked(heapBase + (int)bodyRelOffset);

        uint paramCount = ReadUInt32(data, ref pos);
        uint techCount = ReadUInt32(data, ref pos);
        _ = ReadUInt32(data, ref pos); // unknown / reserved
        _ = ReadUInt32(data, ref pos); // objectCount

        var meta = new ShaderEffectMeta
        {
            ModAssetPath = modAssetPath,
            RelativeFilePath = relativeFilePath,
        };

        for (int i = 0; i < paramCount; i++)
        {
            uint typeOffset = ReadUInt32(data, ref pos);
            _ = ReadUInt32(data, ref pos); // valueOffset
            _ = ReadUInt32(data, ref pos); // flags
            uint annCount = ReadUInt32(data, ref pos);
            pos = checked(pos + (int)annCount * 8);

            var param = ReadParameterType(data, heapBase, typeOffset);
            meta.Parameters.Add(param);
        }

        for (int t = 0; t < techCount; t++)
        {
            uint techNameOffset = ReadUInt32(data, ref pos);
            uint techAnnCount = ReadUInt32(data, ref pos);
            uint passCount = ReadUInt32(data, ref pos);
            pos = checked(pos + (int)techAnnCount * 8);

            var tech = new ShaderTechniqueMeta
            {
                Name = ReadFxString(data, heapBase, techNameOffset),
            };

            for (int p = 0; p < passCount; p++)
            {
                uint passNameOffset = ReadUInt32(data, ref pos);
                uint passAnnCount = ReadUInt32(data, ref pos);
                uint stateCount = ReadUInt32(data, ref pos);
                pos = checked(pos + (int)passAnnCount * 8);
                pos = checked(pos + (int)stateCount * 16);

                tech.Passes.Add(ReadFxString(data, heapBase, passNameOffset));
            }

            meta.Techniques.Add(tech);
        }

        // 扫描后续对象表以识别编译产出的 Shader Model 版本（vs_3_0 = 0xFFFE0300, ps_3_0 = 0xFFFF0300）
        ScanShaderModels(data, pos, meta);

        return meta;
    }

    private static ShaderParameterMeta ReadParameterType(byte[] data, int heapBase, uint typeRelOffset)
    {
        int tp = checked(heapBase + (int)typeRelOffset);
        uint paramType = ReadUInt32(data, ref tp);
        uint paramClass = ReadUInt32(data, ref tp);
        uint nameOffset = ReadUInt32(data, ref tp);
        uint semanticOffset = ReadUInt32(data, ref tp);
        uint elements = ReadUInt32(data, ref tp);

        uint columns = 0;
        uint rows = 0;
        var paramClassEnum = (D3dxParameterClass)paramClass;
        if (paramClassEnum is D3dxParameterClass.Scalar
            or D3dxParameterClass.Vector
            or D3dxParameterClass.MatrixRows
            or D3dxParameterClass.MatrixColumns)
        {
            columns = ReadUInt32(data, ref tp);
            rows = ReadUInt32(data, ref tp);
        }

        return new ShaderParameterMeta
        {
            Name = ReadFxString(data, heapBase, nameOffset),
            Semantic = ReadFxString(data, heapBase, semanticOffset),
            ParamType = paramType,
            ParamClass = paramClass,
            Rows = rows,
            Columns = columns,
            Elements = elements,
        };
    }

    private static void ScanShaderModels(byte[] data, int pos, ShaderEffectMeta meta)
    {
        if (pos + 8 > data.Length)
            return;

        // 紧跟 Techniques 之后是 smallObjectsCount 与 largeObjectsCount
        uint smallObjects = ReadUInt32(data, ref pos);
        uint largeObjects = ReadUInt32(data, ref pos);

        var seenModels = new HashSet<string>(StringComparer.Ordinal);

        // 先跳过 small objects（字符串/默认对象）
        for (uint i = 0; i < smallObjects; i++)
        {
            if (pos + 8 > data.Length)
                return;

            _ = ReadUInt32(data, ref pos); // objectIndex
            uint byteLen = ReadUInt32(data, ref pos);
            if (byteLen > int.MaxValue - 3)
                return;

            uint paddedLen = (byteLen + 3u) & ~3u;
            if (paddedLen > (uint)(data.Length - pos))
                return;

            TryRecordShaderToken(data, pos, byteLen, seenModels, meta);
            pos += (int)paddedLen;
        }

        // 再扫描 large objects（编译后的 VS/PS 字节码对象）
        for (uint i = 0; i < largeObjects; i++)
        {
            if (pos + 24 > data.Length)
                return;

            _ = ReadUInt32(data, ref pos); // techniqueIndex
            _ = ReadUInt32(data, ref pos); // passIndex
            _ = ReadUInt32(data, ref pos); // stateIndex
            _ = ReadUInt32(data, ref pos); // usage
            _ = ReadUInt32(data, ref pos); // unk
            uint byteLen = ReadUInt32(data, ref pos);
            if (byteLen > int.MaxValue - 3)
                return;

            uint paddedLen = (byteLen + 3u) & ~3u;
            if (paddedLen > (uint)(data.Length - pos))
                return;

            TryRecordShaderToken(data, pos, byteLen, seenModels, meta);
            pos += (int)paddedLen;
        }
    }

    private static void TryRecordShaderToken(
        byte[] data,
        int offset,
        uint byteLen,
        HashSet<string> seenModels,
        ShaderEffectMeta meta)
    {
        if (byteLen < 4 || offset + 4 > data.Length)
            return;

        uint token = BitConverter.ToUInt32(data, offset);
        uint prefix = token & XnbFxFormatConstants.ShaderStageMask;
        if (prefix != XnbFxFormatConstants.VertexShaderVersionPrefix
            && prefix != XnbFxFormatConstants.PixelShaderVersionPrefix)
            return;

        uint major = (token >> 8) & 0xFFu;
        uint minor = token & 0xFFu;
        if (major is < 1 or > 3)
            return;

        string stage = prefix == XnbFxFormatConstants.VertexShaderVersionPrefix ? "vs" : "ps";
        string model = $"{stage}_{major}_{minor}";

        if (seenModels.Add(model))
            meta.ShaderModels.Add(model);

        if (major != XnbFxFormatConstants.TargetShaderModelMajor)
            meta.HasNonSm3Shader = true;
    }

    private static string ReadFxString(byte[] data, int heapBase, uint relOffset)
    {
        if (relOffset == 0)
            return string.Empty;

        int pos = checked(heapBase + (int)relOffset);
        if (pos < 0 || pos > data.Length - 4)
            return string.Empty;

        int len = BitConverter.ToInt32(data, pos);
        if (len <= 0 || len > data.Length - pos - 4)
            return string.Empty;

        int actualLen = len;
        while (actualLen > 0 && data[pos + 4 + actualLen - 1] == 0)
            actualLen--;

        return Encoding.ASCII.GetString(data, pos + 4, actualLen);
    }

    private static uint ReadUInt32(byte[] data, ref int pos)
    {
        if (pos + 4 > data.Length)
            throw new EndOfStreamException("fx_2_0 字节码解析越界。");

        uint value = BitConverter.ToUInt32(data, pos);
        pos += 4;
        return value;
    }

    private static int Read7BitEncodedInt(BinaryReader reader)
    {
        int result = 0;
        int shift = 0;
        while (true)
        {
            byte b = reader.ReadByte();
            result |= (b & XnbFxFormatConstants.SevenBitPayloadMask) << shift;
            if ((b & XnbFxFormatConstants.SevenBitContinuationMask) == 0)
                return result;
            shift += XnbFxFormatConstants.SevenBitShiftStep;
            if (shift >= XnbFxFormatConstants.SevenBitMaxShift)
                throw new FormatException("无效的 7-bit 编码整数。");
        }
    }
}
