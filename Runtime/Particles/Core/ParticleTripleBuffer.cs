#nullable enable

using System;
using System.Threading;

namespace Apotheosis.Runtime.Particles.Core
{
    /// <summary>单写者/单读者三缓冲容器（<c>Write / Ready / Reading</c>），通过极短锁交换槽位索引实现零拷贝与零踩踏跨线程交付。</summary>
    public sealed class ParticleTripleBuffer<T> where T : class
    {
        private const int SlotCount = 3;

        private readonly object _swapLock = new();
        private readonly T[] _slots;

        // 任何时刻 _writeIndex / _readIndex / _readyIndex 始终是 {0, 1, 2} 的互异排列，写线程与读线程互不踩踏
        private int _writeIndex;
        private int _readIndex;
        private int _readyIndex;
        private bool _hasNewReady;

        public ParticleTripleBuffer(Func<T> slotFactory)
        {
            ArgumentNullException.ThrowIfNull(slotFactory);
            _slots = new T[SlotCount];
            for (int i = 0; i < SlotCount; i++)
                _slots[i] = slotFactory();

            _writeIndex = 0;
            _readIndex = 1;
            _readyIndex = 2;
            _hasNewReady = false;
        }

        /// <summary>获取写线程当前独占的可写槽位。</summary>
        public T WriteSlot => _slots[Volatile.Read(ref _writeIndex)];

        /// <summary>获取读线程当前正在消费的只读槽位（不触发交换）。</summary>
        public T CurrentReadSlot => _slots[Volatile.Read(ref _readIndex)];

        /// <summary>写线程完成写入后，将当前 <c>Write</c> 槽交接至 <c>Ready</c> 并返回下一帧可写槽。</summary>
        public T PublishWrite()
        {
            lock (_swapLock)
            {
                int nextWrite = _readyIndex;
                _readyIndex = _writeIndex;
                Volatile.Write(ref _writeIndex, nextWrite);
                _hasNewReady = true;
                return _slots[nextWrite];
            }
        }

        /// <summary>写线程清空当前独占的 <c>Write</c> 槽并立即发布空快照（严禁触碰主线程可能正在绘制的 <c>Reading</c> 槽）。</summary>
        public T PublishClearedWrite(Action<T> clearSlot)
        {
            ArgumentNullException.ThrowIfNull(clearSlot);
            clearSlot(_slots[Volatile.Read(ref _writeIndex)]);
            return PublishWrite();
        }

        /// <summary>读线程尝试接管最新 <c>Ready</c> 槽；若无新帧则继续复用当前 <c>Reading</c> 槽。</summary>
        public T AcquireRead(out bool swapped)
        {
            lock (_swapLock)
            {
                if (_hasNewReady)
                {
                    int nextRead = _readyIndex;
                    _readyIndex = _readIndex;
                    Volatile.Write(ref _readIndex, nextRead);
                    _hasNewReady = false;
                    swapped = true;
                    return _slots[nextRead];
                }

                swapped = false;
                return _slots[_readIndex];
            }
        }

        /// <summary>在工作线程完全退出且读线程停止访问后，遍历释放全部 3 个槽位资源。</summary>
        public void ForEachSlot(Action<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            for (int i = 0; i < SlotCount; i++)
                action(_slots[i]);
        }
    }
}
