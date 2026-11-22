using System;

namespace Apotheosis.Generator.ShaderGenerator;

using Apotheosis.Generator.AssetPathGenerator;

internal sealed class ShaderParamDescriptor : IEquatable<ShaderParamDescriptor>
{
    public string Name { get; }
    public string Semantic { get; }
    public uint ParamType { get; }
    public uint ParamClass { get; }
    public uint Rows { get; }
    public uint Columns { get; }
    public uint Elements { get; }

    public ShaderParamDescriptor(
        string name,
        string semantic,
        uint paramType,
        uint paramClass,
        uint rows,
        uint columns,
        uint elements)
    {
        Name = name;
        Semantic = semantic;
        ParamType = paramType;
        ParamClass = paramClass;
        Rows = rows;
        Columns = columns;
        Elements = elements;
    }

    public bool Equals(ShaderParamDescriptor? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return ParamType == other.ParamType
               && ParamClass == other.ParamClass
               && Rows == other.Rows
               && Columns == other.Columns
               && Elements == other.Elements
               && string.Equals(Name, other.Name, StringComparison.Ordinal)
               && string.Equals(Semantic, other.Semantic, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => Equals(obj as ShaderParamDescriptor);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(Name);
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(Semantic);
            hash = (hash * 31) + (int)ParamType;
            hash = (hash * 31) + (int)ParamClass;
            hash = (hash * 31) + (int)Rows;
            hash = (hash * 31) + (int)Columns;
            hash = (hash * 31) + (int)Elements;
            return hash;
        }
    }
}

internal sealed class ShaderTechniqueDescriptor : IEquatable<ShaderTechniqueDescriptor>
{
    public string Name { get; }
    public EquatableArray<string> Passes { get; }

    public ShaderTechniqueDescriptor(string name, EquatableArray<string> passes)
    {
        Name = name;
        Passes = passes;
    }

    public bool Equals(ShaderTechniqueDescriptor? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return string.Equals(Name, other.Name, StringComparison.Ordinal)
               && Passes.Equals(other.Passes);
    }

    public override bool Equals(object? obj) => Equals(obj as ShaderTechniqueDescriptor);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(Name);
            hash = (hash * 31) + Passes.GetHashCode();
            return hash;
        }
    }
}

internal sealed class ShaderMetadataRecord : IEquatable<ShaderMetadataRecord>
{
    public string ModAssetPath { get; }
    public EquatableArray<ShaderParamDescriptor> Parameters { get; }
    public EquatableArray<ShaderTechniqueDescriptor> Techniques { get; }

    public ShaderMetadataRecord(
        string modAssetPath,
        EquatableArray<ShaderParamDescriptor> parameters,
        EquatableArray<ShaderTechniqueDescriptor> techniques)
    {
        ModAssetPath = modAssetPath;
        Parameters = parameters;
        Techniques = techniques;
    }

    public bool Equals(ShaderMetadataRecord? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return string.Equals(ModAssetPath, other.ModAssetPath, StringComparison.OrdinalIgnoreCase)
               && Parameters.Equals(other.Parameters)
               && Techniques.Equals(other.Techniques);
    }

    public override bool Equals(object? obj) => Equals(obj as ShaderMetadataRecord);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + StringComparer.OrdinalIgnoreCase.GetHashCode(ModAssetPath);
            hash = (hash * 31) + Parameters.GetHashCode();
            hash = (hash * 31) + Techniques.GetHashCode();
            return hash;
        }
    }
}
