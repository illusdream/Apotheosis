using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Apotheosis.Generator.AssetPathGenerator;

internal enum AssetKind
{
    Texture = 0,
    Effect = 1,
    Font = 2,
    Sound = 3,
}

internal sealed class GeneratorOptions : IEquatable<GeneratorOptions>
{
    public string ProjectDir { get; }
    public string RootNamespace { get; }

    public GeneratorOptions(string projectDir, string rootNamespace)
    {
        ProjectDir = projectDir;
        RootNamespace = rootNamespace;
    }

    public bool Equals(GeneratorOptions? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return string.Equals(ProjectDir, other.ProjectDir, StringComparison.OrdinalIgnoreCase)
               && string.Equals(RootNamespace, other.RootNamespace, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => Equals(obj as GeneratorOptions);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + StringComparer.OrdinalIgnoreCase.GetHashCode(ProjectDir);
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(RootNamespace);
            return hash;
        }
    }
}

internal sealed class AssetRecord : IEquatable<AssetRecord>
{
    /// <summary>
    /// 不含扩展名的完整 tModLoader 资源路径，例如 <c>Apotheosis/Assets/Textures/Items/StarBlade</c>。
    /// </summary>
    public string ModAssetPath { get; }

    /// <summary>
    /// 相对于 Mod 根目录且包含扩展名的文件路径，例如 <c>Assets/Textures/Items/StarBlade.png</c>。
    /// </summary>
    public string RelativeFilePath { get; }

    /// <summary>
    /// 应用跳过规则后，用于生成嵌套 C# 静态类的有效目录段列表。
    /// </summary>
    public EquatableArray<string> DirectorySegments { get; }

    /// <summary>
    /// 该资源路径上被直接跳过的目录段（当路径被提升且发生命名冲突时用于消除歧义）。
    /// </summary>
    public EquatableArray<string> SkippedSegments { get; }

    /// <summary>
    /// 不含扩展名的文件名。
    /// </summary>
    public string FileNameWithoutExtension { get; }

    /// <summary>
    /// 包含前导点号的小写扩展名（例如 <c>.png</c>）。
    /// </summary>
    public string Extension { get; }

    /// <summary>
    /// 决定生成 C# 属性类型与 tML 加载表达式的资源类型。
    /// </summary>
    public AssetKind Kind { get; }

    public AssetRecord(
        string modAssetPath,
        string relativeFilePath,
        EquatableArray<string> directorySegments,
        EquatableArray<string> skippedSegments,
        string fileNameWithoutExtension,
        string extension,
        AssetKind kind)
    {
        ModAssetPath = modAssetPath;
        RelativeFilePath = relativeFilePath;
        DirectorySegments = directorySegments;
        SkippedSegments = skippedSegments;
        FileNameWithoutExtension = fileNameWithoutExtension;
        Extension = extension;
        Kind = kind;
    }

    public bool Equals(AssetRecord? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return Kind == other.Kind
               && string.Equals(ModAssetPath, other.ModAssetPath, StringComparison.Ordinal)
               && string.Equals(RelativeFilePath, other.RelativeFilePath, StringComparison.Ordinal)
               && string.Equals(FileNameWithoutExtension, other.FileNameWithoutExtension, StringComparison.Ordinal)
               && string.Equals(Extension, other.Extension, StringComparison.Ordinal)
               && DirectorySegments.Equals(other.DirectorySegments)
               && SkippedSegments.Equals(other.SkippedSegments);
    }

    public override bool Equals(object? obj) => Equals(obj as AssetRecord);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + (int)Kind;
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(ModAssetPath);
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(RelativeFilePath);
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(FileNameWithoutExtension);
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(Extension);
            hash = (hash * 31) + DirectorySegments.GetHashCode();
            hash = (hash * 31) + SkippedSegments.GetHashCode();
            return hash;
        }
    }
}

/// <summary>
/// 面向 Roslyn 增量生成器缓存的值相等性 <see cref="ImmutableArray{T}"/> 包装类型。
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    public static readonly EquatableArray<T> Empty = new(ImmutableArray<T>.Empty);

    private readonly ImmutableArray<T> _array;

    public EquatableArray(ImmutableArray<T> array)
    {
        _array = array.IsDefault ? ImmutableArray<T>.Empty : array;
    }

    public int Length => _array.IsDefault ? 0 : _array.Length;

    public T this[int index] => _array[index];

    public ImmutableArray<T> AsImmutableArray() => _array.IsDefault ? ImmutableArray<T>.Empty : _array;

    public bool Equals(EquatableArray<T> other)
    {
        var left = AsImmutableArray();
        var right = other.AsImmutableArray();

        if (left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
        {
            if (!left[i].Equals(right[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var arr = AsImmutableArray();
        unchecked
        {
            int hash = 17;
            for (int i = 0; i < arr.Length; i++)
            {
                hash = (hash * 31) + arr[i].GetHashCode();
            }

            return hash;
        }
    }

    public ImmutableArray<T>.Enumerator GetEnumerator() => AsImmutableArray().GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)AsImmutableArray()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)AsImmutableArray()).GetEnumerator();
}
