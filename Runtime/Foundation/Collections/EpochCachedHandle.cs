#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Apotheosis.Runtime.Foundation.Collections
{
    /// <summary>带模块代际校验的受保护句柄懒缓存（可变值类型，禁止声明为 <c>readonly</c> 字段；<typeparamref name="THandle"/> 应为单字宽原子句柄）。</summary>
    internal struct EpochCachedHandle<THandle> where THandle : unmanaged
    {
        private THandle _handle;
        private int _epoch;

        /// <summary>无锁快路径读取当前代际下已缓存的句柄。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGet(int currentEpoch, out THandle handle)
        {
            if (currentEpoch != 0 && Volatile.Read(ref Unsafe.AsRef(in _epoch)) == currentEpoch)
            {
                handle = _handle;
                return true;
            }

            handle = default;
            return false;
        }

        /// <summary>带卸载守卫的无锁快读或加锁惰性创建（静态调用方零分配）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public THandle GetOrCreate(object syncLock, Func<bool> isUnloading, Func<int> getEpoch, Func<THandle> factory)
        {
            if (isUnloading())
                return default;

            int epoch = getEpoch();
            if (TryGet(epoch, out THandle cached))
                return cached;

            lock (syncLock)
            {
                if (isUnloading())
                    return default;

                epoch = getEpoch();
                if (epoch == 0)
                    return default;
                if (_epoch == epoch)
                    return _handle;

                THandle created = factory();
                // 工厂返回无效句柄或执行期间发生卸载/代际切换时不发布缓存
                if (EqualityComparer<THandle>.Default.Equals(created, default) || isUnloading() || getEpoch() != epoch)
                    return default;

                _handle = created;
                // 先写句柄再以释放语义写代际号，确保无锁读线程看到一致句柄
                Volatile.Write(ref _epoch, epoch);
                return created;
            }
        }

        /// <summary>带显式状态的无锁快读或加锁惰性创建（实例调用方配合静态委托实现零闭包分配）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public THandle GetOrCreate<TState>(object syncLock, TState state, Func<bool> isUnloading, Func<TState, int> getEpoch, Func<TState, THandle> factory)
        {
            if (isUnloading())
                return default;

            int epoch = getEpoch(state);
            if (TryGet(epoch, out THandle cached))
                return cached;

            lock (syncLock)
            {
                if (isUnloading())
                    return default;

                epoch = getEpoch(state);
                if (epoch == 0)
                    return default;
                if (_epoch == epoch)
                    return _handle;

                THandle created = factory(state);
                if (EqualityComparer<THandle>.Default.Equals(created, default) || isUnloading() || getEpoch(state) != epoch)
                    return default;

                _handle = created;
                Volatile.Write(ref _epoch, epoch);
                return created;
            }
        }

        public void Invalidate()
        {
            // THandle 为非托管值类型无 GC 滞留风险；仅清零代际号避免无锁 TryGet 读到半更新的 default 句柄
            Volatile.Write(ref _epoch, 0);
        }
    }
}
