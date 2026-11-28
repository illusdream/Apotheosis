#nullable enable

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Apotheosis.Runtime.Particles.Fields;
using Apotheosis.Runtime.Particles.Systems;

namespace Apotheosis.Runtime.Particles.Core
{
    /// <summary>默认粒子管线装配器，串联所有模拟系统与按 Shader 切分的硬件实例化渲染系统。</summary>
    internal sealed class ParticlePipeline : IParticleRuntimePipeline
    {
        private readonly object _registerLock = new();
        private IParticleSimSystem[] _simSystems;
        private IParticleRenderSystem[] _renderSystems;

        public ParticlePipeline()
        {
            // 按固定物理顺序装配默认模拟管线（Life 居首，Gravity/ForceField/Drag 施加受力，Integrate 积分，TileCollision 地形响应，随后视觉曲线与动画）
            _simSystems = new IParticleSimSystem[]
            {
                new ParticleLifeSystem(),
                new ParticleGravitySystem(),
                new ParticleForceFieldSystem(),
                new ParticleDragSystem(),
                new ParticleMotionIntegrateSystem(),
                new ParticleTileCollisionSystem(),
                new ParticleColorOverLifeSystem(),
                new ParticleScaleOverLifeSystem(),
                new ParticleVelocityAlignSystem(),
                new ParticleSheetAnimSystem(),
            };

            _renderSystems = new IParticleRenderSystem[]
            {
                new BasicQuadRenderSystem(),
            };
        }

        /// <summary>追加注册自定义模拟系统（写时复制替换不可变数组，保证工作线程零锁安全读取）。</summary>
        public void RegisterSimSystem(IParticleSimSystem system)
        {
            ArgumentNullException.ThrowIfNull(system);
            lock (_registerLock)
            {
                IParticleSimSystem[] prev = _simSystems;
                var next = new IParticleSimSystem[prev.Length + 1];
                Array.Copy(prev, next, prev.Length);
                next[prev.Length] = system;
                Volatile.Write(ref _simSystems, next);
            }
        }

        /// <summary>追加注册自定义 Shader 粒度渲染系统（写时复制替换不可变数组，保证主线程与工作线程零锁安全读取）。</summary>
        public void RegisterRenderSystem(IParticleRenderSystem system)
        {
            ArgumentNullException.ThrowIfNull(system);
            lock (_registerLock)
            {
                IParticleRenderSystem[] prev = _renderSystems;
                var next = new IParticleRenderSystem[prev.Length + 1];
                Array.Copy(prev, next, prev.Length);
                next[prev.Length] = system;
                Volatile.Write(ref _renderSystems, next);
            }
        }

        public void StepSimulation(ParticleStoreSlot globalSlot, ReadOnlySpan<ParticleInstance> activeInstances, float deltaTime, float simulationTime,
            ReadOnlySpan<ForceFieldData> globalFields, bool isWorldPaused, bool isGameMenu)
        {
            IParticleSimSystem[] sims = Volatile.Read(ref _simSystems);
            if (sims.Length == 0 || !float.IsFinite(deltaTime) || !(deltaTime > 0f))
                return;

            bool stepWorld = !isWorldPaused && !isGameMenu;
            if (stepWorld && globalSlot.EntityCount > 0)
            {
                ParticleSimContext globalCtx = ParticleSimContext.ForGlobalWorld(deltaTime, simulationTime, globalFields);
                for (int s = 0; s < sims.Length; s++)
                    sims[s].Execute(globalSlot, in globalCtx);
            }

            for (int i = 0; i < activeInstances.Length; i++)
            {
                ParticleInstance inst = activeInstances[i];
                ParticleStoreSlot? slot = inst.StoreSlot;
                if (slot is null || slot.EntityCount == 0)
                    continue;

                if (inst.UpdateDomain == ParticleUpdateDomain.World && !stepWorld)
                    continue;

                float effectiveDt = deltaTime * inst.TimeScale;
                if (!float.IsFinite(effectiveDt) || !(effectiveDt > 0f))
                    continue;

                ParticleSimContext instCtx = ParticleSimContext.ForInstance(inst, effectiveDt, simulationTime, globalFields, inst.GetLocalFieldsSpan());
                for (int s = 0; s < sims.Length; s++)
                    sims[s].Execute(slot, in instCtx);
            }
        }

        public void PackRenderSnapshot(ParticleStoreSlot globalSlot, ReadOnlySpan<ParticleInstance> activeInstances)
        {
            IParticleRenderSystem[] renders = Volatile.Read(ref _renderSystems);
            if (renders.Length == 0)
                return;

            Exception? firstException = null;
            ParticleRenderContext globalCtx = ParticleRenderContext.ForGlobalWorld();

            for (int r = 0; r < renders.Length; r++)
            {
                IParticleRenderSystem renderSys = renders[r];
                bool packed = false;
                try
                {
                    renderSys.BeginPack();

                    if (globalSlot.EntityCount > 0)
                        renderSys.PackStore(globalSlot, in globalCtx);

                    for (int i = 0; i < activeInstances.Length; i++)
                    {
                        ParticleInstance inst = activeInstances[i];
                        ParticleStoreSlot? slot = inst.StoreSlot;
                        if (slot is null || slot.EntityCount == 0)
                            continue;

                        ParticleRenderContext instCtx = ParticleRenderContext.ForInstance(inst);
                        renderSys.PackStore(slot, in instCtx);
                    }

                    renderSys.EndPack();
                    packed = true;
                }
                catch (Exception ex)
                {
                    // 单系统故障隔离：继续完成其余 Shader 渲染系统打包，循环结束后统一上抛首个异常供限频记录
                    firstException ??= ex;
                }
                finally
                {
                    if (!packed)
                        renderSys.AbortPack();
                }
            }

            if (firstException is not null)
                ExceptionDispatchInfo.Capture(firstException).Throw();
        }

        public void SubmitOnMainThread()
        {
            IParticleRenderSystem[] renders = Volatile.Read(ref _renderSystems);
            for (int r = 0; r < renders.Length; r++)
                renders[r].SubmitOnMainThread();
        }

        public void ResetWorldBuffers()
        {
            IParticleRenderSystem[] renders = Volatile.Read(ref _renderSystems);
            Exception? firstException = null;

            for (int r = 0; r < renders.Length; r++)
            {
                try
                {
                    renders[r].PublishClearedSnapshot();
                }
                catch (Exception ex)
                {
                    firstException ??= ex;
                }
            }

            if (firstException is not null)
                ExceptionDispatchInfo.Capture(firstException).Throw();
        }

        public void DisposeResources()
        {
            IParticleRenderSystem[] renders = Volatile.Read(ref _renderSystems);
            for (int r = 0; r < renders.Length; r++)
            {
                try
                {
                    renders[r].DisposeResources();
                }
                catch
                {
                    // 卸载路径确保所有渲染系统的 MeshHandle 均有机会完成注销，不被单个异常中断
                }
            }
        }
    }
}
