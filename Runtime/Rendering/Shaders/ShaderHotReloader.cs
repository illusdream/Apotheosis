#if DEBUG
#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using log4net;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;

namespace Apotheosis.Runtime.Rendering.Shaders
{
    /// <summary>
    /// 仅在 <c>Development/DEBUG</c> 模式下生效的着色器运行时热重载器：
    /// 监听源码目录 <c>Assets/</c> 下的 <c>.fx</c> / <c>.fxh</c> / <c>.hlsl</c> 文件保存，
    /// 后台调用 Windows 系统内置 <c>d3dcompiler_47.dll</c> 与原生 <c>ID3DInclude</c> 处理器编译为 <c>fx_2_0</c>（支持 <c>vs_3_0</c>/<c>ps_3_0</c>），
    /// 并在主线程 <c>OnPreDraw</c> 安全间隙无缝替换 <see cref="ManagedShader"/> 底层 <see cref="Effect"/>。
    /// </summary>
    internal sealed unsafe class ShaderHotReloader : IDisposable
    {
        private const string ProjectDirMetadataKey = "ApotheosisProjectDir";
        private const int DebounceMs = 160;
        private const int PollIntervalMs = 80;
        private const int FileReadRetries = 5;
        private const int FileReadRetryDelayMs = 45;
        private const int DisposeTimerWaitMs = 500;
        private const uint DefaultCompileFlags = 1u << 15; // D3DCOMPILE_OPTIMIZATION_LEVEL3
        private const byte Utf8BomByte0 = 0xEF;
        private const byte Utf8BomByte1 = 0xBB;
        private const byte Utf8BomByte2 = 0xBF;
        private const int Utf8BomLength = 3;

        private static readonly HashSet<string> ShaderDirNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Effect",
            "Effects",
            "Shader",
            "Shaders",
        };

