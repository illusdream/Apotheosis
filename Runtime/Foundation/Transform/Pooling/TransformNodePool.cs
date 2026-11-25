#nullable enable

using System;
using System.Collections.Generic;
using Apotheosis.Runtime.Foundation.Collections;

namespace Apotheosis.Runtime.Foundation.Transform.Pooling
{
    /// <summary><see cref="TransformNode"/> 基类对象池与全局池注册中心。</summary>
    public static class TransformNodePool
    {
        /// <summary>默认最大池容量，超出后归还的节点交由 GC 回收。</summary>
        public const int DefaultMaxPoolSize = 1024;

        private static readonly object _poolLock = new();
        private static readonly object _handlersLock = new();
        private static readonly BoundedObjectPool<TransformNode> _pool = new(64, DefaultMaxPoolSize);
        private static readonly List<Action> _clearHandlers = new();
        private static readonly Action<TransformNode> _returnDelegate = ReturnInternal;

        /// <summary>基类节点池的最大缓存数量。</summary>
        public static int MaxPoolSize
        {
            get { lock (_poolLock) return _pool.MaxSize; }
            set { lock (_poolLock) _pool.MaxSize = value; }
        }

        /// <summary>当前池中闲置的基类节点数量。</summary>
        public static int Count { get { lock (_poolLock) return _pool.Count; } }

        /// <summary>从基类对象池借出一个干净的 <see cref="TransformNode"/>。</summary>
        public static TransformNode Rent(string? name = null) => Rent(Transform3D.Identity, name);

        /// <summary>从基类对象池借出一个指定初始局部变换的 <see cref="TransformNode"/>。</summary>
        public static TransformNode Rent(Transform3D initialTransform, string? name = null)
        {
            TransformNode? node;
            lock (_poolLock)
            {
                if (_pool.TryPop(out node))
                    node.InPool = false;
            }

            node ??= new TransformNode();
            node.InitializeFromPool(_returnDelegate, initialTransform, name);
            return node;
        }

        /// <summary>清空基类节点池。</summary>
        public static void Clear()
        {
            lock (_poolLock)
                _pool.Clear();
        }

        /// <summary>清空基类池以及所有已注册的泛型子类池。</summary>
        public static void ClearAll()
        {
            Clear();

            Action[] handlers;
            lock (_handlersLock)
                handlers = _clearHandlers.ToArray();

            for (int i = 0; i < handlers.Length; i++)
                handlers[i]();
        }

        internal static void RegisterClearHandler(Action clearHandler)
        {
            lock (_handlersLock)
                _clearHandlers.Add(clearHandler);
        }

        private static void ReturnInternal(TransformNode node)
        {
            // 仅收纳精确基类，避免自定义子类污染基类池
            if (node.GetType() != typeof(TransformNode))
                return;

            lock (_poolLock)
            {
                if (node.InPool)
                    return;

                node.InPool = true;
                _pool.Return(node);
            }
        }
    }

    /// <summary>自定义 <see cref="TransformNode"/> 子类的独立泛型对象池。</summary>
    public static class TransformNodePool<TNode> where TNode : TransformNode, new()
    {
        private static readonly object _poolLock = new();
        private static readonly BoundedObjectPool<TNode> _pool = new(16, TransformNodePool.DefaultMaxPoolSize);
        private static readonly Action<TransformNode> _returnDelegate = ReturnInternal;

        /// <summary>该子类池的最大缓存数量。</summary>
        public static int MaxPoolSize
        {
            get { lock (_poolLock) return _pool.MaxSize; }
            set { lock (_poolLock) _pool.MaxSize = value; }
        }

        /// <summary>当前池中闲置的该子类节点数量。</summary>
        public static int Count { get { lock (_poolLock) return _pool.Count; } }

        static TransformNodePool() => TransformNodePool.RegisterClearHandler(Clear);

        /// <summary>从该子类池借出一个实例。</summary>
        public static TNode Rent(string? name = null) => Rent(Transform3D.Identity, name);

        /// <summary>从该子类池借出一个带指定初始局部变换的实例。</summary>
        public static TNode Rent(Transform3D initialTransform, string? name = null)
        {
            TNode? node;
            lock (_poolLock)
            {
                if (_pool.TryPop(out node))
                    node.InPool = false;
            }

            // 池空时在锁外构造用户子类，避免持锁执行自定义构造函数引发跨池锁序风险
            node ??= new TNode();
            node.InitializeFromPool(_returnDelegate, initialTransform, name);
            return node;
        }

        /// <summary>清空该子类对象池。</summary>
        public static void Clear()
        {
            lock (_poolLock)
                _pool.Clear();
        }

        private static void ReturnInternal(TransformNode node)
        {
            if (node is not TNode typed || node.GetType() != typeof(TNode))
                return;

            lock (_poolLock)
            {
                if (typed.InPool)
                    return;

                typed.InPool = true;
                _pool.Return(typed);
            }
        }
    }
}
