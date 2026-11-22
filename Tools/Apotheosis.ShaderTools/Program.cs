using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Apotheosis.ShaderTools;

internal static class Program
{
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
        "Generator",
        "Generated",
        "Tools",
        "Properties",
        "Localization",
        ".git",
        ".idea",
        ".vs",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static int Main(string[] args)
    {
        string? projectDir = null;
        string? metaDir = null;
        string modName = "Apotheosis";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project-dir" when i + 1 < args.Length:
                    projectDir = args[++i];
                    break;
                case "--meta-dir" when i + 1 < args.Length:
                    metaDir = args[++i];
                    break;
                case "--mod-name" when i + 1 < args.Length:
                    modName = args[++i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(projectDir) || string.IsNullOrWhiteSpace(metaDir))
        {
            Console.Error.WriteLine("error APOSH000: 缺少必需参数 --project-dir 或 --meta-dir。");
            return 1;
        }

        projectDir = Path.GetFullPath(projectDir);
        metaDir = Path.GetFullPath(metaDir);
        Directory.CreateDirectory(metaDir);

        string assetsDir = Path.Combine(projectDir, "Assets");
        if (!Directory.Exists(assetsDir))
        {
            CleanStaleFiles(metaDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase), "*.shader.meta.json");
            CleanStaleFiles(metaDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase), "*.compile.stamp");
            return 0;
        }

        bool hasErrors = false;

        // 预先采集 Assets 下全部共享着色器头文件 (.fxh / .hlsl) 的最新修改时间，支持跨目录 #include 增量重编
        DateTime latestSharedHeaderUtc = GetLatestShaderHeaderUtc(assetsDir);

        // 1. 增量编译 Assets 下着色器目录中的 .fx -> 未压缩 .xnb
        var fxFiles = Directory.EnumerateFiles(assetsDir, "*.fx", SearchOption.AllDirectories)
            .Where(path => IsInShaderDirectory(projectDir, path))
            .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var activeStampFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string fxPath in fxFiles)
        {
            string relFx = Path.GetRelativePath(projectDir, fxPath).Replace('\\', '/');
            string relWithoutExt = relFx.Substring(0, relFx.Length - 3);
            string stampPath = Path.Combine(metaDir, BuildUniqueMetaFileName(relWithoutExt) + ".compile.stamp");
            activeStampFiles.Add(stampPath);

            string xnbPath = Path.ChangeExtension(fxPath, ".xnb");
            if (!NeedsFxRecompile(fxPath, xnbPath, stampPath, latestSharedHeaderUtc))
                continue;

            var compileResult = FxToXnbCompiler.CompileFxFile(fxPath, assetsDir);
            if (!string.IsNullOrWhiteSpace(compileResult.Diagnostics))
            {
                TextWriter writer = compileResult.Success ? Console.Out : Console.Error;
                writer.WriteLine(compileResult.Diagnostics);
            }

            if (!compileResult.Success || compileResult.EffectBytecode is null)
            {
                hasErrors = true;
                continue;
            }

            byte[] xnbBytes = FxToXnbCompiler.WrapAsXnb(compileResult.EffectBytecode);
            bool xnbUpdated = WriteFileIfChanged(xnbPath, xnbBytes);

            // 更新构建时间戳印记，避免改注释/无关头文件导致字节码不变时陷入“永久重复重编”死循环
            File.WriteAllText(stampPath, DateTime.UtcNow.Ticks.ToString(), Encoding.ASCII);

            if (xnbUpdated)
            {
                Console.WriteLine($"[Apotheosis.ShaderTools] Compiled {Path.GetRelativePath(projectDir, fxPath)} -> {Path.GetRelativePath(projectDir, xnbPath)} ({xnbBytes.Length} bytes)");
            }
        }

        CleanStaleFiles(metaDir, activeStampFiles, "*.compile.stamp");

        if (hasErrors)
            return 1;

        // 2. 扫描所有着色器目录中的 .xnb（含自动编译与手动放入），提取参数表写入 .shader.meta.json
        var xnbFiles = Directory.EnumerateFiles(assetsDir, "*.xnb", SearchOption.AllDirectories)
            .Where(path => IsInShaderDirectory(projectDir, path))
            .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var activeMetaFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string xnbPath in xnbFiles)
        {
            string relPath = Path.GetRelativePath(projectDir, xnbPath).Replace('\\', '/');
            string relWithoutExt = relPath.Substring(0, relPath.Length - 4);
            string modAssetPath = modName + "/" + relWithoutExt;

            try
            {
                ShaderEffectMeta meta = XnbEffectParser.ParseXnbFile(xnbPath, modAssetPath, relPath);

                if (meta.HasNonSm3Shader)
                {
                    Console.WriteLine(
                        $"{xnbPath}(1,1): warning APOSH003: 着色器 {relPath} 包含非 Shader Model 3.0 的 Pass（检测到: {string.Join(", ", meta.ShaderModels)}），建议统一使用 vs_3_0 / ps_3_0。");
                }

                if (!ValidateGlobalParameters(xnbPath, relPath, meta))
                {
                    hasErrors = true;
                    continue;
                }

                string metaFileName = BuildUniqueMetaFileName(relWithoutExt) + ".shader.meta.json";
                string metaFilePath = Path.Combine(metaDir, metaFileName);
                activeMetaFiles.Add(metaFilePath);

                string json = JsonSerializer.Serialize(meta, JsonOptions);
                WriteTextIfChanged(metaFilePath, json);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{xnbPath}(1,1): error APOSH004: 解析着色器 XNB 失败: {ex.Message}");
                hasErrors = true;
            }
        }

        CleanStaleFiles(metaDir, activeMetaFiles, "*.shader.meta.json");
        return hasErrors ? 1 : 0;
    }

    private static bool ValidateGlobalParameters(string xnbPath, string relPath, ShaderEffectMeta meta)
    {
        bool valid = true;

        foreach (var p in meta.Parameters)
        {
            var paramType = (D3dxParameterType)p.ParamType;
            var paramClass = (D3dxParameterClass)p.ParamClass;

            switch (p.Name)
            {
                case "uTime":
                    if (!(paramType == D3dxParameterType.Float && paramClass == D3dxParameterClass.Scalar && p.Elements == 0))
                    {
                        Console.Error.WriteLine(
                            $"{xnbPath}(1,1): error APOSH005: 着色器 {relPath} 中的内置全局参数 uTime 必须为标量 float（实际: type={paramType}, class={paramClass}, rows={p.Rows}, cols={p.Columns}, elems={p.Elements}）。");
                        valid = false;
                    }
                    break;

                case "uTransformMatrix":
                    if (!(paramType == D3dxParameterType.Float
                          && paramClass is D3dxParameterClass.MatrixRows or D3dxParameterClass.MatrixColumns
                          && p.Rows == 4 && p.Columns == 4 && p.Elements == 0))
                    {
                        Console.Error.WriteLine(
                            $"{xnbPath}(1,1): error APOSH005: 着色器 {relPath} 中的内置全局参数 uTransformMatrix 必须为 float4x4 矩阵（实际: type={paramType}, class={paramClass}, rows={p.Rows}, cols={p.Columns}, elems={p.Elements}）。");
                        valid = false;
                    }
                    break;

                case "uScreenResolution":
                case "uZoom":
                    if (!(paramType == D3dxParameterType.Float && paramClass == D3dxParameterClass.Vector && p.Columns == 2 && p.Elements == 0))
                    {
                        Console.Error.WriteLine(
                            $"{xnbPath}(1,1): error APOSH005: 着色器 {relPath} 中的内置全局参数 {p.Name} 必须为 float2 向量（实际: type={paramType}, class={paramClass}, rows={p.Rows}, cols={p.Columns}, elems={p.Elements}）。");
                        valid = false;
                    }
                    break;
            }
        }

        return valid;
    }

    private static DateTime GetLatestShaderHeaderUtc(string assetsDir)
    {
        DateTime latest = DateTime.MinValue;

        foreach (string header in Directory.EnumerateFiles(assetsDir, "*.fxh", SearchOption.AllDirectories))
        {
            DateTime t = File.GetLastWriteTimeUtc(header);
            if (t > latest)
                latest = t;
        }

        foreach (string header in Directory.EnumerateFiles(assetsDir, "*.hlsl", SearchOption.AllDirectories))
        {
            DateTime t = File.GetLastWriteTimeUtc(header);
            if (t > latest)
                latest = t;
        }

        return latest;
    }

    /// <summary>
    /// 与 <c>AssetPathResolver.ResolveAssetKind</c> 保持严格一致：从最内层目录向外判定，
    /// 遇排除目录或更内层的 <c>Font</c>/<c>Fonts</c> 目录时判定为非着色器。
    /// </summary>
    private static bool IsInShaderDirectory(string projectDir, string fullPath)
    {
        string rel = Path.GetRelativePath(projectDir, fullPath).Replace('\\', '/');
        string[] segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (ExcludedDirNames.Contains(segments[i]))
                return false;
        }

        for (int i = segments.Length - 2; i >= 0; i--)
        {
            if (FontDirNames.Contains(segments[i]))
                return false;
            if (ShaderDirNames.Contains(segments[i]))
                return true;
        }

        return false;
    }

    private static bool NeedsFxRecompile(string fxPath, string xnbPath, string stampPath, DateTime latestSharedHeaderUtc)
    {
        if (!File.Exists(xnbPath))
            return true;

        DateTime effectiveOutputUtc = File.GetLastWriteTimeUtc(xnbPath);
        if (File.Exists(stampPath))
        {
            DateTime stampUtc = File.GetLastWriteTimeUtc(stampPath);
            if (stampUtc > effectiveOutputUtc)
                effectiveOutputUtc = stampUtc;
        }

        if (File.GetLastWriteTimeUtc(fxPath) > effectiveOutputUtc)
            return true;

        if (latestSharedHeaderUtc > effectiveOutputUtc)
            return true;

        return false;
    }

    private static string BuildUniqueMetaFileName(string relWithoutExt)
    {
        var sb = new StringBuilder(relWithoutExt.Length + 9);
        foreach (char c in relWithoutExt)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        }

        uint hash = ComputeFnv1a32(relWithoutExt.ToLowerInvariant());
        sb.Append('_').Append(hash.ToString("x8"));
        return sb.ToString();
    }

    private static uint ComputeFnv1a32(string text)
    {
        uint hash = XnbFxFormatConstants.Fnv1a32OffsetBasis;
        for (int i = 0; i < text.Length; i++)
        {
            hash ^= text[i];
            hash *= XnbFxFormatConstants.Fnv1a32Prime;
        }

        return hash;
    }

    private static bool WriteFileIfChanged(string path, byte[] bytes)
    {
        if (File.Exists(path))
        {
            byte[] existing = File.ReadAllBytes(path);
            if (existing.AsSpan().SequenceEqual(bytes))
                return false;
        }

        File.WriteAllBytes(path, bytes);
        return true;
    }

    private static void WriteTextIfChanged(string path, string content)
    {
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path, Encoding.UTF8);
            if (string.Equals(existing, content, StringComparison.Ordinal))
                return;
        }

        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void CleanStaleFiles(string dir, HashSet<string> activeFiles, string searchPattern)
    {
        if (!Directory.Exists(dir))
            return;

        foreach (string file in Directory.EnumerateFiles(dir, searchPattern, SearchOption.TopDirectoryOnly))
        {
            if (!activeFiles.Contains(file))
                File.Delete(file);
        }
    }
}
