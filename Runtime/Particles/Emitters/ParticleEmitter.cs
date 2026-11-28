#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Core;
using Apotheosis.Runtime.Particles.Emitters.Modules;
using Apotheosis.Runtime.Particles.Emitters.SpawnRules;
using Apotheosis.Runtime.Particles.Fragments;
using Friflo.Engine.ECS;

namespace Apotheosis.Runtime.Particles.Emitters
{
    /// <summary>Niagara 风格模块化粒子发射器不可变蓝图：仅负责生成调度与批次分配，具体组件初始化完全委托给已挂载的 <see cref="IParticleSpawnModule"/> 栈。</summary>
    public sealed class ParticleEmitter
    {
        private const int MaxLoopWrapsPerTick = 4;

        private readonly struct ModuleRequirementCacheEntry
        {
            public readonly (Type Type, Func<ComponentTypes, bool> Predicate)[] RequiredAll;
            public readonly (string DisplayNames, Func<ComponentTypes, bool>[] Predicates)[] RequiredAny;

            public ModuleRequirementCacheEntry((Type, Func<ComponentTypes, bool>)[] requiredAll, (string, Func<ComponentTypes, bool>[])[] requiredAny)
            {
                RequiredAll = requiredAll;
                RequiredAny = requiredAny;
            }
        }

        private static readonly ConcurrentDictionary<Type, Func<ComponentTypes, bool>?> ComponentPresencePredicates = new();
        private static readonly ConcurrentDictionary<Type, ModuleRequirementCacheEntry> ModuleRequirementsCache = new();
        private static readonly MethodInfo HasComponentMethodDef = typeof(ParticleEmitter).GetMethod(nameof(HasComponentGeneric), BindingFlags.NonPublic | BindingFlags.Static)!;

        private readonly List<IParticleSpawnRule> _pendingRules = new(2);
        private readonly List<IParticleSpawnModule> _pendingModules = new(8);

        private float _startDelay;
        private float _duration = 1f;
        private bool _loop = true;
        private int _maxParticlesPerTick = 2048;
        private int _maxStoreParticles;

        private IParticleSpawnRule[] _rules = Array.Empty<IParticleSpawnRule>();
        private IParticleSpawnModule[] _modules = Array.Empty<IParticleSpawnModule>();
        private ParticleTemplate? _template;
        private volatile bool _baked;

        /// <summary>发射器蓝图名称。</summary>
        public string Name { get; }

        /// <summary>当前蓝图是否已完成烘焙（烘焙后完全只读不可变，可跨线程无锁复用于任意爆发与子系统挂载）。</summary>
        public bool IsBaked => _baked;

        /// <summary>由已挂载模块自动合成并烘焙的内部粒子原型（必须在 <see cref="Bake"/> 后访问）。</summary>
        public ParticleTemplate Template => _template ?? throw new InvalidOperationException($"粒子发射器 '{Name}' 尚未调用 Bake()。");

        /// <summary>首次开始发射前的延迟时长（秒）。</summary>
        public float StartDelay => _startDelay;

        /// <summary>单个发射周期持续时长（秒；非循环模式下 <c>&lt;= 0</c> 表示首帧触发后即完成）。</summary>
        public float Duration => _duration;

        /// <summary>周期结束后是否自动循环。</summary>
        public bool Loop => _loop;

        /// <summary>单次工作线程步进允许生成的最大粒子数上限（<c>&lt;= 0</c> 表示不限）。</summary>
        public int MaxParticlesPerTick => _maxParticlesPerTick;

        /// <summary>目标存储的软粒子数上限（<c>&lt;= 0</c> 表示不限）。</summary>
        public int MaxStoreParticles => _maxStoreParticles;

        private ParticleEmitter(string name)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "UnnamedEmitter" : name;
        }

        /// <summary>开始链式构建一个新的模块化粒子发射器蓝图。</summary>
        public static ParticleEmitter Create(string name) => new(name);

