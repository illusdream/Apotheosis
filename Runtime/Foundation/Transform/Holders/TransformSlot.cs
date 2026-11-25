#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using Apotheosis.Runtime.Foundation.Transform.Pooling;

namespace Apotheosis.Runtime.Foundation.Transform.Holders
{
    /// <summary>嵌入各实体宿主的轻量值类型变换槽位，统一管理句柄校验、懒借出、替换与清理。</summary>
    internal struct TransformSlot
    {
        private TransformHandle _handle;

        public readonly bool HasTransform => _handle.IsValid;

        public readonly bool TryGet([NotNullWhen(true)] out TransformNode? node) => _handle.TryGetNode(out node);

        public TransformNode GetOrCreate<TState>(in TState state, Func<TState, string?> getDefaultName, Action<TransformNode, TState> bindAction)
        {
            if (_handle.TryGetNode(out TransformNode? existing))
                return existing;

            return CreateAndBind(in state, getDefaultName, bindAction);
        }

        public TransformNode GetOrCreate<TState>(TransformSyncMode syncMode, TransformSyncFlags syncFlags,
            bool forceRebind, in TState state, Func<TState, string?> getDefaultName, Action<TransformNode, TState> bindAction)
        {
            if (_handle.TryGetNode(out TransformNode? existing))
            {
                if (existing.Binding is null || forceRebind)
                {
                    bindAction(existing, state);
                }
                else
                {
                    existing.Binding.SyncMode = syncMode;
                    existing.Binding.SyncFlags = syncFlags;
                }

                return existing;
            }

            return CreateAndBind(in state, getDefaultName, bindAction);
        }

        public TNode Set<TNode, TState>(TNode customNode, bool syncInitialFromTarget, in TState state, Action<TransformNode, TState> bindAction)
            where TNode : TransformNode
        {
            ArgumentNullException.ThrowIfNull(customNode);
            ObjectDisposedException.ThrowIf(customNode.IsDisposed, customNode);

            if (_handle.TryGetNode(out TransformNode? previous) && !ReferenceEquals(previous, customNode))
            {
                // 先清句柄再清理旧节点，防止 OnCleanup 重入回调读到正被销毁的旧句柄
                _handle = default;
                previous.DetachBinding();
                previous.Cleanup(TransformCleanupReason.Manual);
            }

            bindAction(customNode, state);
            if (syncInitialFromTarget)
                customNode.SyncFromTarget();

            _handle = customNode.GetHandle();
            return customNode;
        }

        public void CleanupAndClear(TransformCleanupReason reason, bool detachBeforeCleanup = false)
        {
            if (_handle.TryGetNode(out TransformNode? node))
            {
                _handle = default;
                if (detachBeforeCleanup)
                    node.DetachBinding();
                node.Cleanup(reason);
            }
        }

        public void ResetHandle() => _handle = default;

        private TransformNode CreateAndBind<TState>(in TState state, Func<TState, string?> getDefaultName, Action<TransformNode, TState> bindAction)
        {
            TransformNode node = TransformNodePool.Rent(getDefaultName(state));
            try
            {
                bindAction(node, state);
                node.SyncFromTarget();
                _handle = node.GetHandle();
                return node;
            }
            catch
            {
                node.Cleanup(TransformCleanupReason.Manual);
                throw;
            }
        }
    }
}
