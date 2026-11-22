using System.Text;

namespace Apotheosis.Generator.AssetPathGenerator;

/// <summary>
/// 资源类型发射扩展接口：将特定 <see cref="AssetKind"/> 的字段声明、加载属性与卸载语句从通用的
/// <see cref="AssetCodeEmitter"/> 中解耦，避免公共树状资源生成器侵入具体子系统（如 Shader、Atlas 等）的实现细节。
/// </summary>
internal interface IAssetKindEmitter
{
    /// <summary>发射资源后备字段与懒加载属性（路径常量已由 <see cref="AssetCodeEmitter"/> 统一发射）。</summary>
    void EmitMembers(
        StringBuilder sb,
        string indent,
        AssetRecord asset,
        string propName,
        string pathConstName,
        string fieldName,
        string xmlSafePath);

    /// <summary>发射该资源在 <c>__Unload()</c> 中的清理语句。</summary>
    void EmitUnload(
        StringBuilder sb,
        string indent,
        AssetRecord asset,
        string fieldName);
}