        /// <summary>一次性配置发射周期、延迟与预算上限。</summary>
        public ParticleEmitter WithSchedule(float duration = 1f, bool loop = true, float startDelay = 0f, int maxParticlesPerTick = 2048, int maxStoreParticles = 0)
        {
            EnsureMutable();
            _duration = float.IsFinite(duration) && duration > 0f ? duration : 0f;
            _loop = loop;
            _startDelay = float.IsFinite(startDelay) && startDelay > 0f ? startDelay : 0f;
            _maxParticlesPerTick = maxParticlesPerTick;
            _maxStoreParticles = maxStoreParticles;
            return this;
        }

        /// <summary>配置单周期时长与循环开关。</summary>
        public ParticleEmitter WithDuration(float duration, bool loop = true)
        {
            EnsureMutable();
            _duration = float.IsFinite(duration) && duration > 0f ? duration : 0f;
            _loop = loop;
            return this;
        }

        /// <summary>配置首次发射前的启动延迟时长（秒）。</summary>
        public ParticleEmitter WithStartDelay(float startDelay)
        {
            EnsureMutable();
            _startDelay = float.IsFinite(startDelay) && startDelay > 0f ? startDelay : 0f;
            return this;
        }

        /// <summary>配置单步最大生成上限与目标存储软上限（<c>&lt;= 0</c> 表示不限）。</summary>
        public ParticleEmitter WithLimits(int maxParticlesPerTick = 2048, int maxStoreParticles = 0)
        {
            EnsureMutable();
            _maxParticlesPerTick = maxParticlesPerTick;
            _maxStoreParticles = maxStoreParticles;
            return this;
        }

        /// <summary>追加一条恒定速率生成规则（粒子/秒）。</summary>
        public ParticleEmitter WithRate(float rateOverTime) => AddSpawnRule(new SpawnRateRule(rateOverTime));

        /// <summary>追加一条定点单次爆发规则。</summary>
        public ParticleEmitter WithBurst(float time, int count) => AddSpawnRule(new SpawnBurstRule(time, count));

        /// <summary>追加一条定时多周期爆发规则。</summary>
        public ParticleEmitter WithBurst(float time, int minCount, int maxCount, int cycles = 1, float interval = 0f)
            => AddSpawnRule(new SpawnBurstRule(time, minCount, maxCount, cycles, interval));

        /// <summary>追加一条自定义 <see cref="ParticleEmitterBurst"/> 爆发规则。</summary>
        public ParticleEmitter WithBurst(ParticleEmitterBurst burst) => AddSpawnRule(new SpawnBurstRule(burst));

        /// <summary>追加一条可插拔粒子生成调度规则。</summary>
        public ParticleEmitter AddSpawnRule(IParticleSpawnRule rule)
        {
            ArgumentNullException.ThrowIfNull(rule);
            EnsureMutable();
            _pendingRules.Add(rule);
            return this;
        }

        /// <summary>向发射器挂载一个出生初始化模块（<see cref="Bake"/> 时自动按阶段排序并合成组件签名）。</summary>
        public ParticleEmitter AddModule(IParticleSpawnModule module)
        {
            ArgumentNullException.ThrowIfNull(module);
            EnsureMutable();
            _pendingModules.Add(module);
            return this;
        }

