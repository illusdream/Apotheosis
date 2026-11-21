#nullable enable

using System;
using System.Collections.Generic;

namespace Apotheosis.Runtime.Foundation.Transform.Pooling
{
    /// <summary>
    /// <see cref="TransformNode"/> 基类对象池与全局池注册中心。
    /// <para>
    /// 仅收纳基类 <see cref="TransformNode"/> 实例，避免自定义子类污染基类池；
    /// 自定义子类请使用 <see cref="TransformNodePool{TNode}"/>。
    /// </para>
    /// </summary>
    public static class TransformNodePool
    {
        /// <summary>默认最大池容量，超出后归还的节点交由 GC 回收。</summary>
        public const int DefaultMaxPoolSize = 1024;

        private static readonly Stack<TransformNode> _pool = new(64);
        private static readonly List<Action> _clearHandlers = new();
        private static readonly Action<TransformNode> _returnDelegate = ReturnInternal;

        /// <summary>基类节点池的最大缓存数量。</summary>
        public static int MaxPoolSize { get; set; } = DefaultMaxPoolSize;

        /// <summary>当前池中闲置的基类节点数量。</summary>
        public static int Count => _pool.Count;

        /// <summary>从基类对象池借出一个干净的 <see cref="TransformNode"/>。</summary>
        public static TransformNode Rent(string? name = null)
            => Rent(Transform3D.Identity, name);

        /// <summary>从基类对象池借出一个指定初始局部变换的 <see cref="TransformNode"/>。</summary>
        public static TransformNode Rent(Transform3D initialTransform, string? name = null)
        {
            TransformNode node = _pool.Count > 0 ? _pool.Pop() : new TransformNode();
            node.InitializeFromPool(_returnDelegate, initialTransform, name);
            return node;
        }

        /// <summary>清空基类节点池。</summary>
        public static void Clear() => _pool.Clear();

        /// <summary>清空基类池以及所有已注册的泛型子类池（在模组卸载时由 <see cref="TransformWorldSystem"/> 自动调用）。</summary>
        public static void ClearAll()
        {
            _pool.Clear();
            for (int i = 0; i < _clearHandlers.Count; i++)
            {
                _clearHandlers[i]();
            }
        }

        internal static void RegisterClearHandler(Action clearHandler)
        {
            _clearHandlers.Add(clearHandler);
        }

        private static void ReturnInternal(TransformNode node)
        {
            if (node.InPool || node.GetType() != typeof(TransformNode))
                return;

            node.InPool = true;
            if (_pool.Count < MaxPoolSize)
            {
                _pool.Push(node);
            }
        }
    }

    /// <summary>
    /// 自定义 <see cref="TransformNode"/> 子类的独立泛型对象池。
    /// <para>
    /// 每个子类类型拥有完全隔离的静态池，绝不会与基类或其他子类混用；
    /// 子类可重写 <see cref="TransformNode.OnResetForPool"/> 在回池时清理自身扩展状态。
    /// </para>
    /// </summary>
    public static class TransformNodePool<TNode> where TNode : TransformNode, new()
    {
        private static readonly Stack<TNode> _pool = new(16);
        private static readonly Action<TransformNode> _returnDelegate = ReturnInternal;

        /// <summary>该子类池的最大缓存数量。</summary>
        public static int MaxPoolSize { get; set; } = TransformNodePool.DefaultMaxPoolSize;

        /// <summary>当前池中闲置的该子类节点数量。</summary>
        public static int Count => _pool.Count;

        static TransformNodePool()
        {
            TransformNodePool.RegisterClearHandler(Clear);
        }

        /// <summary>从该子类池借出一个实例。</summary>
        public static TNode Rent(string? name = null)
            => Rent(Transform3D.Identity, name);

        /// <summary>从该子类池借出一个带指定初始局部变换的实例。</summary>
        public static TNode Rent(Transform3D initialTransform, string? name = null)
        {
            TNode node = _pool.Count > 0 ? _pool.Pop() : new TNode();
            node.InitializeFromPool(_returnDelegate, initialTransform, name);
            return node;
        }

        /// <summary>清空该子类对象池。</summary>
        public static void Clear() => _pool.Clear();

        private static void ReturnInternal(TransformNode node)
        {
            if (node.InPool || node is not TNode typed || node.GetType() != typeof(TNode))
                return;

            typed.InPool = true;
            if (_pool.Count < MaxPoolSize)
            {
                _pool.Push(typed);
            }
        }
    }
}
