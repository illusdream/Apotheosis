#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace Apotheosis.Runtime.Foundation.Collections
{
    /// <summary>定长零拷贝环形缓冲区，支持 O(1) 头部压入覆写最旧元素及分段连续切片读取。</summary>
    internal sealed class RingBuffer<T>
    {
        private readonly T[] _buffer;
        private int _head;
        private int _count;

        public RingBuffer(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            _buffer = new T[capacity];
        }

        public int Capacity => _buffer.Length;
        public int Count => _count;
        public bool IsEmpty => _count == 0;

        /// <summary>按逻辑顺序读取元素（<c>0</c> 为最新头部元素）。</summary>
        public ref readonly T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if ((uint)index >= (uint)_count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                int phys = _head + index;
                if (phys >= _buffer.Length)
                    phys -= _buffer.Length;
                return ref _buffer[phys];
            }
        }

        /// <summary>O(1) 在头部压入最新元素，满容量时自动覆盖最旧尾部元素。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PushFront(in T item)
        {
            int nextHead = _head == 0 ? _buffer.Length - 1 : _head - 1;
            _buffer[nextHead] = item;
            _head = nextHead;

            if (_count < _buffer.Length)
                _count++;
        }

        /// <summary>用外部连续切片从头覆写缓冲内容（超出容量部分截断）。</summary>
        public void OverwriteFrom(ReadOnlySpan<T> source)
        {
            int copyCount = Math.Min(source.Length, _buffer.Length);
            if (copyCount > 0)
                source.Slice(0, copyCount).CopyTo(_buffer);

            // _head 非零时旧数据可能位于数组尾部，因此只要原先非空且未完全覆写满缓冲区，均须清理尾部残留引用
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && _count > 0 && copyCount < _buffer.Length)
                Array.Clear(_buffer, copyCount, _buffer.Length - copyCount);

            _head = 0;
            _count = copyCount;
        }

        /// <summary>零拷贝获取逻辑顺序对应的前后两段连续只读切片（未回绕时 <paramref name="second"/> 为空）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetSegments(out ReadOnlySpan<T> first, out ReadOnlySpan<T> second)
        {
            if (_count == 0)
            {
                first = ReadOnlySpan<T>.Empty;
                second = ReadOnlySpan<T>.Empty;
                return;
            }

            int tailSpace = _buffer.Length - _head;
            if (_count <= tailSpace)
            {
                first = _buffer.AsSpan(_head, _count);
                second = ReadOnlySpan<T>.Empty;
            }
            else
            {
                first = _buffer.AsSpan(_head, tailSpace);
                second = _buffer.AsSpan(0, _count - tailSpace);
            }
        }

        public void Clear()
        {
            if (_count > 0 && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                Array.Clear(_buffer, 0, _buffer.Length);

            _head = 0;
            _count = 0;
        }
    }
}