        /// <summary>固化调度规则与模块栈，按 <see cref="ParticleSpawnPhase"/> 稳定排序并自动烘焙内部 <see cref="ParticleTemplate"/>。</summary>
        public ParticleEmitter Bake()
        {
            EnsureMutable();

            ComponentTypes mergedTypes = default;
            int moduleCount = _pendingModules.Count;
            var sortedModules = new IParticleSpawnModule[moduleCount];
            for (int i = 0; i < moduleCount; i++)
            {
                IParticleSpawnModule mod = _pendingModules[i];
                sortedModules[i] = mod;
                ComponentTypes provided = mod.ProvidedComponents;
                mergedTypes.Add(in provided);
            }

            // 运行期兜底校验：静态不可推导的动态拼装蓝图也能在此快速失败
            if (!mergedTypes.Has<ParticleLife>() || !mergedTypes.Has<ParticleSpatial>())
                throw new InvalidOperationException($"粒子发射器 '{Name}' 缺少核心初始化模块（必须提供 ParticleLife 与 ParticleSpatial）。");

            bool hasPose2D = mergedTypes.Has<ParticlePose2D>();
            bool hasPose3D = mergedTypes.Has<ParticlePose3D>();
            if (hasPose2D && hasPose3D)
                throw new InvalidOperationException($"粒子发射器 '{Name}' 同时挂载了 2D 与 3D 姿态模块（ParticlePose2D 与 ParticlePose3D 互斥）。");
            if (!hasPose2D && !hasPose3D)
                throw new InvalidOperationException($"粒子发射器 '{Name}' 缺少姿态初始化模块（必须提供 ParticlePose2D 或 ParticlePose3D 之一）。");

            // 运行期校验各模块声明的 [RequiresComponents] / [RequiresAnyComponent] 依赖，与编译期 APOT104 保持完全一致
            ValidateModuleRequirements(sortedModules, in mergedTypes);

            // 稳定插入排序：按 Phase 升序排列，同阶段严格保持调用方注册顺序
            for (int i = 1; i < sortedModules.Length; i++)
            {
                IParticleSpawnModule key = sortedModules[i];
                int keyPhase = (int)key.Phase;
                int j = i - 1;
                while (j >= 0 && (int)sortedModules[j].Phase > keyPhase)
                {
                    sortedModules[j + 1] = sortedModules[j];
                    j--;
                }

                sortedModules[j + 1] = key;
            }

            _modules = sortedModules;
            _rules = _pendingRules.Count > 0 ? _pendingRules.ToArray() : Array.Empty<IParticleSpawnRule>();
            _template = ParticleTemplate.Bake(Name, new ParticleFragment(mergedTypes));
            _pendingModules.Clear();
            _pendingRules.Clear();
            _baked = true;
            return this;
        }

        /// <summary>创建与本不可变蓝图绑定的独立持续播放态（每个挂载实例拥有独立计时与规则游标，零共享竞态）。</summary>
        public IParticleEmitter CreatePlayback()
        {
            if (!_baked)
                throw new InvalidOperationException($"粒子发射器 '{Name}' 尚未调用 Bake()，无法创建播放态。");

            return new ParticleEmitterPlayback(this);
        }

        /// <summary>仅限粒子工作线程调用：按本蓝图模块栈一次性分配并初始化 <paramref name="count"/> 个粒子（完全无状态）。</summary>
        internal void EmitBurstOnWorker(in ParticleEmitContext ctx, int count, ref ParticleRandom rng)
        {
            if (!_baked || _template is null || count <= 0)
                return;

            if (_maxStoreParticles > 0)
            {
                int budget = _maxStoreParticles - ctx.CurrentParticleCount;
                if (budget <= 0)
                    return;
                if (count > budget)
                    count = budget;
            }

            ParticleSpawnBatch batch = ctx.Allocate(_template, count);
            IParticleSpawnModule[] modules = _modules;
            for (int i = 0; i < modules.Length; i++)
                modules[i].Initialize(in batch, in ctx, ref rng);
        }

        private void EnsureMutable()
        {
            if (_baked)
                throw new InvalidOperationException($"粒子发射器 '{Name}' 已完成 Bake()，不可继续修改。");
        }

        private void ValidateModuleRequirements(IParticleSpawnModule[] modules, in ComponentTypes mergedTypes)
        {
            for (int i = 0; i < modules.Length; i++)
            {
                Type modType = modules[i].GetType();
                ModuleRequirementCacheEntry req = ModuleRequirementsCache.GetOrAdd(modType, BuildModuleRequirements);

                for (int r = 0; r < req.RequiredAll.Length; r++)
                {
                    if (!req.RequiredAll[r].Predicate(mergedTypes))
                        throw new InvalidOperationException($"粒子发射器 '{Name}' 中模块 '{modType.Name}' 依赖组件 {req.RequiredAll[r].Type.Name}，但未挂载提供该组件的模块。");
                }

                for (int r = 0; r < req.RequiredAny.Length; r++)
                {
                    Func<ComponentTypes, bool>[] preds = req.RequiredAny[r].Predicates;
                    bool satisfied = false;
                    for (int p = 0; p < preds.Length; p++)
                    {
                        if (preds[p](mergedTypes))
                        {
                            satisfied = true;
                            break;
                        }
                    }

                    if (!satisfied)
                        throw new InvalidOperationException($"粒子发射器 '{Name}' 中模块 '{modType.Name}' 依赖组件 {req.RequiredAny[r].DisplayNames}，但未挂载提供其中任一组件的模块。");
                }
            }
        }

