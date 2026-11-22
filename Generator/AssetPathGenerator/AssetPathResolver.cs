using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace Apotheosis.Generator.AssetPathGenerator;

internal static class AssetPathResolver
{
    private static readonly string[] NormalizedTargetRoots = NormalizePathList(AssetGeneratorConfig.TargetRootDirectories);
    private static readonly HashSet<string> SkippedPaths = new(NormalizePathList(AssetGeneratorConfig.SkippedDirectoryPaths), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SkippedNames = new(AssetGeneratorConfig.SkippedDirectoryNames, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExcludedDirNames = new(AssetGeneratorConfig.ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExcludedFiles = new(NormalizePathList(AssetGeneratorConfig.ExcludedFiles), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FontDirNames = new(AssetGeneratorConfig.FontDirectoryNames, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ShaderDirNames = new(AssetGeneratorConfig.ShaderDirectoryNames, StringComparer.OrdinalIgnoreCase);

    public static AssetRecord? TryCreateAssetRecord(string fullPath, GeneratorOptions options)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(options.ProjectDir))
            return null;

        string normalizedFull = NormalizePath(fullPath);
        string normalizedRoot = NormalizeDirectoryPrefix(options.ProjectDir);

        if (!normalizedFull.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        string relativePath = normalizedFull.Substring(normalizedRoot.Length).TrimStart('/');
        if (relativePath.Length == 0)
            return null;

        // 检查排除文件（按精确相对路径或文件名匹配）。
        int lastSlash = relativePath.LastIndexOf('/');
        string fileName = lastSlash >= 0 ? relativePath.Substring(lastSlash + 1) : relativePath;
        if (ExcludedFiles.Contains(relativePath) || ExcludedFiles.Contains(fileName))
            return null;

        // 拆分目录段。
        string[] rawDirSegments = lastSlash > 0
            ? relativePath.Substring(0, lastSlash).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

        // 检查任意层级中是否存在被排除的目录名。
        for (int i = 0; i < rawDirSegments.Length; i++)
        {
            if (ExcludedDirNames.Contains(rawDirSegments[i]))
                return null;
        }

        // 按配置的 TargetRootDirectories 进行过滤（如有配置）。
        if (!MatchesTargetRoot(relativePath))
            return null;

        // 解析扩展名与不含扩展名的文件名。
        int lastDot = fileName.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == fileName.Length - 1)
            return null;

        string extension = fileName.Substring(lastDot).ToLowerInvariant();
        string fileNameWithoutExt = fileName.Substring(0, lastDot);

        AssetKind? kind = ResolveAssetKind(extension, rawDirSegments);
        if (kind is null)
            return null;

        // 应用目录跳过机制（支持精确相对目录路径与任意层级目录名）。
        var emittedDirs = ImmutableArray.CreateBuilder<string>(rawDirSegments.Length);
        var skippedDirs = ImmutableArray.CreateBuilder<string>();
        var cumulativePath = new StringBuilder();

        for (int i = 0; i < rawDirSegments.Length; i++)
        {
            string segment = rawDirSegments[i];
            if (cumulativePath.Length > 0)
                cumulativePath.Append('/');
            cumulativePath.Append(segment);

            string currentRelativeDir = cumulativePath.ToString();
            if (SkippedPaths.Contains(currentRelativeDir) || SkippedNames.Contains(segment))
            {
                skippedDirs.Add(segment);
            }
            else
            {
                emittedDirs.Add(segment);
            }
        }

        string relativePathWithoutExt = relativePath.Substring(0, relativePath.Length - extension.Length);
        string modName = string.IsNullOrWhiteSpace(options.RootNamespace) ? "Apotheosis" : options.RootNamespace;
        string modAssetPath = modName + "/" + relativePathWithoutExt;

        return new AssetRecord(
            modAssetPath: modAssetPath,
            relativeFilePath: relativePath,
            directorySegments: new EquatableArray<string>(emittedDirs.ToImmutable()),
            skippedSegments: new EquatableArray<string>(skippedDirs.ToImmutable()),
            fileNameWithoutExtension: fileNameWithoutExt,
            extension: extension,
            kind: kind.Value);
    }

    public static int GetExtensionPriority(string extension)
    {
        // 当两个文件解析到相同的 ModAssetPath 与 AssetKind 时，数值越小优先级越高。
        return extension switch
        {
            ".png" => 0,
            ".rawimg" => 1,
            ".xnb" => 0,
            ".ogg" => 0,
            ".wav" => 1,
            ".mp3" => 2,
            _ => 99,
        };
    }

    public static string GetKindSuffix(AssetKind kind)
    {
        return kind switch
        {
            AssetKind.Texture => "Texture",
            AssetKind.Effect => "Effect",
            AssetKind.Font => "Font",
            AssetKind.Sound => "Sound",
            _ => "Asset",
        };
    }

    /// <summary>
    /// 将原始目录名或文件名转换为合法且未转义的 C# 标识符核心名称（不含前导 '@'）。
    /// </summary>
    public static string SanitizeIdentifierCore(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Asset";

        var sb = new StringBuilder(raw.Length + 1);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (i == 0)
            {
                if (SyntaxFacts.IsIdentifierStartCharacter(c))
                {
                    sb.Append(c);
                }
                else if (SyntaxFacts.IsIdentifierPartCharacter(c))
                {
                    sb.Append('_');
                    sb.Append(c);
                }
                else
                {
                    sb.Append('_');
                }
            }
            else
            {
                sb.Append(SyntaxFacts.IsIdentifierPartCharacter(c) ? c : '_');
            }
        }

        string result = sb.ToString();
        bool allUnderscores = true;
        for (int i = 0; i < result.Length; i++)
        {
            if (result[i] != '_')
            {
                allUnderscores = false;
                break;
            }
        }

        return allUnderscores ? "Asset" + result : result;
    }

    /// <summary>
    /// 若标识符核心名称属于 C# 关键字或保留上下文关键字，则为其添加 '@' 前缀进行转义。
    /// </summary>
    public static string EscapeIfKeyword(string identifierCore)
    {
        if (SyntaxFacts.GetKeywordKind(identifierCore) != SyntaxKind.None
            || identifierCore == "file"
            || identifierCore == "nint"
            || identifierCore == "nuint"
            || identifierCore == "record"
            || identifierCore == "required"
            || identifierCore == "scoped"
            || identifierCore == "var")
        {
            return "@" + identifierCore;
        }

        return identifierCore;
    }

    private static AssetKind? ResolveAssetKind(string extension, string[] dirSegments)
    {
        switch (extension)
        {
            case ".png":
            case ".rawimg":
                return AssetKind.Texture;

            case ".ogg":
            case ".wav":
            case ".mp3":
                return AssetKind.Sound;

            case ".xnb":
                for (int i = dirSegments.Length - 1; i >= 0; i--)
                {
                    if (FontDirNames.Contains(dirSegments[i]))
                        return AssetKind.Font;
                    if (ShaderDirNames.Contains(dirSegments[i]))
                        return AssetKind.Effect;
                }
                return null;

            default:
                return null;
        }
    }

    private static bool MatchesTargetRoot(string relativePath)
    {
        if (NormalizedTargetRoots.Length == 0)
            return true;

        for (int i = 0; i < NormalizedTargetRoots.Length; i++)
        {
            string root = NormalizedTargetRoots[i];
            if (root.Length == 0)
                return true;

            if (relativePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && relativePath.Length > root.Length
                && relativePath[root.Length] == '/')
            {
                return true;
            }
        }

        return false;
    }

    private static string[] NormalizePathList(string[] inputs)
    {
        if (inputs is null || inputs.Length == 0)
            return Array.Empty<string>();

        var list = new List<string>(inputs.Length);
        for (int i = 0; i < inputs.Length; i++)
        {
            string normalized = NormalizePath(inputs[i]).Trim('/');
            if (normalized.Length > 0)
                list.Add(normalized);
        }

        return list.ToArray();
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static string NormalizeDirectoryPrefix(string dirPath)
    {
        string normalized = NormalizePath(dirPath).TrimEnd('/');
        return normalized + "/";
    }
}
