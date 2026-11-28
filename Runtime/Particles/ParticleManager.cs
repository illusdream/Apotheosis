#nullable enable

using System;
using Apotheosis.Runtime.Foundation.Transform;
using Apotheosis.Runtime.Particles.Core;
using Apotheosis.Runtime.Particles.Emitters;
using Apotheosis.Runtime.Particles.Fields;
using Apotheosis.Runtime.Particles.Fragments;
using Apotheosis.Runtime.Particles.Systems;
using Apotheosis.Runtime.Rendering.Targets;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles
{
    /// <summary>ECS 多线程粒子系统的公开统一静态门面（封装子系统生命周期、不可变发射器蓝图挂载与单发射器实例控制、爆发发射及管线注册）。</summary>
    public static class ParticleManager
    {
        /// <summary>当前粒子运行时是否处于可用状态（专用服务端或 Mod 卸载期间为 <c>false</c>）。</summary>
        public static bool IsAvailable => ParticleWorldSystem.Runtime is not null;

        /// <summary>获取粒子工作线程最近一帧的只读统计快照。</summary>
        public static ParticleRuntimeStats GetStats() => ParticleWorldSystem.GetStats();

        // ==================== 小型独立粒子子系统生命周期 ====================

        /// <summary>按完整配置创建小型独立粒子子系统并同步返回代际安全句柄。</summary>
        public static ParticleSystemHandle CreateInstance(in ParticleInstanceConfig config)
        {
            ParticleRuntime? rt = ParticleWorldSystem.Runtime;
            return rt is not null ? rt.CreateInstance(in config) : ParticleSystemHandle.Invalid;
        }

        /// <summary>按常用参数快捷创建小型独立粒子子系统。</summary>
        public static ParticleSystemHandle CreateInstance(ParticleOriginBinding origin, ParticleSimulationSpace space = ParticleSimulationSpace.World,
            ParticleHostLostPolicy hostLostPolicy = ParticleHostLostPolicy.StopAndFade, ParticleUpdateDomain domain = ParticleUpdateDomain.World,
            RenderTargetHandle? targetOverride = null)
        {
            var config = new ParticleInstanceConfig
            {
                Origin = origin,
                SimulationSpace = space,
                HostLostPolicy = hostLostPolicy,
                UpdateDomain = domain,
                TargetOverride = targetOverride,
            };
            return CreateInstance(in config);
        }

        /// <summary>一步创建小型独立粒子子系统并立即挂载不可变 <see cref="ParticleEmitter"/> 蓝图（可无锁复用于多个子系统，无需克隆）。</summary>
        public static ParticleSystemHandle PlayInstance(in ParticleInstanceConfig config, ParticleEmitter emitter, bool autoStopWhenComplete = true)
            => PlayInstance(in config, emitter, out _, autoStopWhenComplete);

        /// <summary>一步创建小型独立粒子子系统并挂载不可变 <see cref="ParticleEmitter"/> 蓝图，同时输出该发射器实例句柄供后续独立调速或启停。</summary>
        public static ParticleSystemHandle PlayInstance(in ParticleInstanceConfig config, ParticleEmitter emitter, out ParticleEmitterHandle emitterHandle, bool autoStopWhenComplete = true)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            if (!emitter.IsBaked)
                throw new InvalidOperationException($"粒子发射器 '{emitter.Name}' 尚未调用 Bake()，无法挂载。");

            emitterHandle = ParticleEmitterHandle.Invalid;
            ParticleRuntime? rt = ParticleWorldSystem.Runtime;
            if (rt is null)
                return ParticleSystemHandle.Invalid;

            ParticleSystemHandle handle = rt.CreateInstance(in config);
            if (!handle.IsValid)
                return ParticleSystemHandle.Invalid;

            emitterHandle = rt.AttachEmitter(handle, emitter, autoStopWhenComplete);
            return handle;
        }

        /// <summary>一步创建小型独立粒子子系统并挂载底层自定义 <see cref="IParticleEmitter"/> 实例。</summary>
        public static ParticleSystemHandle PlayInstance(in ParticleInstanceConfig config, IParticleEmitter emitter, out ParticleEmitterHandle emitterHandle, bool autoStopWhenComplete = true)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            emitterHandle = ParticleEmitterHandle.Invalid;
            ParticleRuntime? rt = ParticleWorldSystem.Runtime;
            if (rt is null)
                return ParticleSystemHandle.Invalid;

            ParticleSystemHandle handle = rt.CreateInstance(in config);
            if (!handle.IsValid)
                return ParticleSystemHandle.Invalid;

            emitterHandle = rt.AttachEmitter(handle, emitter, autoStopWhenComplete);
            return handle;
        }

        /// <summary>检查指定子系统句柄当前是否仍处于有效活跃状态。</summary>
        public static bool IsInstanceAlive(ParticleSystemHandle handle)
        {
            ParticleRuntime? rt = ParticleWorldSystem.Runtime;
            return rt is not null && rt.IsInstanceAlive(handle);
        }

        /// <summary>动态调整整个小型独立粒子子系统的时间流速倍率（不影响全局世界存储）。</summary>
        public static void SetInstanceTimeScale(ParticleSystemHandle handle, float timeScale)
            => ParticleWorldSystem.Runtime?.SetInstanceTimeScale(handle, timeScale);

        /// <summary>请求停止指定子系统继续发射（残留粒子将按生命周期自然消亡后自动回收）。</summary>
        public static void StopInstance(ParticleSystemHandle handle)
            => ParticleWorldSystem.Runtime?.StopInstance(handle);

        /// <summary>请求按指定策略销毁指定小型独立粒子子系统。</summary>
        public static void DestroyInstance(ParticleSystemHandle handle, ParticleHostLostPolicy policy = ParticleHostLostPolicy.StopAndFade)
            => ParticleWorldSystem.Runtime?.DestroyInstance(handle, policy);

        // ==================== 单个已挂载发射器控制 ====================

        /// <summary>向指定子系统挂载不可变 <see cref="ParticleEmitter"/> 蓝图并同步返回该挂载发射器的独立句柄。</summary>
        public static ParticleEmitterHandle AttachEmitter(ParticleSystemHandle handle, ParticleEmitter emitter, bool autoStopWhenComplete = false)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            if (!emitter.IsBaked)
                throw new InvalidOperationException($"粒子发射器 '{emitter.Name}' 尚未调用 Bake()，无法挂载。");

            ParticleRuntime? rt = ParticleWorldSystem.Runtime;
            return rt is not null ? rt.AttachEmitter(handle, emitter, autoStopWhenComplete) : ParticleEmitterHandle.Invalid;
        }

        /// <summary>向指定子系统挂载底层自定义 <see cref="IParticleEmitter"/> 实例并同步返回独立句柄。</summary>
        public static ParticleEmitterHandle AttachEmitter(ParticleSystemHandle handle, IParticleEmitter emitter, bool autoStopWhenComplete = false)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            ParticleRuntime? rt = ParticleWorldSystem.Runtime;
            return rt is not null ? rt.AttachEmitter(handle, emitter, autoStopWhenComplete) : ParticleEmitterHandle.Invalid;
        }

        /// <summary>动态调整单个已挂载发射器的发射速率倍率（不影响同子系统内其他发射器）。</summary>
        public static void SetEmitterRateScale(ParticleEmitterHandle emitterHandle, float rateScale)
            => ParticleWorldSystem.Runtime?.SetEmitterRateScale(emitterHandle, rateScale);

        /// <summary>独立启用或暂停单个已挂载发射器（不影响同子系统内其他发射器）。</summary>
        public static void SetEmitterEnabled(ParticleEmitterHandle emitterHandle, bool enabled)
            => ParticleWorldSystem.Runtime?.SetEmitterEnabled(emitterHandle, enabled);

        /// <summary>让单个已挂载发射器在所属子系统中立即额外爆发指定数量的粒子。</summary>
        public static void TriggerEmitterBurst(ParticleEmitterHandle emitterHandle, int count)
            => ParticleWorldSystem.Runtime?.TriggerEmitterBurst(emitterHandle, count);

        /// <summary>从所属子系统中独立摘除单个已挂载发射器。</summary>
        public static void DetachEmitter(ParticleEmitterHandle emitterHandle)
            => ParticleWorldSystem.Runtime?.DetachEmitter(emitterHandle);

        /// <summary>清空指定子系统上已挂载的全部发射器。</summary>
        public static void ClearEmitters(ParticleSystemHandle handle)
            => ParticleWorldSystem.Runtime?.ClearEmitters(handle);

        // ==================== 全局世界存储单次爆发发射 ====================

        /// <summary>在全局世界存储中于指定 3D 坐标处通过不可变 <see cref="ParticleEmitter"/> 蓝图爆发指定数量粒子。</summary>
        public static void EmitGlobal(ParticleEmitter emitter, int count, Vector3 worldPosition)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            if (!emitter.IsBaked)
                throw new InvalidOperationException($"粒子发射器 '{emitter.Name}' 尚未调用 Bake()，无法发射粒子。");
            if (count <= 0)
                return;

            ParticleRuntime? rt = ParticleWorldSystem.Runtime;
            if (rt is null)
                return;

            ParticleOriginSnapshot origin = ParticleOriginSnapshot.FromPosition(worldPosition);
            rt.EmitGlobal(emitter, count, in origin);
        }

        /// <summary>在全局世界存储中于指定 2D 坐标处通过不可变 <see cref="ParticleEmitter"/> 蓝图爆发指定数量粒子。</summary>
        public static void EmitGlobal(ParticleEmitter emitter, int count, Vector2 worldPosition, float depthZ = 0f)
            => EmitGlobal(emitter, count, new Vector3(worldPosition, depthZ));

        /// <summary>在全局世界存储中于指定 3D 变换原点处通过不可变 <see cref="ParticleEmitter"/> 蓝图爆发指定数量粒子。</summary>
        public static void EmitGlobal(ParticleEmitter emitter, int count, in Transform3D worldTransform)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            if (!emitter.IsBaked)
                throw new InvalidOperationException($"粒子发射器 '{emitter.Name}' 尚未调用 Bake()，无法发射粒子。");
            if (count <= 0)
                return;

            ParticleRuntime? rt = ParticleWorldSystem.Runtime;
            if (rt is null)
                return;

            ParticleOriginSnapshot origin = ParticleOriginSnapshot.FromTransform(in worldTransform);
            rt.EmitGlobal(emitter, count, in origin);
        }

        /// <summary>在全局世界存储中于指定 3D 坐标处通过底层 <see cref="IParticleEmitter"/> 爆发指定数量粒子。</summary>
        public static void EmitGlobal(IParticleEmitter emitter, int count, Vector3 worldPosition)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            if (count <= 0)
                return;

            ParticleRuntime? rt = ParticleWorldSystem.Runtime;
            if (rt is null)
                return;

            ParticleOriginSnapshot origin = ParticleOriginSnapshot.FromPosition(worldPosition);
            rt.EmitGlobal(emitter, count, in origin);
        }

        /// <summary>在全局世界存储中于指定 2D 坐标处通过底层 <see cref="IParticleEmitter"/> 爆发指定数量粒子。</summary>
        public static void EmitGlobal(IParticleEmitter emitter, int count, Vector2 worldPosition, float depthZ = 0f)
            => EmitGlobal(emitter, count, new Vector3(worldPosition, depthZ));

        /// <summary>在全局世界存储中于指定 3D 变换原点处通过底层 <see cref="IParticleEmitter"/> 爆发指定数量粒子。</summary>
        public static void EmitGlobal(IParticleEmitter emitter, int count, in Transform3D worldTransform)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            if (count <= 0)
                return;

            ParticleRuntime? rt = ParticleWorldSystem.Runtime;
            if (rt is null)
                return;

            ParticleOriginSnapshot origin = ParticleOriginSnapshot.FromTransform(in worldTransform);
            rt.EmitGlobal(emitter, count, in origin);
        }

        /// <summary>在全局世界存储中按模板直接批量分配粒子并由自定义委托在工作线程初始化。</summary>
        public static void EmitGlobal(ParticleTemplate template, int count, ParticleBatchInitializer initializer)
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(initializer);
            if (!template.IsBaked)
                throw new InvalidOperationException($"粒子模板 '{template.Name}' 尚未调用 Bake()，无法发射粒子。");
            if (count <= 0)
                return;

            ParticleWorldSystem.Runtime?.EmitGlobal(template, count, in ParticleOriginSnapshot.Identity, initializer);
        }

        /// <summary>在全局世界存储中于指定 3D 坐标处按模板批量分配并自定义初始化粒子。</summary>
        public static void EmitGlobal(ParticleTemplate template, int count, Vector3 worldPosition, ParticleBatchInitializer initializer)
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(initializer);
            if (!template.IsBaked)
                throw new InvalidOperationException($"粒子模板 '{template.Name}' 尚未调用 Bake()，无法发射粒子。");
            if (count <= 0)
                return;

            ParticleRuntime? rt = ParticleWorldSystem.Runtime;
            if (rt is null)
                return;

            ParticleOriginSnapshot origin = ParticleOriginSnapshot.FromPosition(worldPosition);
            rt.EmitGlobal(template, count, in origin, initializer);
        }

        /// <summary>在全局世界存储中于指定 2D 坐标处按模板批量分配并自定义初始化粒子。</summary>
        public static void EmitGlobal(ParticleTemplate template, int count, Vector2 worldPosition, ParticleBatchInitializer initializer, float depthZ = 0f)
            => EmitGlobal(template, count, new Vector3(worldPosition, depthZ), initializer);

        /// <summary>在全局世界存储中于指定 3D 变换原点处按模板批量分配并自定义初始化粒子。</summary>
        public static void EmitGlobal(ParticleTemplate template, int count, in Transform3D worldTransform, ParticleBatchInitializer initializer)
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(initializer);
            if (!template.IsBaked)
                throw new InvalidOperationException($"粒子模板 '{template.Name}' 尚未调用 Bake()，无法发射粒子。");
            if (count <= 0)
                return;

            ParticleRuntime? rt = ParticleWorldSystem.Runtime;
            if (rt is null)
                return;

            ParticleOriginSnapshot origin = ParticleOriginSnapshot.FromTransform(in worldTransform);
            rt.EmitGlobal(template, count, in origin, initializer);
        }

        // ==================== 小型独立粒子子系统单次爆发发射 ====================

        /// <summary>向指定活跃小型粒子子系统通过不可变 <see cref="ParticleEmitter"/> 蓝图立即爆发指定数量粒子。</summary>
        public static void EmitTo(ParticleSystemHandle handle, ParticleEmitter emitter, int count)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            if (!emitter.IsBaked)
                throw new InvalidOperationException($"粒子发射器 '{emitter.Name}' 尚未调用 Bake()，无法发射粒子。");
            if (count <= 0)
                return;

            ParticleWorldSystem.Runtime?.EmitToInstance(handle, emitter, count);
        }

        /// <summary>向指定活跃小型粒子子系统通过底层 <see cref="IParticleEmitter"/> 立即爆发指定数量粒子。</summary>
        public static void EmitTo(ParticleSystemHandle handle, IParticleEmitter emitter, int count)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            if (count <= 0)
                return;

            ParticleWorldSystem.Runtime?.EmitToInstance(handle, emitter, count);
        }

        /// <summary>向指定活跃小型粒子子系统按模板直接批量分配并自定义初始化粒子。</summary>
        public static void EmitTo(ParticleSystemHandle handle, ParticleTemplate template, int count, ParticleBatchInitializer initializer)
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(initializer);
            if (!template.IsBaked)
                throw new InvalidOperationException($"粒子模板 '{template.Name}' 尚未调用 Bake()，无法发射粒子。");
            if (count <= 0)
                return;

            ParticleWorldSystem.Runtime?.EmitToInstance(handle, template, count, initializer);
        }

        // ==================== 全局与局部粒子力场 ====================

        /// <summary>向全局世界添加一个粒子力场并返回句柄（<paramref name="duration"/> &gt; 0 时到期自动销毁）。</summary>
        public static ForceFieldHandle AddGlobalForceField(in ForceFieldData data, float duration = 0f)
        {
            ParticleRuntime? rt = ParticleWorldSystem.Runtime;
            return rt is not null ? rt.AddGlobalForceField(in data, duration) : ForceFieldHandle.Invalid;
        }

        /// <summary>向指定小型独立粒子子系统挂载一个专属局部力场并返回句柄（随子系统回收自动清除）。</summary>
        public static ForceFieldHandle AddInstanceForceField(ParticleSystemHandle instanceHandle, in ForceFieldData data, float duration = 0f)
        {
            ParticleRuntime? rt = ParticleWorldSystem.Runtime;
            return rt is not null ? rt.AddInstanceForceField(instanceHandle, in data, duration) : ForceFieldHandle.Invalid;
        }

        /// <summary>实时更新指定全局或局部力场的参数（如跟随实体移动的位置或动态变化的强度）。</summary>
        public static void UpdateForceField(ForceFieldHandle handle, in ForceFieldData data)
            => ParticleWorldSystem.Runtime?.UpdateForceField(handle, in data);

        /// <summary>移除指定的全局或局部力场。</summary>
        public static void RemoveForceField(ForceFieldHandle handle)
            => ParticleWorldSystem.Runtime?.RemoveForceField(handle);

        /// <summary>清空全部全局世界力场。</summary>
        public static void ClearGlobalForceFields()
            => ParticleWorldSystem.Runtime?.ClearGlobalForceFields();

        // ==================== 管线扩展注册 ====================

        /// <summary>向粒子模拟管线追加注册自定义模拟系统。</summary>
        public static void RegisterSimSystem(IParticleSimSystem system)
        {
            ArgumentNullException.ThrowIfNull(system);
            ParticleWorldSystem.Pipeline?.RegisterSimSystem(system);
        }

        /// <summary>向粒子渲染管线追加注册自定义 Shader 粒度渲染系统。</summary>
        public static void RegisterRenderSystem(IParticleRenderSystem system)
        {
            ArgumentNullException.ThrowIfNull(system);
            ParticleWorldSystem.Pipeline?.RegisterRenderSystem(system);
        }
    }
}
