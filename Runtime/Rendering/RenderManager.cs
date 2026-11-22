#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Apotheosis.Runtime.Rendering.Shaders;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis.Runtime.Rendering
{
    /// <summary>
    /// 全局渲染管线大系统（唯一渲染 <see cref="ModSystem"/> 入口）：
    /// 统一编排渲染帧上下文采集、模块生命周期与卸载顺序。
    /// <para>
    /// 帧上下文采用“首读单点惰性采集”策略：<see cref="Main.OnPreDraw"/> 仅推进帧号与失效标记（零矩阵开销），
    /// 待当帧首次实际绘制读取 <see cref="FrameContext"/> 时再采集唯一一次，确保拿到全部 Mod 修改完毕后的最终视口矩阵。
    /// </para>
    /// </summary>
    public sealed class RenderManager : ModSystem
    {
        private static readonly object _lifecycleLock = new();
        private static volatile bool _unloading;

        private static RenderFrameContext _frameContext;
        private static ulong _frameCounter;
        private static ulong _contextRevision;
        private static float _lastDeltaTime;
        private static bool _contextCaptured;
#if DEBUG
        private static ShaderHotReloader? _shaderHotReloader;
#endif

        /// <summary>受管着色器模块（负责线程安全句柄注册、按需 GPU 加载、唯一实例去重与全局参数同步）。</summary>
        public static ShaderModule Shaders { get; } = new();

        /// <summary>当前帧渲染上下文快照（必须在主线程读取，本帧首次读取时按需采集唯一一次）。</summary>
        public static RenderFrameContext FrameContext => FrameContextRef;

        /// <summary>当前帧渲染上下文只读引用（供主线程渲染热路径零拷贝读取）。</summary>
        internal static ref readonly RenderFrameContext FrameContextRef
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (!_contextCaptured && _frameCounter != 0 && !Main.dedServ)
                {
                    Shaders.AssertMainThread("RenderManager.FrameContext");
                    CaptureCurrentContext();
                }

                return ref _frameContext;
            }
        }

        /// <summary>当前渲染帧号。</summary>
        public static ulong FrameId => Volatile.Read(ref _frameCounter);

        /// <summary>当前帧上下文修订号（每次实际采集视图上下文时递增）。</summary>
        public static ulong ContextRevision => FrameContextRef.ContextRevision;

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
#endif
            }

            if (!Main.dedServ)
            {
                Main.RunOnMainThread(() => Shaders.EnsureMainThreadBound(Environment.CurrentManagedThreadId));
                Main.OnPreDraw += OnPreDraw;
#if DEBUG
                _shaderHotReloader = ShaderHotReloader.TryStart(Shaders, Mod.Logger, Mod.Name);
#endif
            }
        }

        public override void Unload()
        {
            // 先置位卸载标记并摘除事件，再通过 _lifecycleLock 与可能正在运行的 OnPreDraw 严格串行化
            _unloading = true;

            if (!Main.dedServ)
            {
                Main.OnPreDraw -= OnPreDraw;
            }

            lock (_lifecycleLock)
            {
#if DEBUG
                _shaderHotReloader?.Dispose();
                _shaderHotReloader = null;
                Shaders.SetLogger(null);
#endif

                // 按依赖逆序卸载各渲染子模块（未来合批/顶点池先卸载，最后释放 Shader 与 RT）
                Shaders.Unload();

                _frameCounter = 0;
                _contextRevision = 0;
                _lastDeltaTime = 0f;
                _contextCaptured = false;
                _frameContext = default;
            }
        }

        /// <summary>
        /// 使本帧已缓存的视图上下文失效（例如在同帧内切换了自定义 RenderTarget 或手动改动相机后调用），
        /// 下一次绘制读取时将按当前活动 <see cref="Microsoft.Xna.Framework.Graphics.Viewport"/> 与相机状态重新采集。
        /// </summary>
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
