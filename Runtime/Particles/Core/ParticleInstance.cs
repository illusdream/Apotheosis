#nullable enable

using System;
using System.Collections.Generic;
using Apotheosis.Runtime.Particles.Emitters;
using Apotheosis.Runtime.Rendering.Targets;

namespace Apotheosis.Runtime.Particles.Core
{
    /// <summary>粒子力场分类位掩码。</summary>
    [Flags]
    public enum ParticleFieldMask : uint
    {
        None = 0,
        Wind = 1u << 0,
        GravityWell = 1u << 1,
        Shockwave = 1u << 2,
        Vortex = 1u << 3,
        Turbulence = 1u << 4,
        Magic = 1u << 5,
        All = uint.MaxValue,
    }

    /// <summary>粒子模拟空间模式。</summary>
    public enum ParticleSimulationSpace : byte
    {
        /// <summary>世界空间：粒子出生后独立运动，不随系统原点移动。</summary>
        World = 0,

        /// <summary>局部空间：粒子在原点局部空间模拟，渲染时整体叠加原点世界矩阵。</summary>
        Local = 1,
    }

    /// <summary>粒子子系统所属更新域（用于区分世界暂停策略与未来 UI 粒子扩展）。</summary>
    public enum ParticleUpdateDomain : byte
    {
        World = 0,
        Ui = 1,
    }

    /// <summary>绑定原点目标失活时对残留粒子的处理策略。</summary>
    public enum ParticleHostLostPolicy : byte
    {
        /// <summary>停止继续发射，保留当前子系统等待残留粒子自然消亡后回收。</summary>
        StopAndFade = 0,

        /// <summary>立即将残留粒子烘焙为世界坐标并迁移至全局存储，随后立刻回收本子系统。</summary>
        DetachToGlobal = 1,

        /// <summary>立即清空全部残留粒子并回收本子系统。</summary>
        ClearImmediate = 2,
    }

    /// <summary>小型独立粒子子系统的运行状态。</summary>
    public enum ParticleInstanceState : byte
    {
        Active = 0,
        Stopping = 1,
        Disposing = 2,
    }

    /// <summary>创建小型独立粒子子系统的配置描述。</summary>
    public struct ParticleInstanceConfig
    {
        public ParticleOriginBinding Origin;
        public ParticleSimulationSpace SimulationSpace;
        public ParticleUpdateDomain UpdateDomain;
        public ParticleHostLostPolicy HostLostPolicy;
        public float TimeScale;
        public bool AffectByGlobalFields;
        public ParticleFieldMask GlobalFieldMask;
        public RenderTargetHandle? TargetOverride;

        public ParticleInstanceConfig()
        {
            Origin = ParticleOriginBinding.Identity;
            SimulationSpace = ParticleSimulationSpace.World;
            UpdateDomain = ParticleUpdateDomain.World;
            HostLostPolicy = ParticleHostLostPolicy.StopAndFade;
            TimeScale = 1f;
            AffectByGlobalFields = true;
            GlobalFieldMask = ParticleFieldMask.All;
            TargetOverride = null;
        }

        public static ParticleInstanceConfig Default => new();
    }

    /// <summary>单个小型独立粒子系统的宿主对象，包裹池化的 <see cref="ParticleStoreSlot"/> 与工作线程侧系统级上下文。</summary>
    public sealed class ParticleInstance
    {
        private struct MountedEmitterEntry
        {
            public uint EmitterId;
            public IParticleEmitter Playback;
            public float RateScale;
            public bool Enabled;
            public bool AutoStopWhenComplete;
        }

        // 仅由粒子工作线程独占访问的已挂载持续发射器槽位表（每实例通常 1~4 个，线性扫描零字典开销）
        private readonly List<MountedEmitterEntry> _emitters = new(2);
        private bool _stopIfNoEmittersRemain;

        /// <summary>对外公开的代际安全句柄。</summary>
        public ParticleSystemHandle Handle { get; private set; }

        /// <summary>底层 ECS 存储与查询缓存槽。</summary>
        public ParticleStoreSlot? StoreSlot { get; private set; }

        /// <summary>工作线程消费的最新原点世界变换快照。</summary>
        public ParticleOriginSnapshot OriginSnapshot { get; private set; } = ParticleOriginSnapshot.Identity;

        /// <summary>模拟空间模式（世界空间或局部空间）。</summary>
        public ParticleSimulationSpace SimulationSpace { get; internal set; }

