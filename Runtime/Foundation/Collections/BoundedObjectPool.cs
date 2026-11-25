#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Apotheosis.Runtime.Foundation.Collections
{
    /// <summary>带容量上限的轻量对象池（LIFO 保持热点对象局部性，超出上限时自动丢弃避免尖峰驻留）。</summary>
    internal sealed class BoundedObjectPool<T>
    {
        private readonly Stack<T> _pool;
        private readonly Func<T>? _factory;
        private int _maxSize;

        public BoundedObjectPool(int initialCapacity = 16, int maxSize = 1024, Func<T>? factory = null)
        {
            _pool = new Stack<T>(Math.Max(0, initialCapacity));
            _maxSize = Math.Max(0, maxSize);
            _factory = factory;
        }

        public int Count => _pool.Count;

        public int MaxSize
        {
            get => _maxSize;
            set => _maxSize = Math.Max(0, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPop([MaybeNullWhen(false)] out T item) => _pool.TryPop(out item);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Rent()
        {
            if (_pool.TryPop(out T? existing))
                return existing;

            if (_factory is null)
                throw new InvalidOperationException($"BoundedObjectPool<{typeof(T).Name}> 未配置默认工厂委托。");

            return _factory();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Rent(Func<T> factory)
            => _pool.TryPop(out T? existing) ? existing : factory();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Return(in T item)
        {
            if (item is null || _pool.Count >= _maxSize)
                return false;

            _pool.Push(item);
            return true;
        }

        public void Clear() => _pool.Clear();
    }
}
