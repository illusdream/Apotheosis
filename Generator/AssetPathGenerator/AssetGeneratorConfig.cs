using System;

namespace Apotheosis.Generator.AssetPathGenerator;

/// <summary>
/// <see cref="AssetPathGenerator"/> 的配置项。
/// </summary>
internal static class AssetGeneratorConfig
{
    /// <summary>
    /// 在 Mod 根命名空间下生成的根静态类名（例如 <c>Apotheosis.Assets</c>）。
    /// </summary>
    public const string RootClassName = "Assets";

    /// <summary>
    /// 需要扫描资源的顶层相对目录（相对于 Mod 根目录）。
    /// 请使用正斜杠（例如 <c>"Assets"</c>、<c>"Content"</c>、<c>"Content/Textures"</c>）。
    /// 留空则会扫描整个 Mod 根目录（仍会排除 <see cref="ExcludedDirectoryNames"/>）。
    /// </summary>
    public static readonly string[] TargetRootDirectories =
    {
        "Assets",
    };

    /// <summary>
    /// 按精确的 Mod 相对路径跳过/折叠对应的嵌套类层级，将其内部资源提升到父级类中。
    /// <para>
    /// 示例：跳过 <c>"Assets"</c> 会将 <c>Assets/Textures/Sword.png</c> 生成为
    /// <c>Apotheosis.Assets.Textures.Sword</c>，而不是 <c>Apotheosis.Assets.Assets.Textures.Sword</c>；
    /// 跳过 <c>"Assets/Textures/Items"</c> 则会将其中的文件直接提升到 <c>Apotheosis.Assets.Textures</c>。
    /// </para>
    /// </summary>
    public static readonly string[] SkippedDirectoryPaths =
    {
        "Assets",
    };

    /// <summary>
    /// 在任意层级下都会跳过/折叠的目录名称（单段目录名），将其内部资源提升到父级类中。
    /// 示例：添加 <c>"Common"</c> 或 <c>"Misc"</c> 会折叠任意位置的同名文件夹。
    /// </summary>
    public static readonly string[] SkippedDirectoryNames = Array.Empty<string>();

    /// <summary>
    /// 在任意层级下完全排除出资源扫描的目录名称（单段目录名）。
    /// </summary>
    public static readonly string[] ExcludedDirectoryNames =
    {
        "bin",
        "obj",
        "Generator",
        "Generated",
        "Properties",
        "Localization",
        ".git",
        ".idea",
        ".vs",
    };

    /// <summary>
    /// 完全排除出资源生成的文件名（或 Mod 相对文件路径）。
    /// </summary>
    public static readonly string[] ExcludedFiles =
    {
        "icon.png",
        "icon_small.png",
    };

    /// <summary>
    /// 用于将 <c>.xnb</c> 文件识别为 <c>DynamicSpriteFont</c> 字体资源的目录段名称。
    /// </summary>
    public static readonly string[] FontDirectoryNames =
    {
        "Font",
        "Fonts",
    };

    /// <summary>
    /// 用于将 <c>.xnb</c> 文件识别为 <c>Effect</c> 着色器资源的目录段名称。
    /// </summary>
    public static readonly string[] ShaderDirectoryNames =
    {
        "Effect",
        "Effects",
        "Shader",
        "Shaders",
    };
}
