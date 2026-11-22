using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Apotheosis.Generator.ShaderGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Apotheosis.Generator.AssetPathGenerator;

/// <summary>
/// 增量 Roslyn 源码生成器：扫描配置的 Mod 资源目录，并通过可插拔的 <see cref="IAssetKindEmitter"/>
/// 组装生成 <c>Assets.g.cs</c> 及各资源子系统的强类型包装代码（如 <c>Shaders.g.cs</c>）。
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class AssetPathGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor MissingShaderMetadataDiagnostic = new(
        id: "APOSH100",
        title: "Missing shader metadata",
        messageFormat: "Shader asset '{0}' has no matching '.shader.meta.json' file; strongly-typed parameter setters were omitted. Ensure the PrepareShaders MSBuild target ran cleanly.",
        category: "Apotheosis.ShaderGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var optionsProvider = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) =>
        {
            provider.GlobalOptions.TryGetValue("build_property.ProjectDir", out string? projectDir);
            provider.GlobalOptions.TryGetValue("build_property.RootNamespace", out string? rootNamespace);

            return new GeneratorOptions(
                projectDir ?? string.Empty,
                string.IsNullOrWhiteSpace(rootNamespace) ? "Apotheosis" : rootNamespace!);
        });

        var recordsProvider = context.AdditionalTextsProvider
            .Select(static (file, _) => file.Path)
            .Combine(optionsProvider)
            .Select(static (pair, _) => AssetPathResolver.TryCreateAssetRecord(pair.Left, pair.Right))
            .Where(static record => record is not null)
            .Select(static (record, _) => record!)
            .Collect()
            .Select(static (items, _) => AssetCodeEmitter.DeduplicateRecords(items));

        var shaderMetaProvider = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".shader.meta.json", StringComparison.OrdinalIgnoreCase))
            .Select(static (file, ct) => ShaderMetadataParser.TryParse(file.GetText(ct)?.ToString()))
            .Where(static meta => meta is not null)
            .Select(static (meta, _) => meta!)
            .Collect()
            .Select(static (items, _) =>
            {
                if (items.IsDefaultOrEmpty)
                    return EquatableArray<ShaderMetadataRecord>.Empty;

                var ordered = items
                    .OrderBy(static m => m.ModAssetPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static m => m.ModAssetPath, StringComparer.Ordinal)
                    .ToImmutableArray();
                return new EquatableArray<ShaderMetadataRecord>(ordered);
            });

        // 拆分两条独立输出管线：Assets.g.cs 不依赖 .shader.meta.json，
        // 编辑 .fx 参数时仅增量重生成 Shaders.g.cs。
        var assetsInput = optionsProvider.Combine(recordsProvider);
        context.RegisterSourceOutput(assetsInput, static (spc, input) =>
        {
            var (options, deduplicated) = input;
            var shaderTypeByPath = ShaderCodeEmitter.ResolveShaderTypes(options.RootNamespace, deduplicated);

            var customEmitters = new Dictionary<AssetKind, IAssetKindEmitter>
            {
                [AssetKind.Effect] = new ShaderAssetKindEmitter(shaderTypeByPath),
            };

            string assetsSource = AssetCodeEmitter.Emit(
                options.RootNamespace,
                deduplicated,
                customEmitters);
            spc.AddSource("Assets.g.cs", SourceText.From(assetsSource, Encoding.UTF8));
        });

        var shadersInput = optionsProvider
            .Combine(recordsProvider)
            .Combine(shaderMetaProvider);

        context.RegisterSourceOutput(shadersInput, static (spc, input) =>
        {
            var ((options, deduplicated), shaderMetas) = input;

            var (shadersSource, shaderCount, missingMetadataPaths) = ShaderCodeEmitter.Emit(
                options.RootNamespace,
                deduplicated,
                shaderMetas);

            if (shaderCount > 0)
            {
                spc.AddSource("Shaders.g.cs", SourceText.From(shadersSource, Encoding.UTF8));
            }

            for (int i = 0; i < missingMetadataPaths.Count; i++)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    MissingShaderMetadataDiagnostic,
                    Location.None,
                    missingMetadataPaths[i]));
            }
        });
    }
}
