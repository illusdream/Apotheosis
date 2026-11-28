#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Apotheosis.Runtime.Foundation;
using Apotheosis.Runtime.Foundation.Collections;
using Apotheosis.Runtime.Particles.Emitters;
using Apotheosis.Runtime.Particles.Fields;
using Apotheosis.Runtime.Particles.Fragments;
using log4net;

namespace Apotheosis.Runtime.Particles.Core
{
    /// <summary>粒子工作线程单帧只读运行统计快照。</summary>
    public readonly struct ParticleRuntimeStats
    {
        public readonly int GlobalParticles;
        public readonly int ActiveInstances;
        public readonly int InstanceParticles;
        public readonly int PooledStoreSlots;
        public readonly ulong WorkerSteps;
        public readonly double LastStepMs;

        public int TotalParticles => GlobalParticles + InstanceParticles;

        public ParticleRuntimeStats(int globalParticles, int activeInstances, int instanceParticles, int pooledStoreSlots, ulong workerSteps, double lastStepMs)
        {
            GlobalParticles = globalParticles;
            ActiveInstances = activeInstances;
            InstanceParticles = instanceParticles;
            PooledStoreSlots = pooledStoreSlots;
            WorkerSteps = workerSteps;
            LastStepMs = lastStepMs;
        }
    }

    /// <summary>粒子工作线程步进周期回调接口（供后续步骤注册模拟管线、渲染打包与主线程提交）。</summary>
    internal interface IParticleRuntimePipeline
    {
        /// <summary>在工作线程中执行单步模拟（依次驱动全局存储与全部活跃子系统）。</summary>
        void StepSimulation(ParticleStoreSlot globalSlot, ReadOnlySpan<ParticleInstance> activeInstances, float deltaTime, float simulationTime,
            ReadOnlySpan<ForceFieldData> globalFields, bool isWorldPaused, bool isGameMenu);

        /// <summary>在工作线程中打包最新渲染数据并发布至三缓冲。</summary>
        void PackRenderSnapshot(ParticleStoreSlot globalSlot, ReadOnlySpan<ParticleInstance> activeInstances);

        /// <summary>在主线程渲染前从三缓冲读取最新快照并向 <c>RenderManager.Commands</c> 提交命令。</summary>
        void SubmitOnMainThread();

        /// <summary>在工作线程清空世界数据时向三缓冲发布空快照（严禁触碰主线程正在绘制的 <c>Reading</c> 槽）。</summary>
        void ResetWorldBuffers();

        /// <summary>在 Mod 卸载且工作线程退出后释放管线资源（运行于 tML 后台卸载线程，GPU 资源须派发至主线程释放）。</summary>
        void DisposeResources();
    }

    /// <summary>粒子专用工作线程运行时，独占管理全部 <see cref="ParticleStoreSlot"/>、指令队列、实例对象池与安全停机栅栏。</summary>
    internal sealed class ParticleRuntime
    {
        private const int InitialRegistryCapacity = 64;
        private const int MaxPooledStoreSlots = 128;
        private const int MaxPooledInstances = 128;
        private const int WorkerJoinTimeoutMs = 3000;
        private const int ResetBarrierTimeoutMs = 3000;
        private const int MaxLoggedWorkerErrors = 10;

        private enum ControlCommandKind : byte
        {
            CreateInstance = 0,
            StopInstance = 1,
            DestroyInstance = 2,
            CustomWorkerAction = 3,
            AttachEmitter = 4,
            ClearEmitters = 5,
            EmitGlobal = 6,
            EmitToInstance = 7,
            SetEmitterRateScale = 8,
            SetEmitterEnabled = 9,
            DetachEmitter = 10,
            TriggerEmitterBurst = 11,
            SetInstanceTimeScale = 12,
            AddForceField = 13,
            UpdateForceField = 14,
            RemoveForceField = 15,
            ClearGlobalForceFields = 16,
        }

        private struct ControlCommand
        {
            public ControlCommandKind Kind;
            public ParticleUpdateDomain Domain;
            public ParticleSystemHandle Handle;
            public ParticleInstanceConfig Config;
            public ParticleOriginSnapshot InitialSnapshot;
            public ParticleHostLostPolicy DestroyPolicy;
            public uint EmitterId;
            public uint FieldId;
            public int EmitCount;
            public float FloatValue;
            public bool BoolFlag;
            public ForceFieldData FieldData;
            public ParticleEmitter? Blueprint;
            public IParticleEmitter? Emitter;
            public ParticleTemplate? Template;
            public ParticleBatchInitializer? Initializer;
            public Action<ParticleRuntime>? CustomAction;
        }

        private struct OriginUpdateItem
        {
            public ParticleSystemHandle Handle;
            public ParticleOriginSnapshot Snapshot;
        }

        private struct CustomSampleItem
        {
            public ParticleSystemHandle Handle;
            public ParticleOriginBinding Binding;
            public ParticleOriginSnapshot Previous;
            public ParticleOriginSnapshot Sampled;
        }

        private struct RegistrySlot
        {
            public bool Active;
            public uint Generation;
            public int ActiveListIndex;
            public ParticleUpdateDomain UpdateDomain;
            public ParticleOriginBinding OriginBinding;
            public ParticleOriginSnapshot LastSnapshot;
        }

        private sealed class FramePacket
        {
            public float DeltaTime;
            public bool IsWorldPaused;
            public bool IsGameMenu;
            public bool HasPendingTick;
            public ValueBuffer<OriginUpdateItem> OriginUpdates = new(32, 32);
        }

        // ==================== 主线程侧注册表与通信队列 ====================

        private readonly object _registryLock = new();
        private RegistrySlot[] _registry = new RegistrySlot[InitialRegistryCapacity];
        private readonly Stack<ushort> _freeSlotIds = new();
        private readonly List<ushort> _activeSlotIds = new(32);
        private ushort _nextFreshSlotId = 1;
        private uint _nextEmitterId;
        private uint _nextFieldId;
        private ValueBuffer<CustomSampleItem> _customSampleScratch = new(8, 8);

        private readonly object _commandLock = new();
        private ValueBuffer<ControlCommand> _pendingCommands = new(64, 64);
        private ValueBuffer<ControlCommand> _workerCommands = new(64, 64);

        // 三帧包轮换：主线程写 _stagingPacket 后与 _submittedPacket 互换，工作线程换出到 _workerPacket 消费
        private readonly object _framePacketLock = new();
        private FramePacket _stagingPacket = new();
        private FramePacket _submittedPacket = new();
        private FramePacket _workerPacket = new();

        // ==================== 工作线程独占状态 ====================

        private readonly ParticleStoreSlot _globalWorldSlot = new(initialEntityCapacity: 512);
        private readonly BoundedObjectPool<ParticleStoreSlot> _storeSlotPool = new(8, MaxPooledStoreSlots, static () => new ParticleStoreSlot(64));
        private readonly BoundedObjectPool<ParticleInstance> _instancePool = new(8, MaxPooledInstances, static () => new ParticleInstance());
        private readonly Dictionary<ushort, ParticleInstance> _workerInstanceMap = new(32);
        private readonly List<ParticleInstance> _activeInstances = new(32);
        private readonly List<ActiveForceFieldEntry> _globalFields = new(8);
        private ValueBuffer<ForceFieldData> _globalFieldDataCache = new(8, 8);
        private bool _globalFieldsDirty;
        private float _simulationTime;
        private readonly Action<string, Exception> _logWorkerExceptionCallback;
        private ParticleRandom _workerRng = new(ParticleRandom.NextSeed());

        private volatile IParticleRuntimePipeline? _pipeline;
        private ILog? _logger;
        private int _loggedErrorCount;