        private static ModuleRequirementCacheEntry BuildModuleRequirements(Type modType)
        {
            var reqAllList = new List<(Type, Func<ComponentTypes, bool>)>();
            var reqAllSeen = new HashSet<Type>();
            foreach (RequiresComponentsAttribute attr in modType.GetCustomAttributes<RequiresComponentsAttribute>(inherit: true))
            {
                foreach (Type compType in attr.ComponentTypes)
                {
                    if (compType is null || !reqAllSeen.Add(compType))
                        continue;

                    Func<ComponentTypes, bool>? pred = GetComponentPresencePredicate(compType);
                    if (pred is not null)
                        reqAllList.Add((compType, pred));
                }
            }

            var reqAnyList = new List<(string, Func<ComponentTypes, bool>[])>();
            foreach (RequiresAnyComponentAttribute attr in modType.GetCustomAttributes<RequiresAnyComponentAttribute>(inherit: true))
            {
                var preds = new List<Func<ComponentTypes, bool>>();
                var names = new List<string>();
                foreach (Type compType in attr.ComponentTypes)
                {
                    if (compType is null)
                        continue;

                    Func<ComponentTypes, bool>? pred = GetComponentPresencePredicate(compType);
                    if (pred is not null)
                    {
                        preds.Add(pred);
                        names.Add(compType.Name);
                    }
                }

                if (preds.Count > 0)
                    reqAnyList.Add((string.Join(" 或 ", names), preds.ToArray()));
            }

            return new ModuleRequirementCacheEntry(reqAllList.ToArray(), reqAnyList.ToArray());
        }

        private static Func<ComponentTypes, bool>? GetComponentPresencePredicate(Type compType)
        {
            return ComponentPresencePredicates.GetOrAdd(compType, static t =>
            {
                if (!t.IsValueType || !typeof(IComponent).IsAssignableFrom(t) || t.ContainsGenericParameters)
                    return null;

                MethodInfo closed = HasComponentMethodDef.MakeGenericMethod(t);
                return (Func<ComponentTypes, bool>)Delegate.CreateDelegate(typeof(Func<ComponentTypes, bool>), closed);
            });
        }

        private static bool HasComponentGeneric<T>(ComponentTypes types) where T : struct, IComponent => types.Has<T>();

        /// <summary>不可变 <see cref="ParticleEmitter"/> 蓝图对应的独立工作线程播放态，仅持有每实例计时游标与生成规则状态。</summary>
        private sealed class ParticleEmitterPlayback : IParticleEmitter
        {
            private readonly ParticleEmitter _blueprint;
            private readonly object?[]? _ruleStates;

            private float _delayElapsed;
            private float _elapsed;
            private bool _hasTicked;
            private bool _completed;

            public bool IsCompleted => _completed;

            public ParticleEmitterPlayback(ParticleEmitter blueprint)
            {
                _blueprint = blueprint;
                IParticleSpawnRule[] rules = blueprint._rules;
                if (rules.Length > 0)
                {
                    object?[]? states = null;
                    for (int i = 0; i < rules.Length; i++)
                    {
                        object? state = rules[i].CreateState();
                        if (state is not null)
                        {
                            states ??= new object?[rules.Length];
                            states[i] = state;
                        }
                    }

                    _ruleStates = states;
                }
            }

            public void Reset()
            {
                _delayElapsed = 0f;
                _elapsed = 0f;
                _hasTicked = false;
                _completed = false;

                if (_ruleStates is not null)
                {
                    IParticleSpawnRule[] rules = _blueprint._rules;
                    for (int i = 0; i < rules.Length; i++)
                        rules[i].ResetState(_ruleStates[i]);
                }
            }

            public void EmitBurst(in ParticleEmitContext ctx, int count, ref ParticleRandom rng)
                => _blueprint.EmitBurstOnWorker(in ctx, count, ref rng);

