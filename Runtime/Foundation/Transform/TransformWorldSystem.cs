#nullable enable

using System.Collections.Generic;
using Apotheosis.Runtime.Foundation.Transform.Bindings;
using Apotheosis.Runtime.Foundation.Transform.Pooling;
using Terraria.ModLoader;

namespace Apotheosis.Runtime.Foundation.Transform
{
    /// <summary>
    /// 全局 3D 变换层级托管系统：负责每帧检测失活实体并级联清理子树、按 <c>Pull → Push</c> 顺序同步绑定节点，
    /// 以及在世界/模组卸载时清空全部节点池与绑定池。
    /// </summary>
    public sealed class TransformWorldSystem : ModSystem
    {
        private static readonly List<TransformNode> _managedNodes = new();
        private static readonly List<TransformNode> _iterationBuffer = new();
        private static readonly List<TransformNode> _pushBuffer = new();

        /// <summary>当前已注册托管的活跃节点数量。</summary>
        public static int ManagedCount => _managedNodes.Count;

        /// <summary>将节点注册到世界托管同步队列。</summary>
        public static void Register(TransformNode node)
        {
            if (node.IsDisposed || node.IsRegisteredInWorld)
                return;

            node.IsRegisteredInWorld = true;
            _managedNodes.Add(node);
        }

        /// <summary>将节点从世界托管同步队列移除（O(1) Swap-Remove）。</summary>
        public static void Unregister(TransformNode node)
        {
            if (!node.IsRegisteredInWorld)
                return;

            node.IsRegisteredInWorld = false;
            int index = _managedNodes.IndexOf(node);
            if (index >= 0)
            {
                int last = _managedNodes.Count - 1;
                if (index != last)
                {
                    _managedNodes[index] = _managedNodes[last];
                }

                _managedNodes.RemoveAt(last);
            }
        }

        public override void PostUpdateEverything()
        {
            if (_managedNodes.Count == 0)
                return;

            // 1. 失活检测与级联清理（复用的遍历缓冲避免 Cleanup 修改集合引发异常）
            SnapshotManagedNodes();
            for (int i = 0; i < _iterationBuffer.Count; i++)
            {
                TransformNode node = _iterationBuffer[i];
                if (node.IsDisposed)
                    continue;

                ITransformBinding? binding = node.Binding;
                if (binding is not null && !binding.IsAlive)
                {
                    node.Cleanup(TransformCleanupReason.TargetDestroyed);
                }
            }

            if (_managedNodes.Count == 0)
            {
                _iterationBuffer.Clear();
                return;
            }

            // 2. Pull 阶段：先从宿主实体（如 Player / 原版 NPC）拉取本帧最新空间状态
            _pushBuffer.Clear();
            SnapshotManagedNodes();
            for (int i = 0; i < _iterationBuffer.Count; i++)
            {
                TransformNode node = _iterationBuffer[i];
                if (node.IsDisposed || node.Binding is null)
                    continue;

                switch (node.Binding.SyncMode)
                {
                    case TransformSyncMode.Pull:
                        node.SyncFromTarget();
                        break;
                    case TransformSyncMode.Push:
                        _pushBuffer.Add(node);
                        break;
                }
            }

            // 3. Push 阶段：按层级深度从父到子将世界变换写回受控实体
            if (_pushBuffer.Count > 1)
            {
                _pushBuffer.Sort(static (a, b) => a.Depth.CompareTo(b.Depth));
            }

            for (int i = 0; i < _pushBuffer.Count; i++)
            {
                TransformNode node = _pushBuffer[i];
                if (!node.IsDisposed)
                {
                    node.ApplyToTarget();
                }
            }

            _iterationBuffer.Clear();
            _pushBuffer.Clear();
        }

        public override void OnWorldUnload()
        {
            CleanupAllManaged(TransformCleanupReason.WorldUnload);
        }

        public override void Unload()
        {
            CleanupAllManaged(TransformCleanupReason.WorldUnload);
            TransformNodePool.ClearAll();
            TransformBindingPools.ClearAll();
        }

        private static void SnapshotManagedNodes()
        {
            _iterationBuffer.Clear();
            _iterationBuffer.AddRange(_managedNodes);
        }

        private static void CleanupAllManaged(TransformCleanupReason reason)
        {
            if (_managedNodes.Count == 0)
            {
                _iterationBuffer.Clear();
                _pushBuffer.Clear();
                return;
            }

            SnapshotManagedNodes();
            for (int i = 0; i < _iterationBuffer.Count; i++)
            {
                TransformNode node = _iterationBuffer[i];
                if (!node.IsDisposed)
                {
                    node.Cleanup(reason);
                }
            }

            _managedNodes.Clear();
            _iterationBuffer.Clear();
            _pushBuffer.Clear();
        }
    }
}
