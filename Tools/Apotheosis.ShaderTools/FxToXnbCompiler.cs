using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Apotheosis.ShaderTools;

/// <summary>
/// 调用 Windows 系统内置 <c>d3dcompiler_47.dll</c> 将 HLSL <c>.fx</c> 按 <c>fx_2_0</c>（支持内部 <c>vs_3_0</c>/<c>ps_3_0</c>）编译，
/// 并直接封装为 FNA / XNA 4.0 兼容的未压缩 <c>.xnb</c>（<c>EffectReader</c>）文件。
/// <para>
/// 使用自定义 <c>ID3DInclude</c> 处理器解析相对与 <c>Assets/</c> 根目录的 <c>#include</c>，
/// 同时自动剥离主文件与全部被包含头文件中的 UTF-8 BOM，且绝不修改进程当前工作目录。
/// </para>
/// </summary>
internal static unsafe class FxToXnbCompiler
{
    private const string EffectReaderTypeName =
        "Microsoft.Xna.Framework.Content.EffectReader, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553";

    // D3DCOMPILE_OPTIMIZATION_LEVEL3 (1 << 15)
    private const uint DefaultCompileFlags = 1u << 15;

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(
        byte[] pSrcData,
        nuint srcDataSize,
        [MarshalAs(UnmanagedType.LPStr)] string? pSourceName,
        IntPtr pDefines,
        IntPtr pInclude,
        [MarshalAs(UnmanagedType.LPStr)] string? pEntrypoint,
        [MarshalAs(UnmanagedType.LPStr)] string pTarget,
        uint flags1,
        uint flags2,
        out IntPtr ppCode,
        out IntPtr ppErrorMsgs);

    [ComImport]
    [Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        [PreserveSig]
        IntPtr GetBufferPointer();

        [PreserveSig]
        nuint GetBufferSize();
    }

    public readonly record struct CompileResult(bool Success, byte[]? EffectBytecode, string Diagnostics);

    public static CompileResult CompileFxFile(string fxFilePath, string assetsRootDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new CompileResult(
                false,
                null,
                $"{fxFilePath}(1,1): error APOSH001: 内置 .fx -> .xnb 编译依赖 Windows 系统组件 d3dcompiler_47.dll。");
        }

        byte[] sourceBytes = StripUtf8Bom(File.ReadAllBytes(fxFilePath));
        string fileNameOnly = Path.GetFileName(fxFilePath);
        string baseDir = Path.GetDirectoryName(fxFilePath) ?? assetsRootDir;

        IntPtr codePtr;
        IntPtr errorPtr;
        int hr;

        using (var includeHandler = new NativeD3DIncludeHandler(baseDir, assetsRootDir))
        {
            hr = D3DCompile(
                sourceBytes,
                (nuint)sourceBytes.Length,
                fileNameOnly,
                IntPtr.Zero,
                includeHandler.Pointer,
                null,
                "fx_2_0",
                DefaultCompileFlags,
                0,
                out codePtr,
                out errorPtr);
        }

        string rawDiagnostics = ReadAndReleaseBlobString(errorPtr);
        string diagnostics = FilterKnownHarmlessWarnings(ExpandFileNameInDiagnostics(rawDiagnostics, fileNameOnly, fxFilePath));

        if (hr != 0 || codePtr == IntPtr.Zero)
        {
            if (codePtr != IntPtr.Zero)
                Marshal.Release(codePtr);

            string errMsg = string.IsNullOrWhiteSpace(diagnostics)
                ? $"{fxFilePath}(1,1): error APOSH002: d3dcompiler_47 编译失败 (HRESULT=0x{hr:X8})。"
                : diagnostics;

            return new CompileResult(false, null, errMsg);
        }

