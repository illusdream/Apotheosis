#nullable enable

namespace Apotheosis.Runtime.Foundation.Transform.Bindings
{
    /// <summary>
    /// <see cref="TransformNode"/> 与具体目标实体之间的生命周期绑定契约。
    /// <para>
    /// 负责同步模式、存活/防槽位复用校验、调用显式解析器读写数据，以及在节点清理时执行实体清理与绑定回收。
    /// </para>
    /// </summary>
    public interface ITransformBinding
    {
        /// <summary>当前关联的目标实体对象。</summary>
        object? Target { get; }

        /// <summary>自动同步方向（<see cref="TransformSyncMode.Pull"/> / <see cref="TransformSyncMode.Push"/> / <see cref="TransformSyncMode.Manual"/>）。</summary>
        TransformSyncMode SyncMode { get; set; }

        /// <summary>参与同步的空间分量掩码。</summary>
        TransformSyncFlags SyncFlags { get; set; }

        /// <summary>关联实体当前是否仍存活有效且未被槽位替换。</summary>
        bool IsAlive { get; }

        /// <summary>在节点未显式重写清理判定时，默认是否应连带清理关联实体。</summary>
        bool DefaultCleanupTargetOnDispose { get; }

        /// <summary>执行一次 Pull：通过解析器从目标实体读取变换写入节点。</summary>
        void SyncFromTarget(TransformNode node);

        /// <summary>执行一次 Push：通过解析器将节点世界变换写回目标实体。</summary>
        void ApplyToTarget(TransformNode node);

        /// <summary>执行关联目标实体的清理/销毁逻辑。</summary>
        void CleanupTarget(TransformCleanupReason reason);

        /// <summary>解除绑定时释放引用；若为池化绑定则清空状态并自动归还对象池。</summary>
        void Release();
    }
}