        private static readonly HashSet<string> FontDirNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Font",
            "Fonts",
        };

        private static readonly HashSet<string> ExcludedDirNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin",
            "obj",
            ".git",
            ".vs",
            ".idea",
            "Generated",
            "Generator",
            "Tools",
        };

        private readonly struct PendingReload
        {
            public readonly string AssetPath;
            public readonly string RelativeFile;
            public readonly byte[]? Bytecode;
            public readonly string Diagnostics;
            public readonly long CompileMs;
            public readonly bool Success;

            public PendingReload(string assetPath, string relativeFile, byte[]? bytecode, string diagnostics, long compileMs, bool success)
            {
                AssetPath = assetPath;
                RelativeFile = relativeFile;
                Bytecode = bytecode;
                Diagnostics = diagnostics;
                CompileMs = compileMs;
                Success = success;
            }
        }

        private readonly ShaderModule _shaders;
        private readonly ILog _logger;
        private readonly string _modName;
        private readonly string _modAssetPrefix;
        private readonly string _projectDir;
        private readonly string _assetsDir;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _debounceTimer;

        private readonly ConcurrentDictionary<string, long> _pendingFxFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<PendingReload> _completedQueue = new();

        private int _compilingFlag;
        private volatile bool _disposed;

        private ShaderHotReloader(ShaderModule shaders, ILog logger, string modName, string projectDir, string assetsDir)
        {
            _shaders = shaders;
            _logger = logger;
            _modName = modName;
            _modAssetPrefix = modName + "/";
            _projectDir = projectDir;
            _assetsDir = assetsDir;

            _watcher = new FileSystemWatcher(assetsDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = false,
            };

            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileRenamed;

            _debounceTimer = new Timer(OnDebounceTick, null, PollIntervalMs, PollIntervalMs);
            _watcher.EnableRaisingEvents = true;

            _logger.Info($"[ShaderHotReload] Watching '{assetsDir}' for .fx/.fxh/.hlsl changes.");
        }

        /// <summary>尝试启动热重载监听（非 Windows、服务端或源码目录不存在时安全返回 <c>null</c>）。</summary>
        public static ShaderHotReloader? TryStart(ShaderModule shaders, ILog logger, string modName)
        {
            if (Main.dedServ || !OperatingSystem.IsWindows())
                return null;

            string? projectDir = ResolveProjectDirectory(typeof(ShaderHotReloader).Assembly);
            if (string.IsNullOrWhiteSpace(projectDir))
                return null;

            string assetsDir = Path.Combine(projectDir, "Assets");
            if (!Directory.Exists(assetsDir))
                return null;

            try
            {
                return new ShaderHotReloader(shaders, logger, modName, projectDir, assetsDir);
            }
            catch (Exception ex)
            {
                logger.Warn($"[ShaderHotReload] Failed to start file watcher: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 在主线程 <c>OnPreDraw</c> 安全间隙调用：合并同一路径的多次连续编译结果（只保留最新一份），
        /// 把后台编译完成的字节码实例化为 FNA <see cref="Effect"/> 并热替换到对应 <see cref="ManagedShader"/>。
        /// 无待处理项时仅一次无锁 <see cref="ConcurrentQueue{T}.IsEmpty"/> 检查。
        /// </summary>
        public void ProcessPendingOnMainThread()
        {
            if (_disposed || _completedQueue.IsEmpty)
                return;

            GraphicsDevice? gd = Main.graphics?.GraphicsDevice;
            if (gd is null || gd.IsDisposed)
                return;

            // 合并同一 AssetPath 的重复入队，只应用最新编译产物，避免连续保存产生多余的 GPU Effect 创建与销毁
            Dictionary<string, PendingReload>? latestByPath = null;
            while (_completedQueue.TryDequeue(out PendingReload dequeued))
            {
                latestByPath ??= new Dictionary<string, PendingReload>(StringComparer.OrdinalIgnoreCase);
                latestByPath[dequeued.AssetPath] = dequeued;
            }

            if (latestByPath is null)
                return;

            foreach (PendingReload item in latestByPath.Values)
            {
                if (!item.Success || item.Bytecode is null)
                {
                    string errText = $"[ShaderHotReload] Compile failed: {item.RelativeFile}\n{item.Diagnostics}";
                    _logger.Error(errText);
                    ShowInGameMessage($"[ShaderHotReload] 编译失败: {item.RelativeFile}（详情见 client.log）", Color.OrangeRed);
                    continue;
                }

                if (!_shaders.TryGetByAssetPath(item.AssetPath, out ManagedShader? shader))
                {
                    // 该着色器尚未被当前会话注册使用，跳过即可
                    continue;
                }

                Effect? newEffect = null;
                try
                {
                    newEffect = new Effect(gd, item.Bytecode);
                    shader.ApplyHotReloadedEffect(newEffect);

                    string okMsg = $"[ShaderHotReload] Reloaded {item.RelativeFile} ({item.CompileMs} ms)";
                    _logger.Info(okMsg);
                    if (!string.IsNullOrWhiteSpace(item.Diagnostics))
                        _logger.Warn(item.Diagnostics);

                    ShowInGameMessage(okMsg, Color.Cyan);
                }
                catch (Exception ex)
                {
                    if (newEffect is not null && !newEffect.IsDisposed)
                        newEffect.Dispose();

                    _logger.Error($"[ShaderHotReload] Failed to apply effect '{item.RelativeFile}':", ex);
                    ShowInGameMessage($"[ShaderHotReload] 应用失败: {item.RelativeFile} - {ex.Message}", Color.OrangeRed);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileEvent;
                _watcher.Created -= OnFileEvent;
                _watcher.Renamed -= OnFileRenamed;
                _watcher.Dispose();
            }
            catch
            {
                // 卸载阶段静默清理
            }

            try
            {
                using var timerDone = new ManualResetEvent(false);
                if (_debounceTimer.Dispose(timerDone))
                    timerDone.WaitOne(DisposeTimerWaitMs);
            }
            catch
            {
                // 卸载阶段静默清理
            }

            _pendingFxFiles.Clear();
            while (_completedQueue.TryDequeue(out _))
            {
            }
        }

        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            if (!_disposed && !string.IsNullOrWhiteSpace(e.FullPath))
                EnqueueChangedFile(e.FullPath);
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            if (!_disposed && !string.IsNullOrWhiteSpace(e.FullPath))
                EnqueueChangedFile(e.FullPath);
        }

        private void EnqueueChangedFile(string fullPath)
        {
            string ext = Path.GetExtension(fullPath);
            long now = Environment.TickCount64;

            if (string.Equals(ext, ".fx", StringComparison.OrdinalIgnoreCase))
            {
                if (IsInShaderDirectory(fullPath))
                    _pendingFxFiles[fullPath] = now;
                return;
            }

            if (string.Equals(ext, ".fxh", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".hlsl", StringComparison.OrdinalIgnoreCase))
            {
                // 共享头文件变动：将所有已注册且磁盘上存在 .fx 源码的着色器加入重编队列
                string[] activeAssetPaths = _shaders.GetRegisteredAssetPathsSnapshot();
                for (int i = 0; i < activeAssetPaths.Length; i++)
                {
                    string? fxPath = TryMapAssetPathToFxFile(activeAssetPaths[i]);
                    if (fxPath is not null && File.Exists(fxPath))
                        _pendingFxFiles[fxPath] = now;
                }
            }
        }

        private void OnDebounceTick(object? state)
        {
            if (_disposed || _pendingFxFiles.IsEmpty)
                return;

            if (Interlocked.CompareExchange(ref _compilingFlag, 1, 0) != 0)
                return;

            try
            {
                long now = Environment.TickCount64;
                foreach (var kvp in _pendingFxFiles)
                {
                    if (_disposed)
                        return;

                    if (now - kvp.Value < DebounceMs)
                        continue;

                    if (!_pendingFxFiles.TryRemove(kvp.Key, out long timestamp))
                        continue;

                    // 若在 TryRemove 前刚被更新为更新的时间戳，保留较大时间戳继续等待防抖
                    if (now - timestamp < DebounceMs)
                    {
                        _pendingFxFiles.AddOrUpdate(
                            kvp.Key,
                            static (_, ts) => ts,
                            static (_, existing, ts) => Math.Max(existing, ts),
                            timestamp);
                        continue;
                    }

                    CompileAndEnqueue(kvp.Key);
                }
            }
            finally
            {
                Volatile.Write(ref _compilingFlag, 0);
            }
        }

        private void CompileAndEnqueue(string fxPath)
        {
            if (_disposed || !OperatingSystem.IsWindows() || !File.Exists(fxPath))
                return;

            string relPath = Path.GetRelativePath(_projectDir, fxPath).Replace('\\', '/');
            if (!relPath.EndsWith(".fx", StringComparison.OrdinalIgnoreCase))
                return;

            string relWithoutExt = relPath.Substring(0, relPath.Length - 3);
            string assetPath = _modAssetPrefix + relWithoutExt;

            // 仅热编当前会话中已经注册的着色器
            if (!_shaders.TryGetByAssetPath(assetPath, out _))
                return;

            byte[]? rawBytes = TryReadFileWithRetry(fxPath);
            if (rawBytes is null || rawBytes.Length == 0)
                return;

            var sw = Stopwatch.StartNew();
            var (success, bytecode, diagnostics) = CompileFxWindows(fxPath, rawBytes, _assetsDir);
            sw.Stop();

            if (!_disposed)
                _completedQueue.Enqueue(new PendingReload(assetPath, relPath, bytecode, diagnostics, sw.ElapsedMilliseconds, success));
        }

        private static byte[]? TryReadFileWithRetry(string filePath)
        {
            for (int attempt = 0; attempt < FileReadRetries; attempt++)
            {
                try
                {
                    // 使用 FileShare.Read 等待编辑器完成写入并关闭写入句柄，避免读到截断或半写文件
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (fs.Length == 0)
                    {
                        if (attempt + 1 < FileReadRetries)
                        {
                            Thread.Sleep(FileReadRetryDelayMs);
                            continue;
                        }

                        return null;
                    }

                    int expectedLen = checked((int)fs.Length);
                    var bytes = new byte[expectedLen];
                    int readTotal = 0;
                    while (readTotal < expectedLen)
                    {
                        int n = fs.Read(bytes, readTotal, expectedLen - readTotal);
                        if (n <= 0)
                            break;
                        readTotal += n;
                    }

                    if (readTotal == 0 && attempt + 1 < FileReadRetries)
                    {
                        Thread.Sleep(FileReadRetryDelayMs);
                        continue;
                    }

                    if (readTotal != bytes.Length)
                        Array.Resize(ref bytes, readTotal);

                    return bytes;
                }
                catch (IOException) when (attempt + 1 < FileReadRetries)
                {
                    Thread.Sleep(FileReadRetryDelayMs);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private bool IsInShaderDirectory(string fullPath)
        {
            string rel = Path.GetRelativePath(_projectDir, fullPath).Replace('\\', '/');
            string[] segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                return false;

            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (ExcludedDirNames.Contains(segments[i]))
                    return false;
            }

            // 由内向外匹配，与 AssetPathResolver / ShaderTools 保持完全一致
            for (int i = segments.Length - 2; i >= 0; i--)
            {
                if (FontDirNames.Contains(segments[i]))
                    return false;

                if (ShaderDirNames.Contains(segments[i]))
                    return true;
            }

            return false;
        }

        private string? TryMapAssetPathToFxFile(string modAssetPath)
        {
            if (!modAssetPath.StartsWith(_modAssetPrefix, StringComparison.OrdinalIgnoreCase))
                return null;

            string relNoExt = modAssetPath.Substring(_modAssetPrefix.Length).Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(_projectDir, relNoExt + ".fx");
        }

        private static string? ResolveProjectDirectory(Assembly assembly)
        {
            foreach (var attr in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(attr.Key, ProjectDirMetadataKey, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(attr.Value))
                {
                    return attr.Value;
                }
            }

            return null;
        }

        private static void ShowInGameMessage(string text, Color color)
        {
            if (!Main.gameMenu)
                Main.NewText(text, color);
        }

        // ==================== Windows d3dcompiler_47.dll 编译互操作（自定义 ID3DInclude，绝不修改进程 CWD） ====================

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

        [SupportedOSPlatform("windows")]
        private static (bool Success, byte[]? Bytecode, string Diagnostics) CompileFxWindows(
            string fxPath,
            byte[] rawBytes,
            string assetsDir)
        {
            byte[] sourceBytes = StripUtf8Bom(rawBytes);
            string baseDir = Path.GetDirectoryName(fxPath) ?? assetsDir;

            IntPtr codePtr;
            IntPtr errorPtr;
            int hr;

            using (var includeHandler = new NativeD3DIncludeHandler(baseDir, assetsDir))
            {
                hr = D3DCompile(
                    sourceBytes,
                    (nuint)sourceBytes.Length,
                    Path.GetFileName(fxPath),
                    IntPtr.Zero,
                    includeHandler.Pointer,
                    null,
                    "fx_2_0",
                    DefaultCompileFlags,
                    0,
                    out codePtr,
                    out errorPtr);
            }

            string diagnostics = FilterKnownHarmlessWarnings(ReadAndReleaseBlobString(errorPtr));
            if (hr != 0 || codePtr == IntPtr.Zero)
            {
                if (codePtr != IntPtr.Zero)
                    Marshal.Release(codePtr);

                string errMsg = string.IsNullOrWhiteSpace(diagnostics)
                    ? $"d3dcompiler_47 编译失败 (HRESULT=0x{hr:X8})"
                    : diagnostics;

                return (false, null, errMsg);
            }

            byte[] bytecode = ReadAndReleaseBlobBytes(codePtr);
            return (true, bytecode, diagnostics);
        }

        private static byte[] StripUtf8Bom(byte[] rawBytes)
        {
            if (rawBytes.Length >= Utf8BomLength
                && rawBytes[0] == Utf8BomByte0
                && rawBytes[1] == Utf8BomByte1
                && rawBytes[2] == Utf8BomByte2)
            {
                var stripped = new byte[rawBytes.Length - Utf8BomLength];
                Buffer.BlockCopy(rawBytes, Utf8BomLength, stripped, 0, stripped.Length);
                return stripped;
            }

            return rawBytes;
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

        private static string FilterKnownHarmlessWarnings(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            var sb = new StringBuilder();
            using var reader = new StringReader(raw);
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("warning X4717", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append(line);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 面向可卸载 Mod 程序集的原生 <c>ID3DInclude</c> 实现：
        /// 实例级分配与释放 vtable（模组重载零泄漏），支持相对父文件/主文件/<c>Assets/</c> 根目录的 <c>#include</c>，
        /// 并对全部头文件自动剥离 UTF-8 BOM，绝不修改进程当前工作目录。
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

            private readonly string _baseDir;
            private readonly string _assetsRootDir;
            private readonly Dictionary<IntPtr, string> _openDirs = new();
            private VTable* _vtable;
            private NativeObject* _nativeObj;
            private GCHandle _selfHandle;

            public IntPtr Pointer => (IntPtr)_nativeObj;

            public NativeD3DIncludeHandler(string baseDir, string assetsRootDir)
            {
                _baseDir = baseDir;
                _assetsRootDir = assetsRootDir;
                _selfHandle = GCHandle.Alloc(this);

                _vtable = (VTable*)NativeMemory.Alloc((nuint)sizeof(VTable));
                _vtable->Open = &OpenCallback;
                _vtable->Close = &CloseCallback;

                _nativeObj = (NativeObject*)NativeMemory.Alloc((nuint)sizeof(NativeObject));
                _nativeObj->Vtbl = _vtable;
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

                if (_vtable is not null)
                {
                    NativeMemory.Free(_vtable);
                    _vtable = null;
                }

                if (_selfHandle.IsAllocated)
                    _selfHandle.Free();
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

                    byte[]? raw = TryReadFileWithRetry(resolvedPath);
                    if (raw is null)
                        return E_FAIL;

                    byte[] bytes = StripUtf8Bom(raw);
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
}
#endif
