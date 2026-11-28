#nullable enable

using System;
using Friflo.Engine.ECS;

namespace Apotheosis.Runtime.Particles.Fragments
{
    /// <summary>由布局碎片烘焙而成的不可变粒子原型，仅管理 <see cref="ComponentTypes"/> 签名与零迁移批量分配（布局合法性由编译期 Roslyn 分析器保障）。</summary>
    public sealed class ParticleTemplate
    {
        private static readonly Tags EmptyTags = default;

        private ComponentTypes _componentTypes;
        private volatile bool _baked;

        /// <summary>模板名称。</summary>
        public string Name { get; }

        /// <summary>当前模板是否已完成烘焙（烘焙后不可变，可跨线程安全共享）。</summary>
        public bool IsBaked => _baked;

        /// <summary>烘焙后的完整 ECS 组件类型签名。</summary>
        public ComponentTypes ComponentTypes => _componentTypes;

        private ParticleTemplate(string name, ComponentTypes initialTypes)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "UnnamedTemplate" : name;
            _componentTypes = initialTypes;
        }

        /// <summary>开始链式构建一个新的粒子模板。</summary>
        public static ParticleTemplate Create(string name) => new(name, default);

        /// <summary>直接由合并好的 <see cref="ParticleFragment"/> 一步烘焙不可变粒子模板。</summary>
        public static ParticleTemplate Bake(string name, ParticleFragment layout) => new ParticleTemplate(name, layout.Types).Bake();

        /// <summary>向模板追加一个组件布局碎片（必须在 <see cref="Bake"/> 之前调用）。</summary>
        public ParticleTemplate Add(ParticleFragment fragment)
        {
            EnsureMutable();
            _componentTypes.Add(in fragment.Types);
            return this;
        }

        /// <summary>向模板直接追加单个组件类型（必须在 <see cref="Bake"/> 之前调用）。</summary>
        public ParticleTemplate With<T>() where T : struct, IComponent
        {
            EnsureMutable();
            _componentTypes.Add<T>();
            return this;
        }

        /// <summary>从模板移除单个组件类型（必须在 <see cref="Bake"/> 之前调用）。</summary>
        public ParticleTemplate Without<T>() where T : struct, IComponent
        {
            EnsureMutable();
            _componentTypes.Remove<T>();
            return this;
        }

        /// <summary>固化当前组件签名；烘焙后不可再修改。</summary>
        public ParticleTemplate Bake()
        {
            _baked = true;
            return this;
        }

        /// <summary>仅限粒子工作线程调用：按烘焙签名在目标 <see cref="EntityStore"/> 一次性连续分配 <paramref name="count"/> 个粒子，返回原始切片供发射器单遍写入。</summary>
        internal ParticleSpawnBatch AllocateBatch(EntityStore store, int count)
        {
            if (!_baked)
                throw new InvalidOperationException($"粒子模板 '{Name}' 尚未调用 Bake()，无法分配粒子。");

            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count), "批量分配数量必须大于 0。");

            // 直接向目标 Archetype 批量创建实体：一次性完成 Store 与 Archetype 扩容，零迁移、零多余默认覆写
            Archetype archetype = store.GetArchetype(in _componentTypes, in EmptyTags);
            int startIndex = archetype.Count;
            archetype.CreateEntities(count);

            return new ParticleSpawnBatch(archetype, startIndex, count);
        }

        private void EnsureMutable()
        {
            if (_baked)
                throw new InvalidOperationException($"粒子模板 '{Name}' 已烘焙，禁止继续修改。");
        }
    }
}