        /// <summary>所属更新域（世界或 UI）。</summary>
        public ParticleUpdateDomain UpdateDomain { get; internal set; }

        /// <summary>绑定目标失活后的残留粒子处理策略。</summary>
        public ParticleHostLostPolicy HostLostPolicy { get; internal set; }

        /// <summary>当前子系统运行状态。</summary>
        public ParticleInstanceState State { get; internal set; }

        /// <summary>子系统独立时间缩放倍率。</summary>
        public float TimeScale { get; internal set; } = 1f;

        /// <summary>是否接受全局世界力场影响。</summary>
        public bool AffectByGlobalFields { get; internal set; } = true;

        /// <summary>全局力场位掩码过滤器。</summary>
        public ParticleFieldMask GlobalFieldMask { get; internal set; } = ParticleFieldMask.All;

        /// <summary>可选渲染目标覆盖（<c>null</c> 时按粒子自身目标提交）。</summary>
        public RenderTargetHandle? TargetOverride { get; internal set; }

        /// <summary>所有已挂载发射器完成后是否自动将本子系统切换为 <see cref="ParticleInstanceState.Stopping"/> 并在残留粒子消亡后回收。</summary>
        public bool AutoStopWhenEmittersComplete { get; internal set; }

        /// <summary>是否为局部空间模式。</summary>
        public bool IsLocalSpace => SimulationSpace == ParticleSimulationSpace.Local;

        /// <summary>当前子系统中的活跃粒子数量。</summary>
        public int ParticleCount => StoreSlot?.EntityCount ?? 0;

        /// <summary>工作线程侧是否挂载有至少一个发射器。</summary>
        internal bool HasAttachedEmitters => _emitters.Count > 0;

        /// <summary>曾要求发射器完成后自动停止且最后一个发射器已被摘除，需于本帧转入停止态。</summary>
        internal bool ShouldAutoStopWhenEmpty => _stopIfNoEmittersRemain && _emitters.Count == 0;

        internal void Initialize(ParticleSystemHandle handle, ParticleStoreSlot storeSlot, in ParticleInstanceConfig config, in ParticleOriginSnapshot initialSnapshot)
        {
            Handle = handle;
            StoreSlot = storeSlot;
            OriginSnapshot = initialSnapshot;
            SimulationSpace = config.SimulationSpace;
            UpdateDomain = config.UpdateDomain;
            HostLostPolicy = config.HostLostPolicy;
            TimeScale = float.IsFinite(config.TimeScale) && config.TimeScale >= 0f ? config.TimeScale : 1f;
            AffectByGlobalFields = config.AffectByGlobalFields;
            GlobalFieldMask = config.GlobalFieldMask;
            TargetOverride = config.TargetOverride;
            AutoStopWhenEmittersComplete = false;
            _stopIfNoEmittersRemain = false;
            _emitters.Clear();
            State = ParticleInstanceState.Active;
        }

        internal void UpdateOriginSnapshot(in ParticleOriginSnapshot snapshot) => OriginSnapshot = snapshot;

        internal bool AttachEmitterOnWorker(uint emitterId, IParticleEmitter playback, bool autoStopWhenComplete)
        {
            if (State != ParticleInstanceState.Active || emitterId == 0)
                return false;

            _stopIfNoEmittersRemain = false;
            _emitters.Add(new MountedEmitterEntry
            {
                EmitterId = emitterId,
                Playback = playback,
                RateScale = 1f,
                Enabled = true,
                AutoStopWhenComplete = autoStopWhenComplete,
            });

            RecomputeAutoStopFlag();
            return true;
        }

        internal void SetEmitterRateScaleOnWorker(uint emitterId, float rateScale)
        {
            if (!float.IsFinite(rateScale) || rateScale < 0f)
                return;

            for (int i = 0; i < _emitters.Count; i++)
            {
                if (_emitters[i].EmitterId != emitterId)
                    continue;

                MountedEmitterEntry entry = _emitters[i];
                entry.RateScale = rateScale;
                _emitters[i] = entry;
                return;
            }
        }

        internal void SetEmitterEnabledOnWorker(uint emitterId, bool enabled)
        {
            for (int i = 0; i < _emitters.Count; i++)
            {
                if (_emitters[i].EmitterId != emitterId)
                    continue;

                MountedEmitterEntry entry = _emitters[i];
                entry.Enabled = enabled;
                _emitters[i] = entry;
                return;
            }
        }

