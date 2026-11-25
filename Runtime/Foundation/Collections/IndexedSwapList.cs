#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Apotheosis.Runtime.Foundation.Collections
{
    /// <summary>支持 <see cref="IndexedSwapList{T}"/> O(1) 交换删除的侵入式索引契约（单实例同一时刻仅归属单个列表）。</summary>
    internal interface IIndexedSwapItem
    {
        /// <summary>元素在所属列表中的当前索引（未加入时为 <see cref="RuntimeConstants.InvalidIndex"/>）。</summary>
        int SwapListIndex { get; set; }
    }

    /// <summary>侵入式 O(1) 插入、查询与交换删除的无序紧密列表。</summary>
    internal sealed class IndexedSwapList<T> where T : class, IIndexedSwapItem
    {
        private readonly List<T> _items;

        public IndexedSwapList(int initialCapacity = 16)
        {
            _items = new List<T>(initialCapacity);
        }

        public int Count => _items.Count;

        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _items[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(T item)
        {
            int idx = item.SwapListIndex;
            return (uint)idx < (uint)_items.Count && ReferenceEquals(_items[idx], item);
        }

        /// <summary>O(1) 追加元素；若已在列表中则返回 <c>false</c>。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Add(T item)
        {
            if (Contains(item))
                return false;

            Debug.Assert(item.SwapListIndex == RuntimeConstants.InvalidIndex, "元素已归属其他 IndexedSwapList，禁止跨列表重复加入。");
            item.SwapListIndex = _items.Count;
            _items.Add(item);
            return true;
        }

        /// <summary>O(1) 将目标元素与尾元素交换并移除；不在列表中则返回 <c>false</c>。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(T item)
        {
            int idx = item.SwapListIndex;
            if ((uint)idx >= (uint)_items.Count || !ReferenceEquals(_items[idx], item))
                return false;

            int lastIdx = _items.Count - 1;
            if (idx != lastIdx)
            {
                T last = _items[lastIdx];
                _items[idx] = last;
                last.SwapListIndex = idx;
            }

            _items.RemoveAt(lastIdx);
            item.SwapListIndex = RuntimeConstants.InvalidIndex;
            return true;
        }

        /// <summary>将当前活跃元素快照拷贝到调用方缓冲列表，便于遍历期间安全增删。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyTo(List<T> destination)
        {
            destination.Clear();
            destination.AddRange(_items);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<T> AsSpan() => CollectionsMarshal.AsSpan(_items);

        /// <summary>清空列表并将所有已存元素的索引重置为无效。</summary>
        public void Clear()
        {
            for (int i = 0; i < _items.Count; i++)
                _items[i].SwapListIndex = RuntimeConstants.InvalidIndex;

            _items.Clear();
        }
    }
}
