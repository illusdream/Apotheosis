#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace Apotheosis.Runtime.Foundation.Collections
{
    /// <summary>零分配可变值类型可扩容连续缓冲（应作为字段或按 <c>ref</c> 传递，勿按值拷贝），支持 <see cref="Span{T}"/> 预留、引用追加与底层数组直传。</summary>
    internal struct ValueBuffer<T>
    {
        private T[]? _items;
        private int _count;
        private readonly int _defaultGrowCapacity;

        public ValueBuffer(int initialCapacity = 0, int defaultGrowCapacity = 4)
        {
            _defaultGrowCapacity = Math.Max(1, defaultGrowCapacity);
            _items = initialCapacity > 0 ? new T[initialCapacity] : Array.Empty<T>();
            _count = 0;
        }

        public readonly int Count => _count;
        public readonly int Capacity => _items?.Length ?? 0;
        public readonly T[] RawArray => _items ?? Array.Empty<T>();

        public readonly ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _items![index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity(int required)
        {
            T[]? items = _items;
            if (items is not null && required <= items.Length)
                return;

            Grow(required);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Add(in T item)
        {
            int idx = _count;
            EnsureCapacity(idx + 1);
            _items![idx] = item;
            _count = idx + 1;
            return idx;
        }

        /// <summary>追加一个默认初始化槽位并返回其可写引用。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T AddRef()
        {
            int idx = _count;
            EnsureCapacity(idx + 1);
            _count = idx + 1;
            ref T slot = ref _items![idx];
            slot = default!;
            return ref slot;
        }

        /// <summary>预留连续空间并返回起始索引与可写切片。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Allocate(int count, out Span<T> span)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count == 0)
            {
                span = Span<T>.Empty;
                return _count;
            }

            int start = _count;
            int end = checked(start + count);
            EnsureCapacity(end);
            _count = end;
            span = _items.AsSpan(start, count);
            return start;
        }

        /// <summary>预留连续空间并返回可写切片。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<T> AllocateSpan(int count)
        {
            Allocate(count, out Span<T> span);
            return span;
        }

        /// <summary>批量追加切片数据并返回起始索引。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int AddRange(ReadOnlySpan<T> source)
        {
            int start = _count;
            if (source.IsEmpty)
                return start;

            int end = checked(start + source.Length);
            EnsureCapacity(end);
            source.CopyTo(_items.AsSpan(start, source.Length));
            _count = end;
            return start;
        }

        /// <summary>用源切片完整覆写当前内容并清理尾部多余引用。</summary>
        public void CopyFrom(ReadOnlySpan<T> source)
        {
            int newCount = source.Length;
            if (newCount > 0)
            {
                EnsureCapacity(newCount);
                source.CopyTo(_items.AsSpan(0, newCount));
            }

            if (_count > newCount && _items is not null && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                Array.Clear(_items, newCount, _count - newCount);

            _count = newCount;
        }

        /// <summary>确保底层容量并直接设置当前逻辑长度（供外部向 <see cref="RawArray"/> 拷贝后更新计数）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetCount(int newCount, bool clearReferences = true)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(newCount);
            if (newCount > _count)
                EnsureCapacity(newCount);
            else if (clearReferences && newCount < _count && _items is not null && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                Array.Clear(_items, newCount, _count - newCount);

            _count = newCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear(bool clearReferences = true)
        {
            if (_count > 0 && clearReferences && _items is not null && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                Array.Clear(_items, 0, _count);

            _count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly Span<T> AsSpan() => _count > 0 ? _items.AsSpan(0, _count) : Span<T>.Empty;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ReadOnlySpan<T> AsReadOnlySpan() => _count > 0 ? _items.AsSpan(0, _count) : ReadOnlySpan<T>.Empty;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Grow(int required)
        {
            int current = _items?.Length ?? 0;
            int growBase = _defaultGrowCapacity > 0 ? _defaultGrowCapacity : 4;
            int next = current == 0 ? growBase : (int)Math.Min(Array.MaxLength, (long)current * 2);
            if (next < required)
                next = required;

            if (_items is null || _items.Length == 0)
                _items = new T[next];
            else
                Array.Resize(ref _items, next);
        }
    }
}