        internal void DetachEmitterOnWorker(uint emitterId)
        {
            for (int i = 0; i < _emitters.Count; i++)
            {
                if (_emitters[i].EmitterId != emitterId)
                    continue;

                bool removedWantedAutoStop = _emitters[i].AutoStopWhenComplete;
                _emitters.RemoveAt(i);
                RecomputeAutoStopFlag();
                if (removedWantedAutoStop && _emitters.Count == 0)
                    _stopIfNoEmittersRemain = true;
                return;
            }
        }

        internal void StopIfAutoStopEmptyOnWorker()
        {
            if (!_stopIfNoEmittersRemain || _emitters.Count > 0)
                return;

            _stopIfNoEmittersRemain = false;
            AutoStopWhenEmittersComplete = false;
            if (State == ParticleInstanceState.Active)
                State = ParticleInstanceState.Stopping;
        }

        internal void TriggerEmitterBurstOnWorker(uint emitterId, int count, ref ParticleRandom rng, Action<string, Exception>? onError)
        {
            if (State != ParticleInstanceState.Active || StoreSlot is null || count <= 0)
                return;

            for (int i = 0; i < _emitters.Count; i++)
            {
                if (_emitters[i].EmitterId != emitterId)
                    continue;

                try
                {
                    ParticleEmitContext ctx = ParticleEmitContext.ForInstance(this, _emitters[i].RateScale);
                    _emitters[i].Playback.EmitBurst(in ctx, count, ref rng);
                }
                catch (Exception ex)
                {
                    onError?.Invoke("TriggerEmitterBurst", ex);
                }

                return;
            }
        }

        internal void ClearEmittersOnWorker()
        {
            _emitters.Clear();
            AutoStopWhenEmittersComplete = false;
            _stopIfNoEmittersRemain = false;
        }

        internal void TickEmittersOnWorker(float deltaTime, ref ParticleRandom rng, Action<string, Exception>? onError)
        {
            if (State != ParticleInstanceState.Active || StoreSlot is null || _emitters.Count == 0)
                return;

            bool allCompleted = true;

            for (int i = 0; i < _emitters.Count; i++)
            {
                MountedEmitterEntry entry = _emitters[i];
                try
                {
                    // 先判 IsCompleted：已完成的发射器即使后续被 SetEmitterEnabled(false) 也不应阻止实例自动停止
                    if (entry.Playback.IsCompleted)
                        continue;

                    if (!entry.Enabled)
                    {
                        allCompleted = false;
                        continue;
                    }

                    ParticleEmitContext ctx = ParticleEmitContext.ForInstance(this, entry.RateScale);
                    entry.Playback.Tick(in ctx, deltaTime, ref rng);
                    if (!entry.Playback.IsCompleted)
                        allCompleted = false;
                }
                catch (Exception ex)
                {
                    // 单发射器异常隔离：记录后继续驱动其余发射器，避免单点异常跳过本帧模拟与渲染打包
                    allCompleted = false;
                    onError?.Invoke("TickAttachedEmitter", ex);
                }
            }

            if (allCompleted && AutoStopWhenEmittersComplete && State == ParticleInstanceState.Active)
            {
                ClearEmittersOnWorker();
                State = ParticleInstanceState.Stopping;
            }
        }

        private void RecomputeAutoStopFlag()
        {
            bool anyAutoStop = false;
            for (int i = 0; i < _emitters.Count; i++)
            {
                if (_emitters[i].AutoStopWhenComplete)
                {
                    anyAutoStop = true;
                    break;
                }
            }

            AutoStopWhenEmittersComplete = anyAutoStop;
        }

        internal void ResetForPool()
        {
            Handle = ParticleSystemHandle.Invalid;
            StoreSlot = null;
            OriginSnapshot = ParticleOriginSnapshot.Identity;
            SimulationSpace = ParticleSimulationSpace.World;
            UpdateDomain = ParticleUpdateDomain.World;
            HostLostPolicy = ParticleHostLostPolicy.StopAndFade;
            TimeScale = 1f;
            AffectByGlobalFields = true;
            GlobalFieldMask = ParticleFieldMask.All;
            TargetOverride = null;
            ClearEmittersOnWorker();
            State = ParticleInstanceState.Disposing;
        }
    }
}
