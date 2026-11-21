#nullable enable

namespace Apotheosis.Runtime.Foundation.Transform
{
    /// <summary>
    /// 自定义实体统一接入 <see cref="TransformNode"/> 层级树与级联清理体系的标准接口。
    /// </summary>
    public interface ITransformable
    {
        /// <summary>该实体持有的 3D 变换节点。</summary>
        TransformNode Transform { get; }

        /// <summary>该实体当前是否仍处于存活状态。</summary>
        bool IsAlive { get; }

        /// <summary>
        /// 当关联的 <see cref="TransformNode"/> 执行清理且判定应连带清理本实体时触发。
        /// </summary>
        void OnTransformCleanup(TransformCleanupReason reason);
    }
}
