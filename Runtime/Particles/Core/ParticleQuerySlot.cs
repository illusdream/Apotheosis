#nullable enable

using System;
using System.Threading;
using Apotheosis.Runtime.Foundation;

namespace Apotheosis.Runtime.Particles.Core
{
    /// <summary>4 字节强类型查询缓存槽位（<c>default</c> 为无效，通过 <see cref="Allocate"/> 线程安全自增分配）。</summary>
    public readonly struct ParticleQuerySlot : IEquatable<ParticleQuerySlot>
    {
        private static int _nextId;

        private readonly int _id;

        private ParticleQuerySlot(int id) => _id = id;

        /// <summary>当前槽位是否已通过 <see cref="Allocate"/> 合法分配。</summary>
        public bool IsValid => _id > RuntimeConstants.IndexNone;

        /// <summary>对应 <see cref="ParticleStoreSlot"/> 内部缓存数组的 0-based 下标。</summary>
        public int Index => _id - 1;

        /// <summary>当前进程域内已分配的查询槽位总数。</summary>
        public static int AllocatedCount => Volatile.Read(ref _nextId);

        /// <summary>原子分配一个全局唯一的查询缓存槽位（线程安全）。</summary>
        public static ParticleQuerySlot Allocate()
        {
            // Interlocked.Increment 通过 CPU 原子指令保证多线程并发初始化静态字段时也绝不撞号
            int id = Interlocked.Increment(ref _nextId);
            return new ParticleQuerySlot(id);
        }

        public bool Equals(ParticleQuerySlot other) => _id == other._id;
        public override bool Equals(object? obj) => obj is ParticleQuerySlot other && Equals(other);
        public override int GetHashCode() => _id;
        public static bool operator ==(ParticleQuerySlot left, ParticleQuerySlot right) => left.Equals(right);
        public static bool operator !=(ParticleQuerySlot left, ParticleQuerySlot right) => !left.Equals(right);
        public override string ToString() => IsValid ? $"ParticleQuerySlot({Index})" : "ParticleQuerySlot(Invalid)";
    }
}