            public void Tick(in ParticleEmitContext ctx, float deltaTime, ref ParticleRandom rng)
            {
                if (_completed || !(deltaTime > 0f) || !float.IsFinite(deltaTime))
                    return;

                float delay = _blueprint._startDelay;
                if (_delayElapsed < delay)
                {
                    float remainDelay = delay - _delayElapsed;
                    if (deltaTime <= remainDelay)
                    {
                        _delayElapsed += deltaTime;
                        return;
                    }

                    _delayElapsed = delay;
                    deltaTime -= remainDelay;
                }

                float duration = _blueprint._duration;
                bool loop = _blueprint._loop;
                long emissionCap = _blueprint._maxParticlesPerTick > 0 ? _blueprint._maxParticlesPerTick : int.MaxValue;
                long totalToEmit = 0;

                if (!loop && duration == 0f)
                {
                    totalToEmit += EvaluateRules(0f, 0f, deltaTime, includeStart: true, emissionCap, in ctx, ref rng);
                    _hasTicked = true;
                    _completed = true;
                }
                else if (!loop)
                {
                    float prev = _elapsed;
                    float toEnd = duration - prev;
                    bool reachedEnd = deltaTime >= toEnd;
                    float stepDt = reachedEnd ? toEnd : deltaTime;
                    float next = reachedEnd ? duration : prev + deltaTime;

                    if (stepDt > 0f || !_hasTicked)
                        totalToEmit += EvaluateRules(prev, next, stepDt, includeStart: !_hasTicked, emissionCap, in ctx, ref rng);

                    _elapsed = next;
                    _hasTicked = true;
                    if (reachedEnd)
                        _completed = true;
                }
                else if (duration == 0f)
                {
                    float prev = _elapsed;
                    float next = prev + deltaTime;
                    totalToEmit += EvaluateRules(prev, next, deltaTime, includeStart: !_hasTicked, emissionCap, in ctx, ref rng);
                    _elapsed = next;
                    _hasTicked = true;
                }
                else
                {
                    float remaining = deltaTime;
                    int wraps = 0;
                    while (remaining > 0f && wraps < MaxLoopWrapsPerTick)
                    {
                        float prev = _elapsed;
                        float toEnd = duration - prev;
                        bool reachedEnd = remaining >= toEnd;
                        float stepDt = reachedEnd ? toEnd : remaining;
                        float next = reachedEnd ? duration : prev + stepDt;

                        // 按本步剩余预算递减传入，触达单步上限后仅推进时间游标不再多余求值或空耗 RNG
                        long remainingCap = emissionCap - totalToEmit;
                        if (remainingCap > 0)
                            totalToEmit += EvaluateRules(prev, next, stepDt, includeStart: !_hasTicked, remainingCap, in ctx, ref rng);

                        remaining = reachedEnd ? remaining - toEnd : 0f;
                        if (reachedEnd)
                        {
                            _elapsed = 0f;
                            _hasTicked = false;
                            wraps++;
                        }
                        else
                        {
                            _elapsed = next;
                            _hasTicked = true;
                        }
                    }
                }

                if (totalToEmit > emissionCap)
                    totalToEmit = emissionCap;

                if (totalToEmit > 0)
                    _blueprint.EmitBurstOnWorker(in ctx, (int)totalToEmit, ref rng);
            }

            private long EvaluateRules(float prevTime, float nextTime, float stepDt, bool includeStart, long emissionCap, in ParticleEmitContext emitCtx, ref ParticleRandom rng)
            {
                IParticleSpawnRule[] rules = _blueprint._rules;
                if (rules.Length == 0 || emissionCap <= 0)
                    return 0;

                ParticleOriginSnapshot origin = emitCtx.Origin;
                long total = 0;

                for (int i = 0; i < rules.Length; i++)
                {
                    long rem = emissionCap - total;
                    if (rem <= 0)
                        return emissionCap;

                    var ruleCtx = new ParticleSpawnRuleContext(prevTime, nextTime, stepDt, includeStart, emitCtx.EmissionRateScale, rem, in origin);
                    object? state = _ruleStates is not null ? _ruleStates[i] : null;
                    long count = rules[i].Evaluate(in ruleCtx, state, ref rng);
                    if (count > 0)
                    {
                        total += count;
                        if (total >= emissionCap)
                            return emissionCap;
                    }
                }

                return total;
            }
        }
    }
}