        byte[] bytecode = ReadAndReleaseBlobBytes(codePtr);
        return new CompileResult(true, bytecode, diagnostics);
    }

    /// <summary>
    /// 将 <c>fx_2_0</c> 字节码封装为未压缩 XNA 4.0 <c>.xnb</c>（<c>EffectReader</c>）二进制。
    /// </summary>
    public static byte[] WrapAsXnb(byte[] effectBytecode)
    {
        using var payloadMs = new MemoryStream(effectBytecode.Length + 256);
        using (var payloadWriter = new BinaryWriter(payloadMs, Encoding.UTF8, leaveOpen: true))
        {
            // Type reader table: 1 reader (EffectReader, version 0)
            Write7BitEncodedInt(payloadWriter, 1);
            payloadWriter.Write(EffectReaderTypeName);
            payloadWriter.Write(0);

            // Shared resource count: 0
            Write7BitEncodedInt(payloadWriter, 0);

            // Primary asset: reader index 1 (1-based)
            Write7BitEncodedInt(payloadWriter, 1);

            // EffectReader payload: int32 length + raw fx_2_0 bytecode
            payloadWriter.Write(effectBytecode.Length);
            payloadWriter.Write(effectBytecode);
        }

        byte[] payload = payloadMs.ToArray();
        uint totalSize = (uint)(XnbFxFormatConstants.XnbHeaderSize + payload.Length);

        byte[] xnb = new byte[totalSize];
        xnb[0] = (byte)'X';
        xnb[1] = (byte)'N';
        xnb[2] = (byte)'B';
        xnb[3] = XnbFxFormatConstants.XnbPlatformWindows;
        xnb[4] = XnbFxFormatConstants.Xna4FormatVersion;
        xnb[5] = XnbFxFormatConstants.XnbFlagUncompressed;

        BitConverter.TryWriteBytes(xnb.AsSpan(6, 4), totalSize);
        Buffer.BlockCopy(payload, 0, xnb, XnbFxFormatConstants.XnbHeaderSize, payload.Length);
        return xnb;
    }

    private static byte[] StripUtf8Bom(byte[] rawBytes)
    {
        if (rawBytes.Length >= XnbFxFormatConstants.Utf8BomLength
            && rawBytes[0] == XnbFxFormatConstants.Utf8BomByte0
            && rawBytes[1] == XnbFxFormatConstants.Utf8BomByte1
            && rawBytes[2] == XnbFxFormatConstants.Utf8BomByte2)
        {
            var stripped = new byte[rawBytes.Length - XnbFxFormatConstants.Utf8BomLength];
            Buffer.BlockCopy(rawBytes, XnbFxFormatConstants.Utf8BomLength, stripped, 0, stripped.Length);
            return stripped;
        }

        return rawBytes;
    }

    private static void Write7BitEncodedInt(BinaryWriter writer, int value)
    {
        uint v = (uint)value;
        while (v >= XnbFxFormatConstants.SevenBitContinuationMask)
        {
            writer.Write((byte)(v | XnbFxFormatConstants.SevenBitContinuationMask));
            v >>= XnbFxFormatConstants.SevenBitShiftStep;
        }
        writer.Write((byte)v);
    }

    [SupportedOSPlatform("windows")]
    private static string ReadAndReleaseBlobString(IntPtr blobPtr)
    {
        if (blobPtr == IntPtr.Zero)
            return string.Empty;

        ID3DBlob? blob = null;
        try
        {
            blob = (ID3DBlob)Marshal.GetObjectForIUnknown(blobPtr);
            int size = checked((int)blob.GetBufferSize());
            if (size <= 0)
                return string.Empty;

            return Marshal.PtrToStringAnsi(blob.GetBufferPointer(), size)?.TrimEnd('\0', '\r', '\n') ?? string.Empty;
        }
        finally
        {
            if (blob is not null)
                Marshal.FinalReleaseComObject(blob);
            Marshal.Release(blobPtr);
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ReadAndReleaseBlobBytes(IntPtr blobPtr)
    {
        ID3DBlob? blob = null;
        try
        {
            blob = (ID3DBlob)Marshal.GetObjectForIUnknown(blobPtr);
            int size = checked((int)blob.GetBufferSize());
            var bytes = new byte[size];
            if (size > 0)
                Marshal.Copy(blob.GetBufferPointer(), bytes, 0, size);
            return bytes;
        }
        finally
        {
            if (blob is not null)
                Marshal.FinalReleaseComObject(blob);
            Marshal.Release(blobPtr);
        }
    }

    private static string ExpandFileNameInDiagnostics(string raw, string fileNameOnly, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        string prefix = fileNameOnly + "(";
        var sb = new StringBuilder(raw.Length + 64);
        using var reader = new StringReader(raw);
        while (reader.ReadLine() is { } line)
        {
            if (sb.Length > 0)
                sb.AppendLine();

            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                sb.Append(fullPath).Append(line.AsSpan(fileNameOnly.Length));
            else
                sb.Append(line);
        }

        return sb.ToString();
    }

    private static string FilterKnownHarmlessWarnings(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var sb = new StringBuilder();
        using var reader = new StringReader(raw);
        while (reader.ReadLine() is { } line)
        {
            // d3dcompiler_47 编 fx_2_0 必然会发出 X4717 弃用提示，过滤以保持零噪音构建
            if (line.Contains("warning X4717", StringComparison.OrdinalIgnoreCase))
                continue;

            if (sb.Length > 0)
                sb.AppendLine();
            sb.Append(line);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 轻量原生 <c>ID3DInclude</c> 实现：支持相对当前文件、父包含文件与 <c>Assets/</c> 根目录的 <c>#include</c>，
    /// 并对每个头文件自动剥离 UTF-8 BOM。
    /// </summary>
    private sealed class NativeD3DIncludeHandler : IDisposable
    {
        private const int S_OK = 0;
        private const int E_FAIL = unchecked((int)0x80004005);

        [StructLayout(LayoutKind.Sequential)]
        private struct VTable
        {
            public delegate* unmanaged[Stdcall]<NativeObject*, int, byte*, void*, void**, uint*, int> Open;
            public delegate* unmanaged[Stdcall]<NativeObject*, void*, int> Close;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeObject
        {
            public VTable* Vtbl;
            public IntPtr ManagedHandle;
        }

        private static readonly VTable* SharedVTable = AllocateSharedVTable();

        private readonly string _baseDir;
        private readonly string _assetsRootDir;
        private readonly Dictionary<IntPtr, string> _openDirs = new();
        private NativeObject* _nativeObj;
        private GCHandle _selfHandle;

        public IntPtr Pointer => (IntPtr)_nativeObj;

        public NativeD3DIncludeHandler(string baseDir, string assetsRootDir)
        {
            _baseDir = baseDir;
            _assetsRootDir = assetsRootDir;
            _selfHandle = GCHandle.Alloc(this);
            _nativeObj = (NativeObject*)NativeMemory.Alloc((nuint)sizeof(NativeObject));
            _nativeObj->Vtbl = SharedVTable;
            _nativeObj->ManagedHandle = GCHandle.ToIntPtr(_selfHandle);
        }

        public void Dispose()
        {
            foreach (IntPtr ptr in _openDirs.Keys)
            {
                NativeMemory.Free((void*)ptr);
            }
            _openDirs.Clear();

            if (_nativeObj is not null)
            {
                NativeMemory.Free(_nativeObj);
                _nativeObj = null;
            }

            if (_selfHandle.IsAllocated)
                _selfHandle.Free();
        }

        private static VTable* AllocateSharedVTable()
        {
            var vtbl = (VTable*)NativeMemory.Alloc((nuint)sizeof(VTable));
            vtbl->Open = &OpenCallback;
            vtbl->Close = &CloseCallback;
            return vtbl;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int OpenCallback(
            NativeObject* self,
            int includeType,
            byte* pFileName,
            void* pParentData,
            void** ppData,
            uint* pBytes)
        {
            try
            {
                if (self is null || pFileName is null || ppData is null || pBytes is null)
                    return E_FAIL;

                var handle = GCHandle.FromIntPtr(self->ManagedHandle);
                if (handle.Target is not NativeD3DIncludeHandler handler)
                    return E_FAIL;

                string? fileName = Marshal.PtrToStringUTF8((IntPtr)pFileName);
                if (string.IsNullOrWhiteSpace(fileName))
                    return E_FAIL;

                string? parentDir = pParentData is not null && handler._openDirs.TryGetValue((IntPtr)pParentData, out string? pd)
                    ? pd
                    : null;

                string? resolvedPath = handler.ResolveIncludePath(fileName, parentDir);
                if (resolvedPath is null)
                    return E_FAIL;

                byte[] bytes = StripUtf8Bom(File.ReadAllBytes(resolvedPath));
                void* nativeBuf = NativeMemory.Alloc((nuint)Math.Max(bytes.Length, 1));
                if (bytes.Length > 0)
                {
                    fixed (byte* src = bytes)
                    {
                        Buffer.MemoryCopy(src, nativeBuf, bytes.Length, bytes.Length);
                    }
                }

                handler._openDirs[(IntPtr)nativeBuf] = Path.GetDirectoryName(resolvedPath) ?? handler._baseDir;
                *ppData = nativeBuf;
                *pBytes = (uint)bytes.Length;
                return S_OK;
            }
            catch
            {
                return E_FAIL;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CloseCallback(NativeObject* self, void* pData)
        {
            try
            {
                if (self is not null && pData is not null)
                {
                    var handle = GCHandle.FromIntPtr(self->ManagedHandle);
                    if (handle.Target is NativeD3DIncludeHandler handler)
                        handler._openDirs.Remove((IntPtr)pData);

                    NativeMemory.Free(pData);
                }

                return S_OK;
            }
            catch
            {
                return E_FAIL;
            }
        }

        private string? ResolveIncludePath(string includeFileName, string? parentDir)
        {
            if (Path.IsPathRooted(includeFileName) && File.Exists(includeFileName))
                return includeFileName;

            if (!string.IsNullOrEmpty(parentDir))
            {
                string candidate = Path.GetFullPath(Path.Combine(parentDir, includeFileName));
                if (File.Exists(candidate))
                    return candidate;
            }

            string fromBase = Path.GetFullPath(Path.Combine(_baseDir, includeFileName));
            if (File.Exists(fromBase))
                return fromBase;

            string fromAssets = Path.GetFullPath(Path.Combine(_assetsRootDir, includeFileName));
            if (File.Exists(fromAssets))
                return fromAssets;

            return null;
        }
    }
}
