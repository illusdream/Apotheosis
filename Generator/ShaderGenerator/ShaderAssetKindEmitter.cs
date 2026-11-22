using System.Collections.Generic;
using System.Text;
using Apotheosis.Generator.AssetPathGenerator;

namespace Apotheosis.Generator.ShaderGenerator;

/// <summary>
/// 针对 <see cref="AssetKind.Effect"/> 的可插拔资源发射器：
/// 将着色器的按需加载入口（调用 <c>RenderManager.Shaders.GetOrLoad</c> 并返回强类型包装）封装在 Shader 模块自身，
/// 彻底避免污染通用的 <see cref="AssetCodeEmitter"/>。
/// </summary>
internal sealed class ShaderAssetKindEmitter : IAssetKindEmitter
{
    private const string RenderManagerType = "global::Apotheosis.Runtime.Rendering.RenderManager";

    private readonly IReadOnlyDictionary<string, string> _shaderTypeByAssetPath;

    public ShaderAssetKindEmitter(IReadOnlyDictionary<string, string> shaderTypeByAssetPath)
    {
        _shaderTypeByAssetPath = shaderTypeByAssetPath;
    }

    public void EmitMembers(
        StringBuilder sb,
        string indent,
        AssetRecord asset,
        string propName,
        string pathConstName,
        string fieldName,
        string xmlSafePath)
    {
        string shaderStructType = _shaderTypeByAssetPath[asset.ModAssetPath];

        sb.Append(indent).Append("private static ").Append(shaderStructType).Append(' ').Append(fieldName).AppendLine(";");
        sb.Append(indent).Append("/// <summary>按需加载受管强类型着色器：<c>").Append(xmlSafePath).AppendLine("</c></summary>");
        sb.Append(indent).Append("public static ").Append(shaderStructType).Append(' ').AppendLine(propName);
        sb.Append(indent).AppendLine("{");
        sb.Append(indent).AppendLine("    get");
        sb.Append(indent).AppendLine("    {");
        sb.Append(indent).Append("        var cached = ").Append(fieldName).AppendLine(";");
        sb.Append(indent).AppendLine("        if (!cached.Handle.IsValid)");
        sb.Append(indent).AppendLine("        {");
        sb.Append(indent).Append("            cached = new ").Append(shaderStructType).Append('(')
          .Append(RenderManagerType).Append(".Shaders.GetOrLoad(").Append(pathConstName).Append(", ")
          .Append(shaderStructType).AppendLine(".ParameterNames));");
        sb.Append(indent).Append("            ").Append(fieldName).AppendLine(" = cached;");
        sb.Append(indent).AppendLine("        }");
        sb.Append(indent).AppendLine("        return cached;");
        sb.Append(indent).AppendLine("    }");
        sb.Append(indent).AppendLine("}");
    }

    public void EmitUnload(StringBuilder sb, string indent, AssetRecord asset, string fieldName)
    {
        sb.Append(indent).Append(fieldName).AppendLine(" = default;");
    }
}
