#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Apotheosis.Runtime.Foundation.Collections
{
    /// <summary>16 位句柄（<c>1..65535</c>）并发无锁读槽位表，内置受保护标志、帧末去重回收标记与模块代际号。</summary>
    internal sealed class UShortSlotTable<T> where T : class
    {
        [Flags]
        private enum SlotFlags : byte
        {
            None = 0,
            Protected = 1 << 0,
            PendingRelease = 1 << 1,
        }

        private T?[] _slots;
        private SlotFlags[] _flags;
        private readonly Stack<ushort> _freeIds = new();

        private int _highWaterMark;
        private int _activeCount;
        private int _epoch = 1;

        public UShortSlotTable(int initialCapacity = 256)
        {
            int cap = Math.Clamp(initialCapacity, 16, ushort.MaxValue + 1);
            _slots = new T?[cap];
            _flags = new SlotFlags[cap];
        }

        /// <summary>当前活跃槽位总数（无锁可读）。</summary>
        public int ActiveCount => Volatile.Read(ref _activeCount);

        /// <summary>当前槽位表代际号（每次卸载清空后递增，用于外部懒缓存自动失效）。</summary>
        public int Epoch => Volatile.Read(ref _epoch);

        /// <summary>O(1) 无锁安全解析句柄槽位对应的实例。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(ushort id, [NotNullWhen(true)] out T? item)
        {
            if (id != 0)
            {
                T?[] slots = Volatile.Read(ref _slots);
                if ((uint)id < (uint)slots.Length)
                {
                    item = Volatile.Read(ref slots[id]);
                    return item is not null;
                }
            }

            item = null;
            return false;
        }

        /// <summary>锁内快速解析句柄槽位（调用方已持写锁）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetLocked(ushort id, [NotNullWhen(true)] out T? item)
        {
            if (id != 0 && id < _slots.Length)
            {
                item = _slots[id];
                return item is not null;
            }

            item = null;
            return false;
        }

        /// <summary>分配一个空闲槽位 ID（调用方已持写锁）。</summary>
        public ushort AllocateIdLocked(string limitExceededMessage)
        {
            if (_freeIds.TryPop(out ushort reused))
                return reused;

            int next = _highWaterMark + 1;
            if (next > ushort.MaxValue)
                throw new InvalidOperationException(limitExceededMessage);

            if (next >= _slots.Length)
            {
                int newLen = Math.Min(ushort.MaxValue + 1, _slots.Length * 2);
                var grownFlags = new SlotFlags[newLen];
                Array.Copy(_flags, grownFlags, _flags.Length);
                _flags = grownFlags;

                // 最后发布新 _slots 引用，确保无锁并发读线程始终观察到一致数组
                var grownSlots = new T?[newLen];
                Array.Copy(_slots, grownSlots, _slots.Length);
                Volatile.Write(ref _slots, grownSlots);
            }

            _highWaterMark = next;
            return (ushort)next;
        }

        /// <summary>归还已分配但尚未发布的槽位 ID（用于初始化异常回滚，调用方已持写锁）。</summary>
        public void RecycleUnusedIdLocked(ushort id)
        {
            if (id != 0 && id < _slots.Length && _slots[id] is null)
            {
                _flags[id] = SlotFlags.None;
                _freeIds.Push(id);
            }
        }

        /// <summary>将已初始化完成的实例发布到指定槽位（调用方已持写锁）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PublishLocked(ushort id, T item, bool protect = false)
        {
            Debug.Assert(id != 0 && id < _slots.Length && _slots[id] is null, "槽位 ID 非法或已被占用。");
            _flags[id] = protect ? SlotFlags.Protected : SlotFlags.None;
            Volatile.Write(ref _slots[id], item);
            Volatile.Write(ref _activeCount, _activeCount + 1);
        }

        /// <summary>校验槽位活跃且未受保护、未重复入队，并置位待回收标记（调用方已持写锁）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryMarkPendingReleaseLocked(ushort id)
        {
            if (id == 0 || id >= _slots.Length || _slots[id] is null)
                return false;

            if ((_flags[id] & (SlotFlags.Protected | SlotFlags.PendingRelease)) != 0)
                return false;

            _flags[id] |= SlotFlags.PendingRelease;
            return true;
        }

        /// <summary>解除指定槽位的受保护标记（调用方已持写锁）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnprotectLocked(ushort id)
        {
            if (id != 0 && id < _flags.Length)
                _flags[id] &= ~SlotFlags.Protected;
        }

        /// <summary>移除并归还指定槽位 ID，返回被移除的实例（调用方已持写锁）。</summary>
        public T? RemoveLocked(ushort id)
        {
            if (id == 0 || id >= _slots.Length)
                return null;

            T? removed = _slots[id];
            if (removed is null)
                return null;

            Volatile.Write(ref _slots[id], null);
            _flags[id] = SlotFlags.None;
            _freeIds.Push(id);
            Volatile.Write(ref _activeCount, Math.Max(0, _activeCount - 1));
            return removed;
        }

        /// <summary>清空全部槽位、重置分配状态并递增代际号，并将原活跃实例收集至 <paramref name="drainedItems"/>（若为空则按需分配；无需收集请调用无参重载）。</summary>
        public void ClearLocked(ref List<T>? drainedItems)
        {
            int limit = Math.Min(_highWaterMark, _slots.Length - 1);
            for (int i = 1; i <= limit; i++)
            {
                T? item = _slots[i];
                if (item is not null)
                {
                    if (drainedItems is not null)
                        drainedItems.Add(item);
                    else
                        drainedItems = new List<T> { item };

                    _slots[i] = null;
                }
                _flags[i] = SlotFlags.None;
            }

            _freeIds.Clear();
            _highWaterMark = 0;
            Volatile.Write(ref _activeCount, 0);
            IncrementEpochLocked();
        }

        /// <summary>仅清空全部槽位并递增代际号（不收集实例，调用方已持写锁）。</summary>
        public void ClearLocked()
        {
            int clearLen = Math.Min(_highWaterMark + 1, _slots.Length);
            if (clearLen > 0)
            {
                Array.Clear(_slots, 0, clearLen);
                Array.Clear(_flags, 0, clearLen);
            }

            _freeIds.Clear();
            _highWaterMark = 0;
            Volatile.Write(ref _activeCount, 0);
            IncrementEpochLocked();
        }

        private void IncrementEpochLocked()
        {
            int next = _epoch + 1;
            if (next == 0)
                next = 1;
            Volatile.Write(ref _epoch, next);
        }
    }
}