        public ParticleRuntime()
        {
            _logWorkerExceptionCallback = LogWorkerException;
        }

        // ==================== 线程同步与无 Dispose 停机/重置栅栏 ====================

        private readonly object _signalLock = new();
        private bool _hasWakeSignal;
        private long _requestedResetVersion;
        private long _completedResetVersion;
        private bool _workerAlive;

        private Thread? _workerThread;
        private volatile bool _started;
        private volatile bool _stopRequested;
        private volatile bool _disposed;

        // ==================== 只读遥测快照 ====================

        private readonly object _statsLock = new();
        private ParticleRuntimeStats _stats;
        private ulong _workerSteps;
        private double _lastStepMs;

        /// <summary>工作线程独占的常驻全局世界存储槽（仅限工作线程内部或自定义工作线程指令访问）。</summary>
        internal ParticleStoreSlot GlobalWorldSlot => _globalWorldSlot;

        /// <summary>配置日志输出接口（可选）。</summary>
        public void SetLogger(ILog? logger) => _logger = logger;

        /// <summary>获取最近一次工作线程步进的统计快照（线程安全）。</summary>
        public ParticleRuntimeStats GetStats()
        {
            lock (_statsLock)
                return _stats;
        }

        /// <summary>注册模拟与渲染管线实现（供后续步骤装配 System）。</summary>
        public void SetPipeline(IParticleRuntimePipeline? pipeline) => _pipeline = pipeline;

        /// <summary>启动粒子专用后台工作线程。</summary>
        public void Start()
        {
            lock (_signalLock)
            {
                if (_started || _disposed)
                    return;

                _stopRequested = false;
                _hasWakeSignal = false;
                _requestedResetVersion = 0;
                _completedResetVersion = 0;
                _workerAlive = true;

                var thread = new Thread(WorkerLoop)
                {
                    Name = "Apotheosis.ParticleWorker",
                    IsBackground = true,
                };
                _workerThread = thread;
                _started = true;
                thread.Start();
            }
        }

        /// <summary>创建小型独立粒子系统并同步返回代际安全句柄（绑定 <see cref="ParticleOriginKind.TransformNode"/> 或 <see cref="ParticleOriginKind.Custom"/> 时须在主线程调用）。</summary>
        public ParticleSystemHandle CreateInstance(in ParticleInstanceConfig config)
        {
            if (!_started || _stopRequested || _disposed)
                return ParticleSystemHandle.Invalid;

#if DEBUG
            if (config.Origin.Kind != ParticleOriginKind.Static)
                ParticleWorldSystem.AssertMainThread("ParticleRuntime.CreateInstance");
#endif

            ParticleOriginSnapshot initialSnapshot = config.Origin.SampleOnMainThread(in ParticleOriginSnapshot.Identity);
            ParticleSystemHandle handle;

            lock (_registryLock)
            {
                if (_stopRequested || _disposed)
                    return ParticleSystemHandle.Invalid;

                ushort slotId;
                if (_freeSlotIds.Count > 0)
                {
                    slotId = _freeSlotIds.Pop();
                }
                else
                {
                    if (_nextFreshSlotId == ushort.MaxValue)
                        throw new InvalidOperationException($"小型独立粒子系统数量已达上限 ({ushort.MaxValue - 1})。");

                    slotId = _nextFreshSlotId++;
                    if (slotId >= _registry.Length)
                        Array.Resize(ref _registry, Math.Min(_registry.Length * 2, ushort.MaxValue));
                }

                ref RegistrySlot slot = ref _registry[slotId];
                uint nextGen = slot.Generation + 1;
                if (nextGen == RuntimeConstants.IndexNone)
                    nextGen = RuntimeConstants.InitialGeneration;

                slot.Active = true;
                slot.Generation = nextGen;
                slot.ActiveListIndex = _activeSlotIds.Count;
                slot.UpdateDomain = config.UpdateDomain;
                slot.OriginBinding = config.Origin;
                slot.LastSnapshot = initialSnapshot;
                _activeSlotIds.Add(slotId);

                handle = new ParticleSystemHandle(slotId, nextGen);
            }

            bool enqueued = EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.CreateInstance,
                Domain = config.UpdateDomain,
                Handle = handle,
                Config = config,
                InitialSnapshot = initialSnapshot,
            });

            if (!enqueued)
            {
                // 若入队瞬间遇停机，立刻回滚注册表槽位避免返回幽灵句柄
                ReleaseRegistrySlot(handle);
                return ParticleSystemHandle.Invalid;
            }

