#nullable enable

using System;
using System.Runtime.CompilerServices;
using Friflo.Engine.ECS;

namespace Apotheosis.Runtime.Particles.Fragments
{
    /// <summary>纯组件布局碎片（轻量值类型，仅封装 <see cref="ComponentTypes"/> 便于自由拼接复用，不含任何赋值或发射逻辑）。</summary>
    public readonly struct ParticleFragment : IEquatable<ParticleFragment>
    {
        public readonly ComponentTypes Types;

        public ParticleFragment(ComponentTypes types) => Types = types;

        /// <summary>当前布局包含的组件类型数量。</summary>
        public int Count => Types.Count;

        /// <summary>检查当前布局是否包含指定组件类型。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has<T>() where T : struct, IComponent => Types.Has<T>();

        /// <summary>返回追加了指定组件类型的新布局。</summary>
        public ParticleFragment With<T>() where T : struct, IComponent
        {
            ComponentTypes next = Types;
            next.Add<T>();
            return new ParticleFragment(next);
        }

        /// <summary>返回移除了指定组件类型的新布局。</summary>
        public ParticleFragment Without<T>() where T : struct, IComponent
        {
            ComponentTypes next = Types;
            next.Remove<T>();
            return new ParticleFragment(next);
        }

        /// <summary>合并另一个布局碎片包含的全部组件类型。</summary>
        public ParticleFragment Combine(ParticleFragment other)
        {
            ComponentTypes next = Types;
            next.Add(in other.Types);
            return new ParticleFragment(next);
        }

        public static ParticleFragment From<T1>() where T1 : struct, IComponent
            => new(ComponentTypes.Get<T1>());

        public static ParticleFragment From<T1, T2>() where T1 : struct, IComponent where T2 : struct, IComponent
            => new(ComponentTypes.Get<T1, T2>());

        public static ParticleFragment From<T1, T2, T3>() where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
            => new(ComponentTypes.Get<T1, T2, T3>());

        public static ParticleFragment From<T1, T2, T3, T4>() where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent where T4 : struct, IComponent
            => new(ComponentTypes.Get<T1, T2, T3, T4>());

        public static ParticleFragment From<T1, T2, T3, T4, T5>() where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent where T4 : struct, IComponent where T5 : struct, IComponent
            => new(ComponentTypes.Get<T1, T2, T3, T4, T5>());

        public static ParticleFragment operator |(ParticleFragment left, ParticleFragment right) => left.Combine(right);
        public static ParticleFragment operator +(ParticleFragment left, ParticleFragment right) => left.Combine(right);

        public bool Equals(ParticleFragment other) => Types.Equals(other.Types);
        public override bool Equals(object? obj) => obj is ParticleFragment other && Equals(other);
        public override int GetHashCode() => Types.GetHashCode();
        public static bool operator ==(ParticleFragment left, ParticleFragment right) => left.Equals(right);
        public static bool operator !=(ParticleFragment left, ParticleFragment right) => !left.Equals(right);
        public override string ToString() => $"ParticleFragment({Types})";
    }

    /// <summary>指向目标 <see cref="Archetype"/> 中刚连续分配的一批新粒子组件切片的零分配视图。</summary>
    public readonly ref struct ParticleSpawnBatch
    {
        private readonly Archetype _archetype;
        private readonly int _startIndex;

        /// <summary>本批次新创建的粒子数量。</summary>
        public int Count { get; }

        internal ParticleSpawnBatch(Archetype archetype, int startIndex, int count)
        {
            _archetype = archetype;
            _startIndex = startIndex;
            Count = count;
        }

        /// <summary>检查当前切片所属 <see cref="Archetype"/> 是否包含指定组件类型。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has<T>() where T : struct, IComponent => _archetype.ComponentTypes.Has<T>();

        /// <summary>获取本批次新粒子对应的指定组件连续可写切片（供发射器单遍写入个体数据）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<T> GetSpan<T>() where T : struct, IComponent => _archetype.Components<T>().Slice(_startIndex, Count);

        /// <summary>将整批共享的常量组件值一次性填充至连续切片。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Fill<T>(in T value) where T : struct, IComponent => _archetype.Components<T>().Slice(_startIndex, Count).Fill(value);
    }
}
