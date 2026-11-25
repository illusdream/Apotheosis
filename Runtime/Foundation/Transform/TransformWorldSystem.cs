#nullable enable

using System.Collections.Generic;
using Apotheosis.Runtime.Foundation.Collections;
using Apotheosis.Runtime.Foundation.Transform.Bindings;
using Apotheosis.Runtime.Foundation.Transform.Pooling;
using Terraria.ModLoader;

namespace Apotheosis.Runtime.Foundation.Transform
{
    /// <summary>全局 3D 变换层级托管系统。</summary>
    public sealed class TransformWorldSystem : ModSystem
    {
        private static readonly object _sync = new();
        private static readonly IndexedSwapList<TransformNode> _managedNodes = new(64);
        private static readonly List<TransformNode> _iterationBuffer = new();
        private static readonly List<TransformNode> _pullBuffer = new();
        private static readonly List<TransformNode> _pushBuffer = new();
        private static volatile bool _unloading;

        /// <summary>当前已注册托管的活跃节点数量。</summary>
        public static int ManagedCount { get { lock (_sync) return _managedNodes.Count; } }

        /// <summary>将节点注册到世界托管同步队列（O(1)）。</summary>
        public static void Register(TransformNode node)
        {
            if (_unloading || node.IsDisposed)
                return;

            lock (_sync)
            {
                if (!_unloading && !node.IsDisposed)
                    _managedNodes.Add(node);
            }
        }

        /// <summary>将节点从世界托管同步队列移除（O(1) 侵入式 Swap-Remove）。</summary>
        public static void Unregister(TransformNode node)
        {
            lock (_sync)
                _managedNodes.Remove(node);
        }

        public override void Load()
        {
            _unloading = false;
        }

        public override void PostUpdateEverything()
        {
            if (_unloading)
                return;

            try
            {
                // 1. 失活检测与级联清理（复用的遍历缓冲避免 Cleanup 修改集合引发异常）
                if (!SnapshotManagedNodes(_iterationBuffer))
                    return;

                for (int i = 0; i < _iterationBuffer.Count; i++)
                {
                    TransformNode node = _iterationBuffer[i];
                    if (node.IsDisposed)
                        continue;

                    ITransformBinding? binding = node.Binding;
                    if (binding is not null && !binding.IsAlive)
                        node.Cleanup(TransformCleanupReason.TargetDestroyed);
                }

                // 2. 分流 Pull / Push 节点
                if (!SnapshotManagedNodes(_iterationBuffer))
                    return;

                bool pullHasHierarchy = false;
                for (int i = 0; i < _iterationBuffer.Count; i++)
                {
                    TransformNode node = _iterationBuffer[i];
                    if (node.IsDisposed || node.Binding is null)
                        continue;

                    switch (node.Binding.SyncMode)
                    {
                        case TransformSyncMode.Pull:
                            _pullBuffer.Add(node);
                            if (node.Depth > 0)
                                pullHasHierarchy = true;
                            break;
                        case TransformSyncMode.Push:
                            _pushBuffer.Add(node);
                            break;
                    }
                }

                // Pull 阶段：存在父子嵌套时按 Depth 升序，避免子节点基于父节点旧帧矩阵反算局部变换
                if (pullHasHierarchy && _pullBuffer.Count > 1)
                    _pullBuffer.Sort(static (a, b) => a.Depth.CompareTo(b.Depth));

                for (int i = 0; i < _pullBuffer.Count; i++)
                {
                    TransformNode node = _pullBuffer[i];
                    if (!node.IsDisposed)
                        node.SyncFromTarget();
                }

                // 3. Push 阶段：按层级深度从父到子将世界变换写回受控实体
                if (_pushBuffer.Count > 1)
                    _pushBuffer.Sort(static (a, b) => a.Depth.CompareTo(b.Depth));

                for (int i = 0; i < _pushBuffer.Count; i++)
                {
                    TransformNode node = _pushBuffer[i];
                    if (!node.IsDisposed)
                        node.ApplyToTarget();
                }
            }
            finally
            {
                _iterationBuffer.Clear();
                _pullBuffer.Clear();
                _pushBuffer.Clear();
            }
        }

        public override void OnWorldUnload()
        {
            CleanupAllManaged(TransformCleanupReason.WorldUnload);
        }

        public override void Unload()
        {
            _unloading = true;
            CleanupAllManaged(TransformCleanupReason.WorldUnload);
            TransformNodePool.ClearAll();
            TransformBindingPools.ClearAll();
        }

        private static bool SnapshotManagedNodes(List<TransformNode> destination)
        {
            lock (_sync)
            {
                if (_managedNodes.Count == 0)
                {
                    destination.Clear();
                    return false;
                }

                _managedNodes.CopyTo(destination);
                return true;
            }
        }

        private static void CleanupAllManaged(TransformCleanupReason reason)
        {
            // 使用局部快照，避免与主线程 PostUpdateEverything 争用静态 _iterationBuffer
            List<TransformNode> snapshot = new();
            if (!SnapshotManagedNodes(snapshot))
                return;

            for (int i = 0; i < snapshot.Count; i++)
            {
                TransformNode node = snapshot[i];
                if (!node.IsDisposed)
                    node.Cleanup(reason);
            }

            lock (_sync)
                _managedNodes.Clear();
        }
    }
}