            return handle;
        }

        /// <summary>检查句柄当前是否仍处于有效活跃状态。</summary>
        public bool IsInstanceAlive(ParticleSystemHandle handle)
        {
            if (!handle.IsValid)
                return false;

            lock (_registryLock)
            {
                if (handle.SlotId >= _registry.Length)
                    return false;

                ref readonly RegistrySlot slot = ref _registry[handle.SlotId];
                return slot.Active && slot.Generation == handle.Generation;
            }
        }

        /// <summary>请求停止指定子系统继续发射（残留粒子将按策略消亡后自动回收）。</summary>
        public void StopInstance(ParticleSystemHandle handle)
        {
            if (!TryGetRegisteredDomain(handle, out ParticleUpdateDomain domain) || _stopRequested || _disposed)
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.StopInstance,
                Domain = domain,
                Handle = handle,
            });
        }

        /// <summary>请求按指定策略销毁指定小型独立粒子系统。</summary>
        public void DestroyInstance(ParticleSystemHandle handle, ParticleHostLostPolicy policy = ParticleHostLostPolicy.StopAndFade)
        {
            if (!TryGetRegisteredDomain(handle, out ParticleUpdateDomain domain) || _stopRequested || _disposed)
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.DestroyInstance,
                Domain = domain,
                Handle = handle,
                DestroyPolicy = policy,
            });
        }

        /// <summary>动态调整指定小型独立粒子子系统的时间流速倍率。</summary>
        public void SetInstanceTimeScale(ParticleSystemHandle handle, float timeScale)
        {
            if (!float.IsFinite(timeScale) || timeScale < 0f || !TryGetRegisteredDomain(handle, out ParticleUpdateDomain domain) || _stopRequested || _disposed)
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.SetInstanceTimeScale,
                Domain = domain,
                Handle = handle,
                FloatValue = timeScale,
            });
        }

        /// <summary>向指定活跃子系统挂载不可变 <see cref="ParticleEmitter"/> 蓝图并同步返回单个发射器实例句柄。</summary>
        public ParticleEmitterHandle AttachEmitter(ParticleSystemHandle handle, ParticleEmitter blueprint, bool autoStopWhenComplete = false)
        {
            ArgumentNullException.ThrowIfNull(blueprint);
            if (!blueprint.IsBaked)
                throw new InvalidOperationException($"粒子发射器 '{blueprint.Name}' 尚未调用 Bake()，无法挂载。");
            if (!TryGetRegisteredDomain(handle, out ParticleUpdateDomain domain) || _stopRequested || _disposed)
                return ParticleEmitterHandle.Invalid;

            uint emitterId = AllocateEmitterId();
            bool enqueued = EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.AttachEmitter,
                Domain = domain,
                Handle = handle,
                EmitterId = emitterId,
                Blueprint = blueprint,
                BoolFlag = autoStopWhenComplete,
            });

            return enqueued ? new ParticleEmitterHandle(handle, emitterId) : ParticleEmitterHandle.Invalid;
        }

        /// <summary>向指定活跃子系统挂载底层自定义 <see cref="IParticleEmitter"/> 实例并同步返回发射器实例句柄。</summary>
        public ParticleEmitterHandle AttachEmitter(ParticleSystemHandle handle, IParticleEmitter emitter, bool autoStopWhenComplete = false)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            if (!TryGetRegisteredDomain(handle, out ParticleUpdateDomain domain) || _stopRequested || _disposed)
                return ParticleEmitterHandle.Invalid;

            uint emitterId = AllocateEmitterId();
            bool enqueued = EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.AttachEmitter,
                Domain = domain,
                Handle = handle,
                EmitterId = emitterId,
                Emitter = emitter,
                BoolFlag = autoStopWhenComplete,
            });

            return enqueued ? new ParticleEmitterHandle(handle, emitterId) : ParticleEmitterHandle.Invalid;
        }

        /// <summary>动态调整单个已挂载发射器的独立发射速率倍率（不影响同子系统内其他发射器）。</summary>
        public void SetEmitterRateScale(ParticleEmitterHandle emitterHandle, float rateScale)
        {
            if (!emitterHandle.IsValid || !float.IsFinite(rateScale) || rateScale < 0f
                || !TryGetRegisteredDomain(emitterHandle.SystemHandle, out ParticleUpdateDomain domain) || _stopRequested || _disposed)
            {
                return;
            }

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.SetEmitterRateScale,
                Domain = domain,
                Handle = emitterHandle.SystemHandle,
                EmitterId = emitterHandle.EmitterId,
                FloatValue = rateScale,
            });
        }

        /// <summary>独立启用或暂停单个已挂载发射器（不影响同子系统内其他发射器）。</summary>
        public void SetEmitterEnabled(ParticleEmitterHandle emitterHandle, bool enabled)
        {
            if (!emitterHandle.IsValid || !TryGetRegisteredDomain(emitterHandle.SystemHandle, out ParticleUpdateDomain domain) || _stopRequested || _disposed)
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.SetEmitterEnabled,
                Domain = domain,
                Handle = emitterHandle.SystemHandle,
                EmitterId = emitterHandle.EmitterId,
                BoolFlag = enabled,
            });
        }

        /// <summary>从所属子系统中独立摘除单个已挂载发射器。</summary>
        public void DetachEmitter(ParticleEmitterHandle emitterHandle)
        {
            if (!emitterHandle.IsValid || !TryGetRegisteredDomain(emitterHandle.SystemHandle, out ParticleUpdateDomain domain) || _stopRequested || _disposed)
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.DetachEmitter,
                Domain = domain,
                Handle = emitterHandle.SystemHandle,
                EmitterId = emitterHandle.EmitterId,
            });
        }

        /// <summary>触发单个已挂载发射器在所属子系统中立即额外爆发指定数量的粒子。</summary>
        public void TriggerEmitterBurst(ParticleEmitterHandle emitterHandle, int count)
        {
            if (count <= 0 || !emitterHandle.IsValid || !TryGetRegisteredDomain(emitterHandle.SystemHandle, out ParticleUpdateDomain domain) || _stopRequested || _disposed)
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.TriggerEmitterBurst,
                Domain = domain,
                Handle = emitterHandle.SystemHandle,
                EmitterId = emitterHandle.EmitterId,
                EmitCount = count,
            });
        }

        /// <summary>清空指定子系统上已挂载的全部发射器。</summary>
        public void ClearEmitters(ParticleSystemHandle handle)
        {
            if (!TryGetRegisteredDomain(handle, out ParticleUpdateDomain domain) || _stopRequested || _disposed)
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.ClearEmitters,
                Domain = domain,
                Handle = handle,
            });
        }

        /// <summary>向常驻全局世界存储投递一次基于不可变 <see cref="ParticleEmitter"/> 蓝图的爆发指令（零闭包分配）。</summary>
        public void EmitGlobal(ParticleEmitter blueprint, int count, in ParticleOriginSnapshot origin)
        {
            ArgumentNullException.ThrowIfNull(blueprint);
            if (!blueprint.IsBaked)
                throw new InvalidOperationException($"粒子发射器 '{blueprint.Name}' 尚未调用 Bake()，无法发射粒子。");
            if (count <= 0 || !_started || _stopRequested || _disposed)
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.EmitGlobal,
                Domain = ParticleUpdateDomain.World,
                Blueprint = blueprint,
                EmitCount = count,
                InitialSnapshot = origin,
            });
        }

        /// <summary>向常驻全局世界存储投递一次基于底层 <see cref="IParticleEmitter"/> 的爆发指令（零闭包分配）。</summary>
        public void EmitGlobal(IParticleEmitter emitter, int count, in ParticleOriginSnapshot origin)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            if (count <= 0 || !_started || _stopRequested || _disposed)
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.EmitGlobal,
                Domain = ParticleUpdateDomain.World,
                Emitter = emitter,
                EmitCount = count,
                InitialSnapshot = origin,
            });
        }

        /// <summary>向常驻全局世界存储投递一次基于模板与自定义初始化委托的爆发指令（零闭包分配）。</summary>
        public void EmitGlobal(ParticleTemplate template, int count, in ParticleOriginSnapshot origin, ParticleBatchInitializer initializer)
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(initializer);
            if (!template.IsBaked)
                throw new InvalidOperationException($"粒子模板 '{template.Name}' 尚未调用 Bake()，无法发射粒子。");
            if (count <= 0 || !_started || _stopRequested || _disposed)
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.EmitGlobal,
                Domain = ParticleUpdateDomain.World,
                Template = template,
                Initializer = initializer,
                EmitCount = count,
                InitialSnapshot = origin,
            });
        }

        /// <summary>向指定小型独立粒子子系统投递一次基于不可变 <see cref="ParticleEmitter"/> 蓝图的爆发指令（零闭包分配）。</summary>
        public void EmitToInstance(ParticleSystemHandle handle, ParticleEmitter blueprint, int count)
        {
            ArgumentNullException.ThrowIfNull(blueprint);
            if (!blueprint.IsBaked)
                throw new InvalidOperationException($"粒子发射器 '{blueprint.Name}' 尚未调用 Bake()，无法发射粒子。");
            if (count <= 0 || !TryGetRegisteredDomain(handle, out ParticleUpdateDomain domain) || _stopRequested || _disposed)
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.EmitToInstance,
                Domain = domain,
                Handle = handle,
                Blueprint = blueprint,
                EmitCount = count,
            });
        }

        /// <summary>向指定小型独立粒子子系统投递一次基于底层 <see cref="IParticleEmitter"/> 的爆发指令（零闭包分配）。</summary>
        public void EmitToInstance(ParticleSystemHandle handle, IParticleEmitter emitter, int count)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            if (count <= 0 || !TryGetRegisteredDomain(handle, out ParticleUpdateDomain domain) || _stopRequested || _disposed)
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.EmitToInstance,
                Domain = domain,
                Handle = handle,
                Emitter = emitter,
                EmitCount = count,
            });
        }

        /// <summary>向指定小型独立粒子子系统投递一次基于模板与自定义初始化委托的爆发指令（零闭包分配）。</summary>
        public void EmitToInstance(ParticleSystemHandle handle, ParticleTemplate template, int count, ParticleBatchInitializer initializer)
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(initializer);
            if (!template.IsBaked)
                throw new InvalidOperationException($"粒子模板 '{template.Name}' 尚未调用 Bake()，无法发射粒子。");
            if (count <= 0 || !TryGetRegisteredDomain(handle, out ParticleUpdateDomain domain) || _stopRequested || _disposed)
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.EmitToInstance,
                Domain = domain,
                Handle = handle,
                Template = template,
                Initializer = initializer,
                EmitCount = count,
            });
        }

        /// <summary>向全局世界注册一个力场并返回句柄（<paramref name="duration"/> &gt; 0 时到期自动移除，世界卸载自动清空）。</summary>
        public ForceFieldHandle AddGlobalForceField(in ForceFieldData data, float duration = 0f)
        {
            if (!_started || _stopRequested || _disposed)
                return ForceFieldHandle.Invalid;

            uint fieldId = AllocateFieldId();
            bool enqueued = EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.AddForceField,
                Domain = ParticleUpdateDomain.World,
                Handle = ParticleSystemHandle.Invalid,
                FieldId = fieldId,
                FieldData = data,
                FloatValue = duration,
            });

            return enqueued ? new ForceFieldHandle(ParticleSystemHandle.Invalid, fieldId) : ForceFieldHandle.Invalid;
        }

        /// <summary>向指定小型独立粒子子系统挂载一个专属局部力场并返回句柄（随子系统回收自动清除）。</summary>
        public ForceFieldHandle AddInstanceForceField(ParticleSystemHandle instanceHandle, in ForceFieldData data, float duration = 0f)
        {
            if (!TryGetRegisteredDomain(instanceHandle, out ParticleUpdateDomain domain) || _stopRequested || _disposed)
                return ForceFieldHandle.Invalid;

            uint fieldId = AllocateFieldId();
            bool enqueued = EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.AddForceField,
                Domain = domain,
                Handle = instanceHandle,
                FieldId = fieldId,
                FieldData = data,
                FloatValue = duration,
            });

            return enqueued ? new ForceFieldHandle(instanceHandle, fieldId) : ForceFieldHandle.Invalid;
        }

        /// <summary>实时更新指定全局或局部力场的参数数据（例如跟随 Boss/玩家移动的引力井或旋涡）。</summary>
        public void UpdateForceField(ForceFieldHandle handle, in ForceFieldData data)
        {
            if (!handle.IsValid || !_started || _stopRequested || _disposed)
                return;

            ParticleUpdateDomain domain = ParticleUpdateDomain.World;
            if (handle.IsInstanceLocal && !TryGetRegisteredDomain(handle.SystemHandle, out domain))
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.UpdateForceField,
                Domain = domain,
                Handle = handle.SystemHandle,
                FieldId = handle.FieldId,
                FieldData = data,
            });
        }

        /// <summary>移除指定的全局或局部力场。</summary>
        public void RemoveForceField(ForceFieldHandle handle)
        {
            if (!handle.IsValid || !_started || _stopRequested || _disposed)
                return;

            ParticleUpdateDomain domain = ParticleUpdateDomain.World;
            if (handle.IsInstanceLocal && !TryGetRegisteredDomain(handle.SystemHandle, out domain))
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.RemoveForceField,
                Domain = domain,
                Handle = handle.SystemHandle,
                FieldId = handle.FieldId,
            });
        }

        /// <summary>立即清空全部全局世界力场。</summary>
        public void ClearGlobalForceFields()
        {
            if (!_started || _stopRequested || _disposed)
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.ClearGlobalForceFields,
                Domain = ParticleUpdateDomain.World,
            });
        }

        /// <summary>投递一个将在粒子工作线程独占执行的自定义操作（用于后续发射与迁移指令扩展）。</summary>
        internal void EnqueueWorkerAction(Action<ParticleRuntime> action, ParticleUpdateDomain domain = ParticleUpdateDomain.World)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (!_started || _stopRequested || _disposed)
                return;

            EnqueueControlCommand(new ControlCommand
            {
                Kind = ControlCommandKind.CustomWorkerAction,
                Domain = domain,
                CustomAction = action,
            });
        }

        /// <summary>仅限粒子工作线程调用：按句柄查找当前活跃的 <see cref="ParticleInstance"/>。</summary>
        internal bool TryGetWorkerInstance(ParticleSystemHandle handle, out ParticleInstance? instance)
        {
            if (handle.IsValid && _workerInstanceMap.TryGetValue(handle.SlotId, out ParticleInstance? found) && found.Handle == handle)
            {
                instance = found;
                return true;
            }

            instance = null;
            return false;
        }

        /// <summary>主线程每逻辑帧采集活跃原点快照并唤醒工作线程推进一帧。</summary>
        public void TickFromMainThread(float deltaTime, bool isWorldPaused, bool isGameMenu)
        {
            if (!_started || _stopRequested || _disposed)
                return;

#if DEBUG
            ParticleWorldSystem.AssertMainThread("ParticleRuntime.TickFromMainThread");
#endif

            _stagingPacket.DeltaTime = deltaTime;
            _stagingPacket.IsWorldPaused = isWorldPaused;
            _stagingPacket.IsGameMenu = isGameMenu;
            _stagingPacket.HasPendingTick = true;
            _stagingPacket.OriginUpdates.Clear(clearReferences: false);
            _customSampleScratch.Clear(clearReferences: true);

            // Static 原点无需每帧重算逆矩阵；TransformNode 直接采样；Custom 委托拷贝到暂存表后锁外执行，杜绝持锁重入或单点异常
            lock (_registryLock)
            {
                for (int i = 0; i < _activeSlotIds.Count; i++)
                {
                    ushort slotId = _activeSlotIds[i];
                    ref RegistrySlot reg = ref _registry[slotId];
                    if (!reg.Active)
                        continue;

                    ParticleOriginKind kind = reg.OriginBinding.Kind;
                    if (kind == ParticleOriginKind.Static)
                        continue;

                    var handle = new ParticleSystemHandle(slotId, reg.Generation);
                    if (kind == ParticleOriginKind.Custom)
                    {
                        var item = new CustomSampleItem { Handle = handle, Binding = reg.OriginBinding, Previous = reg.LastSnapshot };
                        _customSampleScratch.Add(in item);
                        continue;
                    }

                    ParticleOriginSnapshot sampled = reg.OriginBinding.SampleOnMainThread(in reg.LastSnapshot);
                    reg.LastSnapshot = sampled;
                    var update = new OriginUpdateItem { Handle = handle, Snapshot = sampled };
                    _stagingPacket.OriginUpdates.Add(in update);
                }
            }

            if (_customSampleScratch.Count > 0)
            {
                Span<CustomSampleItem> customSpan = _customSampleScratch.AsSpan();
                for (int i = 0; i < customSpan.Length; i++)
                {
                    ref CustomSampleItem item = ref customSpan[i];
                    item.Sampled = item.Binding.SampleOnMainThread(in item.Previous);
                    var update = new OriginUpdateItem { Handle = item.Handle, Snapshot = item.Sampled };
                    _stagingPacket.OriginUpdates.Add(in update);
                }

                lock (_registryLock)
                {
                    for (int i = 0; i < customSpan.Length; i++)
                    {
                        ref readonly CustomSampleItem item = ref customSpan[i];
                        if (item.Handle.SlotId >= _registry.Length)
                            continue;

                        ref RegistrySlot reg = ref _registry[item.Handle.SlotId];
                        if (reg.Active && reg.Generation == item.Handle.Generation)
                            reg.LastSnapshot = item.Sampled;
                    }
                }

                _customSampleScratch.Clear(clearReferences: true);
            }

            lock (_framePacketLock)
                (_stagingPacket, _submittedPacket) = (_submittedPacket, _stagingPacket);

            WakeWorkerSafe();
        }

        /// <summary>主线程渲染前入口：委托已注册管线无等待读取三缓冲并提交渲染命令。</summary>
        public void SubmitRenderOnMainThread()
        {
            if (!_started || _stopRequested || _disposed)
                return;

            _pipeline?.SubmitOnMainThread();
        }

        /// <summary>世界卸载同步栅栏：等待工作线程安全清空全局存储与全部世界域子系统（可从后台线程调用）。</summary>
        public void ResetWorldAndWait()
        {
            bool timedOut = false;
            lock (_signalLock)
            {
                if (!_workerAlive || _stopRequested || _disposed)
                    return;

                long targetVersion = ++_requestedResetVersion;
                _hasWakeSignal = true;
                Monitor.PulseAll(_signalLock);

                // 按单调递增版本号等待，杜绝重入提前返回；停机或工作线程退出时立即唤醒避免永久死等
                long deadline = Environment.TickCount64 + ResetBarrierTimeoutMs;
                while (_workerAlive && !_stopRequested && _completedResetVersion < targetVersion)
                {
                    long remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0)
                    {
                        timedOut = true;
                        break;
                    }

                    Monitor.Wait(_signalLock, (int)remaining);
                }
            }

            if (timedOut)
                _logger?.Warn($"[Particles] 世界卸载重置栅栏等待超过 {ResetBarrierTimeoutMs}ms 未完成。");
        }

        /// <summary>Mod 卸载安全停机栅栏：等待工作线程退出并释放全部托管资源（幂等）。</summary>
        public void ShutdownAndWait()
        {
            Thread? worker;
            lock (_signalLock)
            {
                if (_stopRequested || _disposed)
                    return;

                _stopRequested = true;
                _hasWakeSignal = true;
                worker = _workerThread;
                Monitor.PulseAll(_signalLock);
            }

            bool workerExited = worker is not { IsAlive: true } || worker.Join(WorkerJoinTimeoutMs);

            lock (_signalLock)
            {
                _workerThread = null;
                _started = false;
                _disposed = true;
                Monitor.PulseAll(_signalLock);
            }

            if (!workerExited)
            {
                // 超时后工作线程仍存活：跳过清理其独占状态，防止与在途线程并发崩溃
                _logger?.Warn($"[Particles] 粒子工作线程在 {WorkerJoinTimeoutMs}ms 内未退出，已跳过不安全清理。");
                return;
            }

            _pipeline?.DisposeResources();
            _pipeline = null;

            lock (_registryLock)
            {
                Array.Clear(_registry, 0, _registry.Length);
                _freeSlotIds.Clear();
                _activeSlotIds.Clear();
                _nextFreshSlotId = 1;
            }

            lock (_commandLock)
            {
                _pendingCommands.Clear(clearReferences: true);
                _workerCommands.Clear(clearReferences: true);
            }
        }

        // ==================== 粒子专用工作线程主循环 ====================

        private void WorkerLoop()
        {
            var stopwatch = new Stopwatch();

            try
            {
                while (true)
                {
                    long pendingResetTarget = 0;
                    lock (_signalLock)
                    {
                        // 空载时完全挂起，零 CPU 空转
                        while (!_stopRequested && !_hasWakeSignal && _completedResetVersion >= _requestedResetVersion)
                            Monitor.Wait(_signalLock);

                        if (_stopRequested)
                            break;

                        if (_completedResetVersion < _requestedResetVersion)
                            pendingResetTarget = _requestedResetVersion;

                        _hasWakeSignal = false;
                    }

                    if (pendingResetTarget > 0)
                    {
                        try
                        {
                            ExecuteWorldResetOnWorker();
                        }
                        catch (Exception ex)
                        {
                            LogWorkerException("ExecuteWorldResetOnWorker", ex);
                        }
                        finally
                        {
                            lock (_signalLock)
                            {
                                if (_completedResetVersion < pendingResetTarget)
                                    _completedResetVersion = pendingResetTarget;
                                Monitor.PulseAll(_signalLock);
                            }
                        }

                        continue;
                    }

                    try
                    {
                        stopwatch.Restart();
                        bool didStep = ExecuteSingleWorkerPass();
                        stopwatch.Stop();

                        if (didStep)
                            _lastStepMs = stopwatch.Elapsed.TotalMilliseconds;

                        PublishStats();
                    }
                    catch (Exception ex)
                    {
                        LogWorkerException("ExecuteSingleWorkerPass", ex);
                    }
                }
            }
            finally
            {
                try
                {
                    CleanupAllStoresOnWorker();
                }
                catch (Exception ex)
                {
                    LogWorkerException("CleanupAllStoresOnWorker", ex);
                }

                lock (_signalLock)
                {
                    _workerAlive = false;
                    _started = false;
                    _completedResetVersion = _requestedResetVersion;
                    Monitor.PulseAll(_signalLock);
                }
            }
        }

        private bool ExecuteSingleWorkerPass()
        {
            // 1. 原子摘取本轮主线程投递的全部控制与发射指令
            bool hadCommands = DrainPendingCommands();

            // 2. 原子摘取最新主线程逻辑帧包
            bool hasTick;
            lock (_framePacketLock)
            {
                hasTick = _submittedPacket.HasPendingTick;
                if (hasTick)
                {
                    (_submittedPacket, _workerPacket) = (_workerPacket, _submittedPacket);
                    _submittedPacket.HasPendingTick = false;
                }
            }

            // 3. 顺序执行指令（严格 FIFO：CreateInstance 先于同帧发射/停止指令生效）
            if (hadCommands)
                ProcessWorkerCommands(skipWorldDomain: false);

            if (!hasTick)
            {
                // 无逻辑步时也及时回放指令缓冲并回收立即销毁的子系统，避免槽位滞留至下一帧
                if (hadCommands)
                {
                    PlaybackAllCommandBuffers();
                    ReapFinishedInstances();
                }

                return false;
            }

            // 4. 应用主线程采样的原点快照，处理绑定目标失活策略
            ApplyOriginUpdates(_workerPacket.OriginUpdates.AsReadOnlySpan());

            // 5. 推进已挂载持续发射器（在最新原点快照落地后发射，使出生粒子与单次爆发同帧经历完整模拟管线）
            TickAttachedEmitters(_workerPacket.DeltaTime, _workerPacket.IsWorldPaused, _workerPacket.IsGameMenu);

            // 6. 推进模拟时钟（限时力场扣减排在 StepSimulation 之后，确保单帧冲击波脉冲至少完整生效 1 帧）
            AdvanceSimulationClock(_workerPacket.DeltaTime, _workerPacket.IsWorldPaused, _workerPacket.IsGameMenu);

            // 7. 驱动已注册的模拟管线、扣减限时力场寿命并打包渲染三缓冲
            IParticleRuntimePipeline? pipeline = _pipeline;
            if (pipeline is not null)
            {
                ReadOnlySpan<ParticleInstance> instancesSpan = CollectionsMarshal.AsSpan(_activeInstances);
                ReadOnlySpan<ForceFieldData> globalFieldsSpan = GetGlobalFieldsSpan();
                pipeline.StepSimulation(_globalWorldSlot, instancesSpan, _workerPacket.DeltaTime, _simulationTime,
                    globalFieldsSpan, _workerPacket.IsWorldPaused, _workerPacket.IsGameMenu);

                TickForceFieldDurations(_workerPacket.DeltaTime, _workerPacket.IsWorldPaused, _workerPacket.IsGameMenu);
                PlaybackAllCommandBuffers();
                ReapFinishedInstances();

                instancesSpan = CollectionsMarshal.AsSpan(_activeInstances);
                pipeline.PackRenderSnapshot(_globalWorldSlot, instancesSpan);
            }
            else
            {
                TickForceFieldDurations(_workerPacket.DeltaTime, _workerPacket.IsWorldPaused, _workerPacket.IsGameMenu);
                PlaybackAllCommandBuffers();
                ReapFinishedInstances();
            }

            _workerSteps++;
            return true;
        }

        private bool DrainPendingCommands()
        {
            _workerCommands.Clear(clearReferences: true);
            lock (_commandLock)
            {
                if (_pendingCommands.Count == 0)
                    return false;

                _workerCommands.AddRange(_pendingCommands.AsReadOnlySpan());
                _pendingCommands.Clear(clearReferences: true);
                return true;
            }
        }

        private void ProcessWorkerCommands(bool skipWorldDomain)
        {
            try
            {
                ReadOnlySpan<ControlCommand> cmds = _workerCommands.AsReadOnlySpan();
                for (int i = 0; i < cmds.Length; i++)
                {
                    ref readonly ControlCommand cmd = ref cmds[i];

                    // 世界重置期间：跳过并回收尚未落地的 World 域创建槽位，同时保留 Ui 域控制指令
                    if (skipWorldDomain && cmd.Domain == ParticleUpdateDomain.World)
                    {
                        if (cmd.Kind == ControlCommandKind.CreateInstance)
                            ReleaseRegistrySlot(cmd.Handle);
                        continue;
                    }

                    switch (cmd.Kind)
                    {
                        case ControlCommandKind.CreateInstance:
                        {
                            ParticleStoreSlot slot = _storeSlotPool.Rent();
                            ParticleInstance inst = _instancePool.Rent();
                            inst.Initialize(cmd.Handle, slot, in cmd.Config, in cmd.InitialSnapshot);
                            _workerInstanceMap[cmd.Handle.SlotId] = inst;
                            _activeInstances.Add(inst);

                            // 若传入的绑定原点在创建瞬间即已失活，立即按策略切换状态，避免首帧在 (0,0,0) 误发射
                            if (!cmd.InitialSnapshot.IsAlive)
                                ApplyDestroyPolicyOnWorker(inst, inst.HostLostPolicy);
                            break;
                        }

                        case ControlCommandKind.StopInstance:
                        {
                            if (TryGetWorkerInstance(cmd.Handle, out ParticleInstance? inst) && inst is not null && inst.State == ParticleInstanceState.Active)
                            {
                                inst.ClearEmittersOnWorker();
                                inst.State = ParticleInstanceState.Stopping;
                            }

                            break;
                        }

                        case ControlCommandKind.DestroyInstance:
                        {
                            if (TryGetWorkerInstance(cmd.Handle, out ParticleInstance? inst) && inst is not null)
                                ApplyDestroyPolicyOnWorker(inst, cmd.DestroyPolicy);
                            break;
                        }

                        case ControlCommandKind.SetInstanceTimeScale:
                        {
                            if (TryGetWorkerInstance(cmd.Handle, out ParticleInstance? inst) && inst is not null
                                && float.IsFinite(cmd.FloatValue) && cmd.FloatValue >= 0f)
                            {
                                inst.TimeScale = cmd.FloatValue;
                            }

                            break;
                        }

                        case ControlCommandKind.AttachEmitter:
                        {
                            if (TryGetWorkerInstance(cmd.Handle, out ParticleInstance? inst)
                                && inst is not null
                                && inst.State == ParticleInstanceState.Active)
                            {
                                try
                                {
                                    IParticleEmitter? playback = cmd.Blueprint is not null ? cmd.Blueprint.CreatePlayback() : cmd.Emitter;
                                    if (playback is not null)
                                        inst.AttachEmitterOnWorker(cmd.EmitterId, playback, cmd.BoolFlag);
                                }
                                catch (Exception ex)
                                {
                                    // 隔离自定义生成规则 CreateState 异常，避免中断同批控制指令
                                    LogWorkerException("AttachEmitter", ex);
                                }
                            }

                            break;
                        }

                        case ControlCommandKind.SetEmitterRateScale:
                        {
                            if (TryGetWorkerInstance(cmd.Handle, out ParticleInstance? inst) && inst is not null)
                                inst.SetEmitterRateScaleOnWorker(cmd.EmitterId, cmd.FloatValue);
                            break;
                        }

                        case ControlCommandKind.SetEmitterEnabled:
                        {
                            if (TryGetWorkerInstance(cmd.Handle, out ParticleInstance? inst) && inst is not null)
                                inst.SetEmitterEnabledOnWorker(cmd.EmitterId, cmd.BoolFlag);
                            break;
                        }

                        case ControlCommandKind.DetachEmitter:
                        {
                            if (TryGetWorkerInstance(cmd.Handle, out ParticleInstance? inst) && inst is not null)
                                inst.DetachEmitterOnWorker(cmd.EmitterId);
                            break;
                        }

                        case ControlCommandKind.TriggerEmitterBurst:
                        {
                            if (TryGetWorkerInstance(cmd.Handle, out ParticleInstance? inst) && inst is not null)
                                inst.TriggerEmitterBurstOnWorker(cmd.EmitterId, cmd.EmitCount, ref _workerRng, _logWorkerExceptionCallback);
                            break;
                        }

                        case ControlCommandKind.AddForceField:
                        {
                            if (!cmd.Handle.IsValid)
                            {
                                AddGlobalFieldOnWorker(cmd.FieldId, in cmd.FieldData, cmd.FloatValue);
                            }
                            else if (TryGetWorkerInstance(cmd.Handle, out ParticleInstance? inst) && inst is not null)
                            {
                                inst.AddLocalFieldOnWorker(cmd.FieldId, in cmd.FieldData, cmd.FloatValue);
                            }

                            break;
                        }

                        case ControlCommandKind.UpdateForceField:
                        {
                            if (!cmd.Handle.IsValid)
                            {
                                UpdateGlobalFieldOnWorker(cmd.FieldId, in cmd.FieldData);
                            }
                            else if (TryGetWorkerInstance(cmd.Handle, out ParticleInstance? inst) && inst is not null)
                            {
                                inst.UpdateLocalFieldOnWorker(cmd.FieldId, in cmd.FieldData);
                            }

                            break;
                        }

                        case ControlCommandKind.RemoveForceField:
                        {
                            if (!cmd.Handle.IsValid)
                            {
                                RemoveGlobalFieldOnWorker(cmd.FieldId);
                            }
                            else if (TryGetWorkerInstance(cmd.Handle, out ParticleInstance? inst) && inst is not null)
                            {
                                inst.RemoveLocalFieldOnWorker(cmd.FieldId);
                            }

                            break;
                        }

                        case ControlCommandKind.ClearGlobalForceFields:
                        {
                            ClearGlobalFieldsOnWorker();
                            break;
                        }

                        case ControlCommandKind.ClearEmitters:
                        {
                            if (TryGetWorkerInstance(cmd.Handle, out ParticleInstance? inst) && inst is not null)
                                inst.ClearEmittersOnWorker();
                            break;
                        }

                        case ControlCommandKind.EmitGlobal:
                        {
                            ExecuteEmitCommandOnWorker(_globalWorldSlot, isLocalSpace: false, in cmd.InitialSnapshot, in cmd, "EmitGlobal");
                            break;
                        }

                        case ControlCommandKind.EmitToInstance:
                        {
                            if (TryGetWorkerInstance(cmd.Handle, out ParticleInstance? inst)
                                && inst is not null
                                && inst.State == ParticleInstanceState.Active
                                && inst.StoreSlot is not null)
                            {
                                ParticleOriginSnapshot origin = inst.OriginSnapshot;
                                ExecuteEmitCommandOnWorker(inst.StoreSlot, inst.IsLocalSpace, in origin, in cmd, "EmitToInstance");
                            }

                            break;
                        }

                        case ControlCommandKind.CustomWorkerAction:
                        {
                            // 单条隔离异常，避免业务回调抛错中断同批 CreateInstance 或击穿世界重置流程
                            try
                            {
                                cmd.CustomAction?.Invoke(this);
                            }
                            catch (Exception ex)
                            {
                                LogWorkerException("CustomWorkerAction", ex);
                            }

                            break;
                        }
                    }
                }
            }
            finally
            {
                _workerCommands.Clear(clearReferences: true);
            }
        }

        private void ApplyOriginUpdates(ReadOnlySpan<OriginUpdateItem> updates)
        {
            for (int i = 0; i < updates.Length; i++)
            {
                ref readonly OriginUpdateItem item = ref updates[i];
                if (!TryGetWorkerInstance(item.Handle, out ParticleInstance? inst) || inst is null)
                    continue;

                inst.UpdateOriginSnapshot(in item.Snapshot);
                if (!item.Snapshot.IsAlive && inst.State == ParticleInstanceState.Active)
                    ApplyDestroyPolicyOnWorker(inst, inst.HostLostPolicy);
            }
        }

        private void ExecuteEmitCommandOnWorker(ParticleStoreSlot slot, bool isLocalSpace, in ParticleOriginSnapshot origin, in ControlCommand cmd, string stage)
        {
            if (cmd.EmitCount <= 0)
                return;

            try
            {
                var ctx = new ParticleEmitContext(slot, isLocalSpace, in origin);
                if (cmd.Blueprint is not null)
                {
                    cmd.Blueprint.EmitBurstOnWorker(in ctx, cmd.EmitCount, ref _workerRng);
                }
                else if (cmd.Emitter is not null)
                {
                    cmd.Emitter.EmitBurst(in ctx, cmd.EmitCount, ref _workerRng);
                }
                else if (cmd.Template is not null && cmd.Initializer is not null)
                {
                    ParticleSpawnBatch batch = ctx.Allocate(cmd.Template, cmd.EmitCount);
                    cmd.Initializer(in batch, in ctx, ref _workerRng);
                }
            }
            catch (Exception ex)
            {
                LogWorkerException(stage, ex);
            }
        }

        private void TickAttachedEmitters(float deltaTime, bool isWorldPaused, bool isGameMenu)
        {
            if (!float.IsFinite(deltaTime) || !(deltaTime > 0f) || _activeInstances.Count == 0)
                return;

            bool stepWorld = !isWorldPaused && !isGameMenu;
            for (int i = 0; i < _activeInstances.Count; i++)
            {
                ParticleInstance inst = _activeInstances[i];
                if (inst.State != ParticleInstanceState.Active || inst.StoreSlot is null)
                    continue;

                // 若曾要求自动停止且同帧摘除了最后一个发射器（未再补挂），立即转入停止态等待残留粒子消亡后回收
                if (inst.ShouldAutoStopWhenEmpty)
                {
                    inst.StopIfAutoStopEmptyOnWorker();
                    continue;
                }

                if (!inst.HasAttachedEmitters)
                    continue;

                if (inst.UpdateDomain == ParticleUpdateDomain.World && !stepWorld)
                    continue;

                float effectiveDt = deltaTime * inst.TimeScale;
                if (!float.IsFinite(effectiveDt) || !(effectiveDt > 0f))
                    continue;

                inst.TickEmittersOnWorker(effectiveDt, ref _workerRng, _logWorkerExceptionCallback);
            }
        }

        private void ApplyDestroyPolicyOnWorker(ParticleInstance inst, ParticleHostLostPolicy policy)
        {
            // 进入停止或销毁态后子系统不再驱动发射器，立刻释放发射器及其闭包强引用
            inst.ClearEmittersOnWorker();

            switch (policy)
            {
                case ParticleHostLostPolicy.ClearImmediate:
                    inst.ClearLocalFieldsOnWorker();
                    inst.StoreSlot?.ClearEntities();
                    inst.State = ParticleInstanceState.Disposing;
                    break;

                case ParticleHostLostPolicy.DetachToGlobal:
                    if (inst.UpdateDomain == ParticleUpdateDomain.World)
                    {
                        // 异常隔离：确保迁移失败时不会中断批量命令/原点更新或导致下帧死循环重复拷贝
                        try
                        {
                            inst.StoreSlot?.PlaybackCommandBuffer();
                            if (inst.ParticleCount > 0)
                                ParticleStoreMigrator.MigrateToGlobalOnWorker(inst, _globalWorldSlot);
                        }
                        catch (Exception ex)
                        {
                            LogWorkerException("DetachToGlobal", ex);
                        }
                        finally
                        {
                            inst.StoreSlot?.ClearEntities();
                            inst.ClearLocalFieldsOnWorker();
                            inst.State = ParticleInstanceState.Disposing;
                        }
                    }
                    else
                    {
                        // UI 域子系统不跨入世界全局存储，安全回退为自然淡出
                        inst.State = ParticleInstanceState.Stopping;
                    }

                    break;

                default:
                    inst.State = ParticleInstanceState.Stopping;
                    break;
            }
        }

        private void AddGlobalFieldOnWorker(uint fieldId, in ForceFieldData data, float duration)
        {
            if (fieldId == 0)
                return;

            bool hasDuration = float.IsFinite(duration) && duration > 0f;
            _globalFields.Add(new ActiveForceFieldEntry
            {
                FieldId = fieldId,
                Data = data,
                RemainingDuration = hasDuration ? duration : 0f,
                HasDuration = hasDuration,
            });
            _globalFieldsDirty = true;
        }

        private void UpdateGlobalFieldOnWorker(uint fieldId, in ForceFieldData data)
        {
            for (int i = 0; i < _globalFields.Count; i++)
            {
                if (_globalFields[i].FieldId != fieldId)
                    continue;

                ActiveForceFieldEntry entry = _globalFields[i];
                entry.Data = data;
                _globalFields[i] = entry;
                _globalFieldsDirty = true;
                return;
            }
        }

        private void RemoveGlobalFieldOnWorker(uint fieldId)
        {
            for (int i = 0; i < _globalFields.Count; i++)
            {
                if (_globalFields[i].FieldId != fieldId)
                    continue;

                _globalFields.RemoveAt(i);
                _globalFieldsDirty = true;
                return;
            }
        }

        private void ClearGlobalFieldsOnWorker()
        {
            _globalFields.Clear();
            _globalFieldDataCache.Clear(clearReferences: false);
            _globalFieldsDirty = false;
        }

        private void AdvanceSimulationClock(float deltaTime, bool isWorldPaused, bool isGameMenu)
        {
            if (!float.IsFinite(deltaTime) || !(deltaTime > 0f))
                return;

            if (!isWorldPaused && !isGameMenu)
                _simulationTime += deltaTime;
        }

        private void TickForceFieldDurations(float deltaTime, bool isWorldPaused, bool isGameMenu)
        {
            if (!float.IsFinite(deltaTime) || !(deltaTime > 0f))
                return;

            bool stepWorld = !isWorldPaused && !isGameMenu;
            if (stepWorld)
            {
                for (int i = _globalFields.Count - 1; i >= 0; i--)
                {
                    ActiveForceFieldEntry entry = _globalFields[i];
                    if (!entry.HasDuration)
                        continue;

                    entry.RemainingDuration -= deltaTime;
                    if (!(entry.RemainingDuration > 0f))
                    {
                        _globalFields.RemoveAt(i);
                        _globalFieldsDirty = true;
                    }
                    else
                    {
                        _globalFields[i] = entry;
                    }
                }
            }

            for (int i = 0; i < _activeInstances.Count; i++)
            {
                ParticleInstance inst = _activeInstances[i];
                if (inst.UpdateDomain == ParticleUpdateDomain.World && !stepWorld)
                    continue;

                float effectiveDt = deltaTime * inst.TimeScale;
                if (float.IsFinite(effectiveDt) && effectiveDt > 0f)
                    inst.TickLocalFieldDurationsOnWorker(effectiveDt);
            }
        }

        private ReadOnlySpan<ForceFieldData> GetGlobalFieldsSpan()
        {
            if (_globalFields.Count == 0)
                return ReadOnlySpan<ForceFieldData>.Empty;

            if (_globalFieldsDirty)
            {
                _globalFieldDataCache.Clear(clearReferences: false);
                for (int i = 0; i < _globalFields.Count; i++)
                {
                    ForceFieldData d = _globalFields[i].Data;
                    _globalFieldDataCache.Add(in d);
                }

                _globalFieldsDirty = false;
            }

            return _globalFieldDataCache.AsReadOnlySpan();
        }

        private uint AllocateEmitterId()
        {
            uint id = Interlocked.Increment(ref _nextEmitterId);
            return id != 0 ? id : Interlocked.Increment(ref _nextEmitterId);
        }

        private uint AllocateFieldId()
        {
            uint id = Interlocked.Increment(ref _nextFieldId);
            return id != 0 ? id : Interlocked.Increment(ref _nextFieldId);
        }

        private void PlaybackAllCommandBuffers()
        {
            _globalWorldSlot.PlaybackCommandBuffer();
            for (int i = 0; i < _activeInstances.Count; i++)
                _activeInstances[i].StoreSlot?.PlaybackCommandBuffer();
        }

        private void ReapFinishedInstances()
        {
            for (int i = _activeInstances.Count - 1; i >= 0; i--)
            {
                ParticleInstance inst = _activeInstances[i];
                bool shouldRecycle = inst.State == ParticleInstanceState.Disposing
                    || (inst.State == ParticleInstanceState.Stopping && inst.ParticleCount == 0);
                if (!shouldRecycle)
                    continue;

                RecycleInstanceAtIndex(i);
            }
        }

        private void RecycleInstanceAtIndex(int activeIndex)
        {
            ParticleInstance inst = _activeInstances[activeIndex];
            ParticleSystemHandle handle = inst.Handle;

            // O(1) Swap-Remove 摘除活跃列表
            int lastIdx = _activeInstances.Count - 1;
            if (activeIndex != lastIdx)
                _activeInstances[activeIndex] = _activeInstances[lastIdx];
            _activeInstances.RemoveAt(lastIdx);
            _workerInstanceMap.Remove(handle.SlotId);

            ParticleStoreSlot? slot = inst.StoreSlot;
            if (slot is not null)
            {
                slot.ClearEntities();
                _storeSlotPool.Return(slot);
            }

            inst.ResetForPool();
            _instancePool.Return(inst);
            ReleaseRegistrySlot(handle);
        }

        private void ExecuteWorldResetOnWorker()
        {
            // 1. 先摘取并分流未消费队列：回收待建 World 域注册表槽位，保留 Ui 域指令不误丢
            if (DrainPendingCommands())
                ProcessWorkerCommands(skipWorldDomain: true);

            lock (_framePacketLock)
            {
                _submittedPacket.HasPendingTick = false;
                _submittedPacket.OriginUpdates.Clear(clearReferences: false);
            }

            // 2. 清空全局存储、全局世界力场并回收所有 World 域小型子系统；补齐保留 Ui 指令的命令回放与实例回收
            ClearGlobalFieldsOnWorker();
            _simulationTime = 0f;
            _globalWorldSlot.ClearEntities();
            for (int i = _activeInstances.Count - 1; i >= 0; i--)
            {
                if (_activeInstances[i].UpdateDomain == ParticleUpdateDomain.World)
                    RecycleInstanceAtIndex(i);
            }

            PlaybackAllCommandBuffers();
            ReapFinishedInstances();

            // 3. 通知管线仅清空 Write 槽并发布（或重打包残留 Ui 实例），绝不触碰主线程正在绘制的 Reading 槽
            IParticleRuntimePipeline? pipeline = _pipeline;
            if (pipeline is not null)
            {
                if (_activeInstances.Count > 0)
                    pipeline.PackRenderSnapshot(_globalWorldSlot, CollectionsMarshal.AsSpan(_activeInstances));
                else
                    pipeline.ResetWorldBuffers();
            }

            _lastStepMs = 0.0;
            PublishStats();
        }

        private void CleanupAllStoresOnWorker()
        {
            // 仅在工作线程永久退出前执行一次：直接线性清空，无需归还对象池或逐个释放注册表
            ClearGlobalFieldsOnWorker();
            _simulationTime = 0f;
            _globalWorldSlot.ClearEntities();
            for (int i = 0; i < _activeInstances.Count; i++)
            {
                _activeInstances[i].StoreSlot?.ClearEntities();
                _activeInstances[i].ResetForPool();
            }

            _activeInstances.Clear();
            _workerInstanceMap.Clear();
            _storeSlotPool.Clear();
            _instancePool.Clear();
        }

        private bool TryGetRegisteredDomain(ParticleSystemHandle handle, out ParticleUpdateDomain domain)
        {
            if (!handle.IsValid)
            {
                domain = ParticleUpdateDomain.World;
                return false;
            }

            lock (_registryLock)
            {
                if (handle.SlotId < _registry.Length)
                {
                    ref readonly RegistrySlot slot = ref _registry[handle.SlotId];
                    if (slot.Active && slot.Generation == handle.Generation)
                    {
                        domain = slot.UpdateDomain;
                        return true;
                    }
                }
            }

            domain = ParticleUpdateDomain.World;
            return false;
        }

        private bool EnqueueControlCommand(in ControlCommand cmd)
        {
            lock (_commandLock)
            {
                if (_stopRequested || _disposed)
                    return false;
                _pendingCommands.Add(in cmd);
            }

            WakeWorkerSafe();
            return true;
        }

        private void WakeWorkerSafe()
        {
            lock (_signalLock)
            {
                if (!_workerAlive || _disposed)
                    return;

                _hasWakeSignal = true;
                Monitor.PulseAll(_signalLock);
            }
        }

        private void ReleaseRegistrySlot(ParticleSystemHandle handle)
        {
            if (!handle.IsValid)
                return;

            lock (_registryLock)
            {
                if (handle.SlotId >= _registry.Length)
                    return;

                ref RegistrySlot slot = ref _registry[handle.SlotId];
                if (!slot.Active || slot.Generation != handle.Generation)
                    return;

                // O(1) Swap-Remove 摘除 _activeSlotIds，避免线性扫描加剧主线程与工作线程锁争用
                int idx = slot.ActiveListIndex;
                int count = _activeSlotIds.Count;
                if ((uint)idx < (uint)count)
                {
                    int lastIdx = count - 1;
                    if (idx != lastIdx)
                    {
                        ushort movedSlotId = _activeSlotIds[lastIdx];
                        _activeSlotIds[idx] = movedSlotId;
                        _registry[movedSlotId].ActiveListIndex = idx;
                    }

                    _activeSlotIds.RemoveAt(lastIdx);
                }

                slot.Active = false;
                slot.ActiveListIndex = RuntimeConstants.InvalidIndex;
                slot.OriginBinding = ParticleOriginBinding.Identity;
                slot.LastSnapshot = ParticleOriginSnapshot.Identity;
                _freeSlotIds.Push(handle.SlotId);
            }
        }

        private void PublishStats()
        {
            int instanceParticles = 0;
            for (int i = 0; i < _activeInstances.Count; i++)
                instanceParticles += _activeInstances[i].ParticleCount;

            var snapshot = new ParticleRuntimeStats(_globalWorldSlot.EntityCount, _activeInstances.Count, instanceParticles, _storeSlotPool.Count, _workerSteps, _lastStepMs);
            lock (_statsLock)
                _stats = snapshot;
        }

        private void LogWorkerException(string stage, Exception ex)
        {
            if (Interlocked.Increment(ref _loggedErrorCount) <= MaxLoggedWorkerErrors)
                _logger?.Error($"[Particles] 工作线程阶段 {stage} 发生未预期异常。", ex);
        }
    }
}
