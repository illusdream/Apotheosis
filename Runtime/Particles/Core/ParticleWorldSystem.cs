#nullable enable

using System;
using System.Threading;
using Apotheosis.Runtime.Rendering;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis.Runtime.Particles.Core
{
    /// <summary>粒子系统全局生命周期托管 <see cref="ModSystem"/>，桥接主线程更新、渲染前提交与工作线程停机栅栏。</summary>
    public sealed class ParticleWorldSystem : ModSystem
    {
        private const float FixedLogicDeltaSeconds = 1f / 60f;

        private static readonly object _lifecycleLock = new();
        private static volatile bool _unloading;
        private static volatile ParticleRuntime? _runtime;
        private static volatile ParticlePipeline? _pipeline;
        private static int _mainThreadId;
        private static int _activeTickCount;

        /// <summary>当前活跃粒子运行时（服务端或已卸载时为 <c>null</c>）。</summary>
        internal static ParticleRuntime? Runtime => _unloading ? null : _runtime;

        /// <summary>当前活跃粒子管线装配器（供注册自定义模拟或 Shader 渲染系统）。</summary>
        internal static ParticlePipeline? Pipeline => _unloading ? null : _pipeline;

        /// <summary>获取粒子运行时的最新统计快照。</summary>
        public static ParticleRuntimeStats GetStats()
        {
            ParticleRuntime? rt = Runtime;
            return rt is not null ? rt.GetStats() : default;
        }

#if DEBUG
        /// <summary>仅在开发模式下断言当前调用位于游戏主线程。</summary>
        internal static void AssertMainThread(string operationName)
        {
            int mainId = Volatile.Read(ref _mainThreadId);
            if (mainId != 0 && Environment.CurrentManagedThreadId != mainId)
                throw new InvalidOperationException($"{operationName} 必须在主线程调用（当前线程 {Environment.CurrentManagedThreadId}，主线程 {mainId}）。");
        }
#endif

        public override void Load()
        {
            lock (_lifecycleLock)
            {
                _unloading = false;
                if (Main.dedServ)
                    return;

                var pipeline = new ParticlePipeline();
                var rt = new ParticleRuntime();
                rt.SetLogger(Mod.Logger);
                rt.SetPipeline(pipeline);
                rt.Start();
                _pipeline = pipeline;
                _runtime = rt;
            }

            if (!Main.dedServ)
            {
                Main.RunOnMainThread(() => Volatile.Write(ref _mainThreadId, Environment.CurrentManagedThreadId));
                RenderManager.OnPreDrawSubmit += OnPreDrawSubmit;
            }
        }

        public override void PostUpdateEverything()
        {
            // 注意：ModSystem.PostUpdateEverything 仅在游戏世界内触发（主菜单不触发）；未来支持主菜单 UI 粒子时需补充 DoUpdate/UpdateUI 挂钩
            if (_unloading || Main.dedServ)
                return;

            ParticleRuntime? rt;
            lock (_lifecycleLock)
            {
                if (_unloading || _runtime is null)
                    return;
                rt = _runtime;
                Interlocked.Increment(ref _activeTickCount);
            }

            try
            {
                rt.TickFromMainThread(FixedLogicDeltaSeconds, Main.gamePaused, Main.gameMenu);
            }
            finally
            {
                Interlocked.Decrement(ref _activeTickCount);
            }
        }

        public override void OnWorldUnload()
        {
            if (_unloading || Main.dedServ)
                return;

            // OnWorldUnload 由 tML 在 ThreadPool 后台线程触发：锁内抓取引用、锁外等待重置栅栏，与 Unload 并发时由 Monitor 停机信号安全唤醒
            ParticleRuntime? rt;
            lock (_lifecycleLock)
            {
                if (_unloading)
                    return;
                rt = _runtime;
            }

            rt?.ResetWorldAndWait();
        }

        public override void Unload()
        {
            // 先置位卸载标记并摘除渲染前提交事件，再持锁与可能运行中的 OnPreDrawSubmit / PostUpdateEverything 严格串行化
            _unloading = true;
            if (!Main.dedServ)
                RenderManager.OnPreDrawSubmit -= OnPreDrawSubmit;

            ParticleRuntime? toShutdown;
            lock (_lifecycleLock)
            {
                toShutdown = _runtime;
                _runtime = null;
                _pipeline = null;
                Volatile.Write(ref _mainThreadId, 0);
            }

            // 等待在途主线程 Tick 完整返回后再停机，杜绝与 TickFromMainThread 并发访问主线程暂存缓冲
            var spin = new SpinWait();
            while (Volatile.Read(ref _activeTickCount) > 0)
                spin.SpinOnce();

            // 锁外等待工作线程退出并释放资源，避免持锁 Join 引发潜在死锁
            toShutdown?.ShutdownAndWait();
        }

        private static void OnPreDrawSubmit()
        {
            if (_unloading || RenderManager.IsUnloading || Main.dedServ)
                return;

            lock (_lifecycleLock)
            {
                if (_unloading || RenderManager.IsUnloading)
                    return;

                _runtime?.SubmitRenderOnMainThread();
            }
        }
    }
}
