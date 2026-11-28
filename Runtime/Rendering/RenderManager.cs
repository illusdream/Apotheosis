#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Apotheosis.Runtime.Rendering.Commands;
using Apotheosis.Runtime.Rendering.Geometry;
using Apotheosis.Runtime.Rendering.Materials;
using Apotheosis.Runtime.Rendering.PostProcess;
using Apotheosis.Runtime.Rendering.Shaders;
using Apotheosis.Runtime.Rendering.Targets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Graphics.Effects;
using Terraria.ModLoader;

namespace Apotheosis.Runtime.Rendering
{
    /// <summary>全局渲染管线入口系统，统一管理帧上下文、各渲染子模块及挂钩生命周期。</summary>
    public sealed class RenderManager : ModSystem
    {
        private static readonly object _lifecycleLock = new();
        private static volatile bool _unloading;
        private static int _activePreDrawSubmitCount;

        private static RenderFrameContext _frameContext;
        private static ulong _frameCounter;
        private static ulong _contextRevision;
        private static float _lastDeltaTime;
        private static bool _contextCaptured;
#if DEBUG
        private static ShaderHotReloader? _shaderHotReloader;
#endif

        /// <summary>着色器管理模块。</summary>
        public static ShaderModule Shaders { get; } = new();

        /// <summary>渲染目标管理模块。</summary>
        public static RenderTargetModule Targets { get; } = new();

        /// <summary>材质管理模块。</summary>
        public static MaterialModule Materials { get; } = new();

        /// <summary>网格管理模块。</summary>
        public static MeshModule Meshes { get; } = new();

        /// <summary>渲染命令与合批调度模块。</summary>
        public static RenderCommandModule Commands { get; } = new();

        /// <summary>后处理管线模块（选择性金字塔 Bloom、全屏 FP16 扭曲、Color Adjustments 与 Split Toning）。</summary>
        public static PostProcessModule PostProcess { get; } = new();

        /// <summary>当前帧渲染上下文快照。</summary>
        public static RenderFrameContext FrameContext => FrameContextRef;

        /// <summary>当前帧渲染上下文只读引用。</summary>
        internal static ref readonly RenderFrameContext FrameContextRef
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                // 首读单点惰性采集，确保拿到全部 Mod 修改完毕后的最终视图矩阵
                if (!_contextCaptured && _frameCounter != 0 && !Main.dedServ)
                {
                    Shaders.AssertMainThread("RenderManager.FrameContext");
                    CaptureCurrentContext();
                }

