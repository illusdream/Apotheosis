#nullable enable

using System;
using System.Runtime.CompilerServices;
using Friflo.Engine.ECS;

namespace Apotheosis.Runtime.Particles.Core
{
    /// <summary>封装单个 <see cref="EntityStore"/>、可复用 <see cref="CommandBuffer"/> 与按系统槽位缓存的查询表。</summary>
    public sealed class ParticleStoreSlot
    {
        private const int InitialQueryCacheCapacity = 16;

        private ArchetypeQuery?[] _queryCache;

        /// <summary>当前槽位持有的 ECS 实体存储（仅允许粒子工作线程独占读写）。</summary>
        public EntityStore Store { get; }

        /// <summary>与当前存储绑定的可复用命令缓冲。</summary>
        public CommandBuffer CommandBuffer { get; }

        /// <summary>当前存储中的活跃粒子总数。</summary>
        public int EntityCount => Store.Count;

        /// <summary>命令缓冲中是否存在尚未回放的结构变更。</summary>
        public bool HasPendingCommands
            => (CommandBuffer.EntityCommandsCount | CommandBuffer.ComponentCommandsCount | CommandBuffer.TagCommandsCount
                | CommandBuffer.ScriptCommandsCount | CommandBuffer.ChildCommandsCount) != 0;

        public ParticleStoreSlot(int initialEntityCapacity = 64)
        {
            Store = new EntityStore();
            Store.RecycleIds = true;
            if (initialEntityCapacity > 0)
                Store.EnsureCapacity(initialEntityCapacity);

            CommandBuffer = Store.GetCommandBuffer();
            CommandBuffer.ReuseBuffer = true;

            int cacheCapacity = Math.Max(InitialQueryCacheCapacity, ParticleQuerySlot.AllocatedCount);
            _queryCache = new ArchetypeQuery?[cacheCapacity];
        }

        /// <summary>按系统注册槽位 O(1) 获取或惰性创建对应的 <see cref="ArchetypeQuery"/>。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TQuery GetOrCreateQuery<TQuery>(ParticleQuerySlot querySlot, Func<EntityStore, TQuery> factory) where TQuery : ArchetypeQuery
        {
            int index = querySlot.Index;
            if ((uint)index < (uint)_queryCache.Length && _queryCache[index] is TQuery cached)
                return cached;

            return CreateAndCacheQuery(querySlot, factory);
        }

        /// <summary>回放当前槽位已录制的结构变更命令。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PlaybackCommandBuffer()
        {
            if (HasPendingCommands)
                CommandBuffer.Playback();
        }

        /// <summary>零分配清空全部粒子实体，保留已预热的 Archetype 内存供对象池复用。</summary>
        public void ClearEntities()
        {
            // 先回放残留命令，确保不存在未提交的脏状态
            if (HasPendingCommands)
                CommandBuffer.Playback();

            if (Store.Count == 0)
                return;

            foreach (Archetype archetype in Store.Archetypes)
            {
                // 从末尾倒删：不经 CommandBuffer、免去 Swap-Remove 元素搬移、保留预热 Archetype 内存
                while (archetype.Count > 0)
                {
                    int lastId = archetype.EntityIds[archetype.Count - 1];
                    Store.GetEntityById(lastId).DeleteEntity();
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private TQuery CreateAndCacheQuery<TQuery>(ParticleQuerySlot querySlot, Func<EntityStore, TQuery> factory) where TQuery : ArchetypeQuery
        {
            ArgumentNullException.ThrowIfNull(factory);
            if (!querySlot.IsValid)
                throw new ArgumentException("必须使用通过 ParticleQuerySlot.Allocate() 分配的有效槽位。", nameof(querySlot));

            int index = querySlot.Index;
            if (index >= _queryCache.Length)
            {
                int newSize = Math.Max(InitialQueryCacheCapacity, _queryCache.Length * 2);
                while (newSize <= index)
                    newSize *= 2;
                Array.Resize(ref _queryCache, newSize);
            }

            if (_queryCache[index] is not null)
                throw new InvalidOperationException($"查询槽位 {querySlot} 已缓存类型 {_queryCache[index]!.GetType().Name}，与请求的 {typeof(TQuery).Name} 冲突。");

            TQuery created = factory(Store);
            _queryCache[index] = created;
            return created;
        }
    }
}
