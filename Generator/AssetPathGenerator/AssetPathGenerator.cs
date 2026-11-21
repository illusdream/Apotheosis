using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Apotheosis.Generator.AssetPathGenerator;

/// <summary>
/// 增量 Roslyn 源码生成器：扫描配置的 Mod 资源目录，
/// 并生成带有懒加载 tModLoader 资源属性与路径常量的嵌套静态类。
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class AssetPathGenerator : IIncrementalGenerator
{
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
            .Select(static (items, _) => new EquatableArray<AssetRecord>(items));

        var generationInput = optionsProvider.Combine(recordsProvider);

        context.RegisterSourceOutput(generationInput, static (spc, input) =>
        {
            var (options, records) = input;
            string source = AssetCodeEmitter.Emit(options.RootNamespace, records);
            spc.AddSource("Assets.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }
}
