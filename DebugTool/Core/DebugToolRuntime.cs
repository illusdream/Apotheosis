#if DEBUG
#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Apotheosis.DebugTool.Gizmos;
using Apotheosis.DebugTool.Layout;
using Apotheosis.DebugTool.Renderer;
using Apotheosis.DebugTool.Theme;
using Apotheosis.Runtime.Foundation.Diagnostics;
using Hexa.NET.ImGui;
using HexaGen.Runtime;
using log4net;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.OS;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis.DebugTool.Core
{
    /// <summary>
    /// ImGui 调试工具核心运行时：负责 <c>cimgui.dll</c> 路径引导、FNA 渲染与输入挂钩、主线程懒初始化及热重载安全销毁。
    /// </summary>
    [NoJIT]
    internal static class DebugToolRuntime
    {
        private static ILog? _logger;

        private static bool _hooksInstalled;
        private static bool _gpuInitialized;
        private static bool _initFailed;
        private static bool _weShowedSystemCursor;
        private static int _mainThreadId;

        private static nint _imGuiContextHandle;
        private static FnaImGuiRenderer? _renderer;
        private static DebugInputHandler? _input;
        private static DebugPanelRegistry? _registry;
        private static DebugShellWindow? _shellWindow;
        private static GizmoCommandBuffer? _gizmoBuffer;
        private static GizmoOverlayRenderer? _gizmoRenderer;
        private static Stopwatch? _frameTimer;

        public static DebugThemeSettings Settings { get; } = new();
        public static GizmoSettings GizmoSettings { get; } = new();
        public static bool IsVisible { get; set; }
        public static bool IsGizmoOverlayActive => GizmoSettings.Enabled && !Main.gameMenu && !Main.mapFullscreen && !Main.inFancyUI;
        public static FnaImGuiRenderer? Renderer => _renderer;
        public static DebugInputHandler? Input => _input;
        public static GizmoCommandBuffer? Gizmos => _gizmoBuffer;

        /// <summary>
        /// 注册自定义调试面板模块（可在模组任意 <c>#if DEBUG</c> 位置调用）。
        /// </summary>
        public static void RegisterPanel(IDebugPanel panel) => _registry?.Register(panel);

        public static void Initialize(string binDir, ILog logger)
        {
            _logger = logger;
            _initFailed = false;
            _gpuInitialized = false;
            IsVisible = false;

            ConfigureNativeLibraryLoader(binDir);

            _registry = new DebugPanelRegistry();
            _registry.DiscoverAndRegisterBuiltIn();
            _input = new DebugInputHandler();
            _shellWindow = new DebugShellWindow();
            _gizmoBuffer = new GizmoCommandBuffer(GizmoSettings);
            _gizmoRenderer = new GizmoOverlayRenderer();
            DebugGizmos.AttachSink(_gizmoBuffer);
            _frameTimer = Stopwatch.StartNew();

            if (!_hooksInstalled)
            {
                On_Main.DoUpdate += Hook_DoUpdate;
                On_Main.DoDraw += Hook_DoDraw;
                On_Main.DrawCursor += Hook_DrawCursor;
                On_Main.DrawThickCursor += Hook_DrawThickCursor;
                InstallCursorVisibilityHooks();
                _hooksInstalled = true;
            }
        }

        public static void OnPostUpdateInput()
        {
            if (_initFailed || _input is null)
                return;

            _input.ApplyTerrariaInputIsolation(IsVisible);
        }

        public static void OnWorldUnload()
        {
            _gizmoBuffer?.ClearWorldTransient();
        }

        public static void Shutdown()
        {
            IsVisible = false;

            if (_gizmoBuffer is not null)
            {
                DebugGizmos.DetachSink(_gizmoBuffer);
                _gizmoBuffer.ClearAll();
                _gizmoBuffer = null;
            }

            _gizmoRenderer = null;
            WorldScreenTransform.ResetCurrent();

            if (_hooksInstalled)
            {
                On_Main.DoUpdate -= Hook_DoUpdate;
                On_Main.DoDraw -= Hook_DoDraw;
                On_Main.DrawCursor -= Hook_DrawCursor;
                On_Main.DrawThickCursor -= Hook_DrawThickCursor;
                _hooksInstalled = false;
            }

            _input?.Dispose();
            _input = null;

            _registry?.UnloadAll();
            _registry = null;

            _shellWindow = null;
            _frameTimer = null;

            // FNA GPU 资源与 ImGui 上下文必须在主线程释放
            var rendererToDispose = _renderer;
            nint contextHandleToDestroy = _imGuiContextHandle;
            bool wasGpuInit = _gpuInitialized;

            _renderer = null;
            _imGuiContextHandle = 0;
            _gpuInitialized = false;

            if (wasGpuInit)
            {
                void DisposeOnMainThread()
                {
                    try
                    {
                        SyncSystemCursor(false);
                        rendererToDispose?.Dispose();
                        DestroyContextUnsafe(contextHandleToDestroy);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn($"[Apotheosis.DebugTool] Cleanup warning: {ex.Message}");
                    }
                }

                if (Environment.CurrentManagedThreadId == _mainThreadId)
                    DisposeOnMainThread();
                else
                    Main.QueueMainThreadAction(DisposeOnMainThread);
            }

            _logger = null;
        }

        private static void Hook_DoUpdate(On_Main.orig_DoUpdate orig, Main self, ref GameTime gameTime)
        {
            float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
            if (dt <= 0f || dt > 0.25f)
                dt = 1f / 60f;

            if (!_initFailed && _input is not null)
            {
                if (_input.PollToggleHotkey())
                {
                    IsVisible = !IsVisible;
                    if (!IsVisible)
                    {
                        _input.ReleaseTerrariaInputLock();
                        SyncSystemCursor(false);
                    }
                }

                if (!IsVisible || Main.instance is null || !Main.instance.IsActive)
                    SyncSystemCursor(false);

                // 在原版输入逻辑前同步键盘屏蔽状态，避免在 ImGui 打字触发快捷键
                _input.ApplyTerrariaInputIsolation(IsVisible);

                if (_registry is not null)
                {
                    var panels = _registry.Panels;
                    for (int i = 0; i < panels.Count; i++)
                    {
                        try
                        {
                            panels[i].OnUpdate(dt);
                        }
                        catch
                        {
                            // 单面板更新异常不阻断主循环
                        }
                    }
                }
            }

            _gizmoBuffer?.BeginUpdate();
            try
            {
                orig(self, ref gameTime);
            }
            finally
            {
                // orig 结束后 Main.gamePaused / Main.gameMenu / Main.mapFullscreen 已反映本帧最终状态
                _gizmoBuffer?.EndUpdate(dt, IsGizmoOverlayActive, Main.gamePaused);
            }
        }

        private static void Hook_DoDraw(On_Main.orig_DoDraw orig, Main self, GameTime gameTime)
        {
            _gizmoBuffer?.BeginDrawPhase();
            try
            {
                orig(self, gameTime);

                bool drawShell = IsVisible;
                bool drawGizmos = IsGizmoOverlayActive && _gizmoBuffer is not null && _gizmoRenderer is not null;

                if (_initFailed || (!drawShell && !drawGizmos) || Main.dedServ)
                    return;

                GraphicsDevice? gd = Main.graphics?.GraphicsDevice;
                if (gd is null || gd.IsDisposed)
                    return;

                if (!_gpuInitialized)
                {
                    if (!TryInitializeGpuAndImGui(gd))
                        return;
                }

                RenderFrame(gd, drawShell, drawGizmos);
            }
            finally
            {
                _gizmoBuffer?.EndDrawPhase();
            }
        }

        private static unsafe bool TryInitializeGpuAndImGui(GraphicsDevice gd)
        {
            try
            {
                _mainThreadId = Environment.CurrentManagedThreadId;

                var ctx = ImGui.CreateContext();
                _imGuiContextHandle = (nint)ctx.Handle;
                ImGui.SetCurrentContext(ctx);

                var io = ImGui.GetIO();
                io.IniFilename = null; // 避免在 tModLoader Steam 目录下残留 imgui.ini
                io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
                io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures | ImGuiBackendFlags.RendererHasVtxOffset;

                CyberpunkTheme.ConfigureFonts(io);
                CyberpunkTheme.ApplyStyle(Settings);

                _renderer = new FnaImGuiRenderer(gd);
                _input?.Attach();

                _gpuInitialized = true;
                _logger?.Info($"[Apotheosis.DebugTool] ImGui + FNA backend ready. Fonts: {CyberpunkTheme.LoadedFontSummary}");
                return true;
            }
            catch (Exception ex)
            {
                _initFailed = true;
                IsVisible = false;
                _logger?.Error("[Apotheosis.DebugTool] Failed to initialize ImGui FNA backend.", ex);
                return false;
            }
        }

        private static unsafe void RenderFrame(GraphicsDevice gd, bool drawShell, bool drawGizmos)
        {
            if (_renderer is null || _input is null || _registry is null || _shellWindow is null || _imGuiContextHandle == 0)
                return;

            try
            {
                ImGui.SetCurrentContext(new ImGuiContextPtr((ImGuiContext*)_imGuiContextHandle));

                int fbWidth = gd.PresentationParameters.BackBufferWidth;
                int fbHeight = gd.PresentationParameters.BackBufferHeight;
                if (fbWidth <= 0 || fbHeight <= 0)
                    return;

                var io = ImGui.GetIO();
                io.DisplaySize = new System.Numerics.Vector2(fbWidth, fbHeight);
                io.DisplayFramebufferScale = System.Numerics.Vector2.One;

                float dt = 1f / 60f;
                if (_frameTimer is not null)
                {
                    dt = (float)_frameTimer.Elapsed.TotalSeconds;
                    _frameTimer.Restart();
                    if (dt <= 0.0001f || dt > 0.25f)
                        dt = 1f / 60f;
                }

                io.DeltaTime = dt;

                // 仅在主面板打开时接收键鼠输入；纯 Gizmos 叠层模式下完全透传游戏输入且不显示系统光标
                _input.UpdateImGuiInput(io, drawShell, fbWidth, fbHeight);
                CyberpunkTheme.ApplyStyle(Settings);

                ImGui.NewFrame();

                if (drawGizmos && _gizmoBuffer is not null && _gizmoRenderer is not null)
                    _gizmoRenderer.Render(_gizmoBuffer, fbWidth, fbHeight);

                if (drawShell)
                {
                    bool open = IsVisible;
                    _shellWindow.Draw(_registry, Settings, _input, ref open);
                    if (!open)
                    {
                        IsVisible = false;
                        _input.ReleaseTerrariaInputLock();
                        SyncSystemCursor(false);
                    }
                    else
                    {
                        SyncSystemCursor(io.WantCaptureMouse && Main.instance is { IsActive: true });
                    }
                }
                else
                {
                    SyncSystemCursor(false);
                }

                ImGui.Render();
                _renderer.RenderDrawData(ImGui.GetDrawData());
            }
            catch (Exception ex)
            {
                _initFailed = true;
                IsVisible = false;
                _input.ReleaseTerrariaInputLock();
                SyncSystemCursor(false);
                _logger?.Error("[Apotheosis.DebugTool] Runtime render failure; DebugTool has been disabled for this session.", ex);
            }
        }

        private static void Hook_DrawCursor(On_Main.orig_DrawCursor orig, Microsoft.Xna.Framework.Vector2 bonus, bool smart)
        {
            // 光标悬停在 ImGui 面板上方时改由 Windows 系统光标显示，跳过原版软件光标避免半透明背景透出双光标
            if (IsVisible && _input is { WantCaptureMouse: true } && _weShowedSystemCursor)
                return;

            orig(bonus, smart);
        }

        private static Microsoft.Xna.Framework.Vector2 Hook_DrawThickCursor(On_Main.orig_DrawThickCursor orig, bool smart)
        {
            if (IsVisible && _input is { WantCaptureMouse: true } && _weShowedSystemCursor)
                return Microsoft.Xna.Framework.Vector2.Zero;

            return orig(smart);
        }

        private static void InstallCursorVisibilityHooks()
        {
            // 拦截 FocusHelper.UpdateFocus 每帧调用 IsMouseVisible = false 与 ForceCursorHidden() 导致的系统光标 60Hz 闪烁
            MethodInfo? setMouseVisible = typeof(Game).GetProperty(nameof(Game.IsMouseVisible))?.SetMethod;
            if (setMouseVisible is not null)
                MonoModHooks.Add(setMouseVisible, (Action<Action<Game, bool>, Game, bool>)Hook_SetIsMouseVisible);

            IMouseNotifier? mouseNotifier = Platform.Get<IMouseNotifier>();
            MethodInfo? forceHidden = mouseNotifier?.GetType().GetMethod(
                "ForceCursorHidden",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (forceHidden is not null)
                MonoModHooks.Add(forceHidden, (Action<Action<IMouseNotifier>, IMouseNotifier>)Hook_ForceCursorHidden);
        }

        private static void Hook_SetIsMouseVisible(Action<Game, bool> orig, Game self, bool value)
        {
            if (_weShowedSystemCursor && !value)
                return;

            orig(self, value);
        }

        private static void Hook_ForceCursorHidden(Action<IMouseNotifier> orig, IMouseNotifier self)
        {
            if (_weShowedSystemCursor)
                return;

            orig(self);
        }

        private static void SyncSystemCursor(bool showSystemCursor)
        {
            if (Main.instance is null)
                return;

            if (showSystemCursor)
            {
                _weShowedSystemCursor = true;
                if (!Main.instance.IsMouseVisible)
                    Main.instance.IsMouseVisible = true;
            }
            else if (_weShowedSystemCursor)
            {
                // 必须先释放拦截标记，再将系统光标恢复隐藏
                _weShowedSystemCursor = false;
                Main.instance.IsMouseVisible = false;
            }
        }

        private static unsafe void DestroyContextUnsafe(nint handle)
        {
            if (handle != 0)
                ImGui.DestroyContext(new ImGuiContextPtr((ImGuiContext*)handle));
        }

        private static void ConfigureNativeLibraryLoader(string binDir)
        {
            string win64NativeDir = Path.Combine(binDir, "runtimes", "win-x64", "native");
            if (!LibraryLoader.CustomLoadFolders.Contains(win64NativeDir))
                LibraryLoader.CustomLoadFolders.Add(win64NativeDir);

            if (!LibraryLoader.CustomLoadFolders.Contains(binDir))
                LibraryLoader.CustomLoadFolders.Add(binDir);
        }
    }
}
#endif
