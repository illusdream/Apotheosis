#nullable enable

using System;

namespace Apotheosis.Runtime.Foundation.Transform
{
    /// <summary>
    /// 触发 <see cref="TransformNode.Cleanup"/> 的原因分类。
    /// </summary>
    public enum TransformCleanupReason
    {
        /// <summary>由业务代码显式手动调用清理。</summary>
        Manual = 0,

        /// <summary>由父节点被清理时级联触发。</summary>
        ParentCleaned = 1,

        /// <summary>由宿主死亡钩子或托管系统检测到目标实体失活而触发。</summary>
        TargetDestroyed = 2,

        /// <summary>由世界卸载或模组卸载统一清理触发。</summary>
        WorldUnload = 3
    }

    /// <summary>
    /// 节点与关联实体之间的自动同步方向。
    /// </summary>
    public enum TransformSyncMode
    {
        /// <summary>不参与每帧自动同步，由调用方按需显式调用解析器。</summary>
        Manual = 0,

        /// <summary>从实体读取变换写入节点（实体驱动节点，常用于挂载宿主）。</summary>
        Pull = 1,

        /// <summary>将节点的世界变换写回实体（节点驱动实体，常用于附属挂件/子物体）。</summary>
        Push = 2
    }

    /// <summary>
    /// 控制解析器读写哪些空间变换分量。
    /// </summary>
    [Flags]
    public enum TransformSyncFlags
    {
        /// <summary>不同步任何分量。</summary>
        None = 0,

        /// <summary>同步位置（2D <c>Center</c>）。</summary>
        Position = 1 << 0,

        /// <summary>同步旋转（<c>rotation</c> / <c>fullRotation</c>）。</summary>
        Rotation = 1 << 1,

        /// <summary>同步缩放大小（<c>scale</c>）。</summary>
        Scale = 1 << 2,

        /// <summary>同步朝向/翻转（<c>direction</c> / <c>spriteDirection</c> / <c>gravDir</c>）。</summary>
        Facing = 1 << 3,

        /// <summary>仅同步位置与旋转。</summary>
        PositionAndRotation = Position | Rotation,

        /// <summary>同步全部支持的分量。</summary>
        All = Position | Rotation | Scale | Facing
    }
}