                return ref _frameContext;
            }
        }

        /// <summary>每渲染帧开始前在主线程触发的通用提交事件。</summary>
        public static event Action? OnPreDrawSubmit;

        /// <summary>当前渲染帧号。</summary>
        public static ulong FrameId => Volatile.Read(ref _frameCounter);

        /// <summary>当前视图上下文修订号。</summary>
        public static ulong ContextRevision => FrameContextRef.ContextRevision;

        internal static bool IsUnloading => _unloading;

        public override void Load()
        {
            lock (_lifecycleLock)
            {
                _unloading = false;
                _frameCounter = 0;
                _contextRevision = 0;
                _lastDeltaTime = 0f;
                _contextCaptured = false;
                _frameContext = default;
#if DEBUG
                Shaders.SetLogger(Mod.Logger);
                Targets.SetLogger(Mod.Logger);
#endif
                Targets.ResetForLoad();
                PostProcess.ResetForLoad();
            }

            if (!Main.dedServ)
            {
                Main.RunOnMainThread(() =>
                {
                    Shaders.EnsureMainThreadBound(Environment.CurrentManagedThreadId);
                    PostProcess.InitializeOnMainThread();
                });
                Main.OnPreDraw += OnPreDraw;
                On_Main.DoDraw += Hook_DoDraw;
                On_Main.DoDraw_UpdateCameraPosition += Hook_DoDraw_UpdateCameraPosition;
                On_Main.DrawProjectiles += Hook_DrawProjectiles;
                On_Main.DrawDust += Hook_DrawDust;
                On_FilterManager.EndCapture_RenderTarget2D_RenderTarget2D_RenderTarget2D += Hook_FilterManager_EndCapture;
                On_Main.DrawInterface += Hook_DrawInterface;
#if DEBUG
                _shaderHotReloader = ShaderHotReloader.TryStart(Shaders, Mod.Logger, Mod.Name);
#endif
            }
        }

        public override bool RequiresScreenTarget()
        {
            if (Main.dedServ || _unloading)
                return false;

            return Main.gameMenu ? PostProcess.ShouldCaptureInMenu() : PostProcess.ShouldCaptureScreenTarget();
        }

        public override void PostDrawTiles()
        {
            FlushTimingSafe(RenderTiming.AfterTiles);
        }

        public override void Unload()
        {
            // 先置位卸载标记并摘除事件，再通过 _lifecycleLock 与可能正在运行的 OnPreDraw / Flush / EndFrame 严格串行化
            _unloading = true;

            if (!Main.dedServ)
            {
                Main.OnPreDraw -= OnPreDraw;
                On_Main.DoDraw -= Hook_DoDraw;
                On_Main.DoDraw_UpdateCameraPosition -= Hook_DoDraw_UpdateCameraPosition;
                On_Main.DrawProjectiles -= Hook_DrawProjectiles;
                On_Main.DrawDust -= Hook_DrawDust;
                On_FilterManager.EndCapture_RenderTarget2D_RenderTarget2D_RenderTarget2D -= Hook_FilterManager_EndCapture;
                On_Main.DrawInterface -= Hook_DrawInterface;
            }

            lock (_lifecycleLock)
            {
                OnPreDrawSubmit = null;
            }

            // 等待锁外可能正在运行的 OnPreDrawSubmit 回调完整返回后再卸载各渲染子模块，避免并发访问已卸载模块
            var spin = new SpinWait();
            while (Volatile.Read(ref _activePreDrawSubmitCount) > 0)
                spin.SpinOnce();

            lock (_lifecycleLock)
            {
#if DEBUG
                _shaderHotReloader?.Dispose();
                _shaderHotReloader = null;
                Shaders.SetLogger(null);
                Targets.SetLogger(null);
#endif

                // 按依赖严格逆序卸载各渲染子模块（所有 GPU Buffer/RT 均安全派发到主线程释放）
                PostProcess.Unload();
                Commands.Unload();
                Meshes.Unload();
                Materials.Unload();
                Targets.Unload();
                Shaders.Unload();

                _frameCounter = 0;
                _contextRevision = 0;
                _lastDeltaTime = 0f;
                _contextCaptured = false;
                _frameContext = default;
            }
        }

        /// <summary>使本帧已缓存的视图上下文失效，下次读取时按当前视口与相机重新采集。</summary>
        public static void InvalidateViewContext()
        {
            if (!Main.dedServ)
                Shaders.AssertMainThread("RenderManager.InvalidateViewContext");

            _contextCaptured = false;
        }

        private static void OnPreDraw(GameTime gameTime)
        {
            if (_unloading)
                return;

            Action? submitHandlers;
            lock (_lifecycleLock)
            {
                if (_unloading)
                    return;

                Shaders.EnsureMainThreadBound(Environment.CurrentManagedThreadId);
#if DEBUG
                _shaderHotReloader?.ProcessPendingOnMainThread();
#endif
                _frameCounter++;
                _lastDeltaTime = (float)gameTime.ElapsedGameTime.TotalSeconds;
                _contextCaptured = false;
                submitHandlers = OnPreDrawSubmit;
                if (submitHandlers is not null)
                    Interlocked.Increment(ref _activePreDrawSubmitCount);
            }

            // 锁外触发帧前提交事件，避免订阅方内部持锁或提交命令引发锁序死锁
            if (submitHandlers is not null)
            {
                try
                {
                    if (!_unloading)
                        submitHandlers.Invoke();
                }
                finally
                {
                    Interlocked.Decrement(ref _activePreDrawSubmitCount);
                }
            }
        }

        private static void Hook_DoDraw(On_Main.orig_DoDraw orig, Main self, GameTime gameTime)
        {
            try
            {
                orig(self, gameTime);
            }
            finally
            {
                if (!_unloading && !Main.dedServ)
                {
                    lock (_lifecycleLock)
                    {
                        if (!_unloading)
                        {
                            Commands.EndFrame();
                            Meshes.EndFrame();
                            Materials.EndFrame();
                            Targets.EndFrame();
                        }
                    }
                }
            }
        }

        private static void Hook_DoDraw_UpdateCameraPosition(On_Main.orig_DoDraw_UpdateCameraPosition orig)
        {
            orig();
            if (!_unloading && !Main.dedServ)
                PostProcess.NotifyPreDrawViewEffects(Main.GameViewMatrix.Effects);
            FlushTimingSafe(RenderTiming.PreDrawOffscreen, invalidateAfter: true);
        }

        private static void Hook_DrawProjectiles(On_Main.orig_DrawProjectiles orig, Main self)
        {
            FlushTimingSafe(RenderTiming.BeforeProjectiles);
            orig(self);
            FlushTimingSafe(RenderTiming.AfterProjectiles);
        }

        private static void Hook_DrawDust(On_Main.orig_DrawDust orig, Main self)
        {
            orig(self);
            FlushTimingSafe(RenderTiming.AfterDust);
        }

        private static void Hook_FilterManager_EndCapture(On_FilterManager.orig_EndCapture_RenderTarget2D_RenderTarget2D_RenderTarget2D orig,
            FilterManager self, RenderTarget2D finalTexture, RenderTarget2D screenTarget1, RenderTarget2D screenTarget2)
        {
            // 先让原版 EndCapture 完成 skyTarget + screenTarget 合成、原版滤镜、重力翻转并写入 Main.finalScreenTarget
            orig(self, finalTexture, screenTarget1, screenTarget2);

            if (!_unloading && !Main.dedServ)
            {
                GraphicsDevice? gd = Main.graphics?.GraphicsDevice;
                if (gd is { IsDisposed: false })
                {
                    lock (_lifecycleLock)
                    {
                        if (!_unloading)
                            PostProcess.ExecuteAfterEndCapture(gd, finalTexture);
                    }
                }
            }
        }

        private static void Hook_DrawInterface(On_Main.orig_DrawInterface orig, Main self, GameTime gameTime)
        {
            orig(self, gameTime);
            // DrawInterface 位于 EndCapture 之后（后备缓冲），刷新视口上下文避免沿用离屏 screenTarget 尺寸
            FlushTimingSafe(RenderTiming.AfterInterface, invalidateBefore: true);
        }

        private static void FlushTimingSafe(RenderTiming timing, bool invalidateBefore = false, bool invalidateAfter = false)
        {
            if (_unloading || Main.dedServ)
                return;

            lock (_lifecycleLock)
            {
                if (_unloading)
                    return;

                if (invalidateBefore)
                    _contextCaptured = false;

                Commands.Flush(timing);

                if (invalidateAfter)
                    _contextCaptured = false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CaptureCurrentContext()
        {
            ulong nextRevision = _contextRevision + 1;
            _frameContext = RenderFrameContext.Capture(_frameCounter, nextRevision, _lastDeltaTime);
            Volatile.Write(ref _contextRevision, nextRevision);
            _contextCaptured = true;
        }
    }
}
