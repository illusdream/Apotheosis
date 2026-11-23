#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Apotheosis.Runtime.Foundation;
#if DEBUG
using log4net;
#endif
using Terraria;

namespace Apotheosis.Runtime.Rendering.Shaders
{
    /// <summary>着色器管理模块，负责句柄注册、路径去重、主线程按需加载与全局参数同步。</summary>
    public sealed class ShaderModule
    {
        // 定长 256 槽位（槽位 0 保留为 null，1..255 对应 8 位 SlotIndex），永不扩容，保证并发无锁读取安全
        private readonly ManagedShader?[] _slots = new ManagedShader?[byte.MaxValue + 1];
        private readonly object _registerLock = new();
        private readonly Dictionary<string, ShaderHandle> _pathToHandle = new(StringComparer.OrdinalIgnoreCase);

        private int _count;
        private byte _generation = RuntimeConstants.InitialGeneration;
        private int _mainThreadId;
#if DEBUG
        private ILog? _logger;
        private readonly HashSet<string> _warnedKeys = new(StringComparer.Ordinal);
#endif

        /// <summary>当前已注册并受管的着色器实例总数。</summary>
        public int RegisteredCount => Volatile.Read(ref _count);

        /// <summary>当前模块代际号（每次卸载后递增，用于使旧句柄立即失效）。</summary>
        public byte CurrentGeneration => Volatile.Read(ref _generation);

        internal bool IsOnMainThread
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                int mainTid = Volatile.Read(ref _mainThreadId);
                if (mainTid != 0)
                    return Environment.CurrentManagedThreadId == mainTid;

                return Program.IsMainThread;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void EnsureMainThreadBound(int threadId)
        {
            if (Volatile.Read(ref _mainThreadId) == 0)
                Volatile.Write(ref _mainThreadId, threadId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void AssertMainThread(string contextName)
        {
            if (!IsOnMainThread)
            {
                throw new InvalidOperationException(
                    $"渲染/着色器 GPU 操作 \"{contextName}\" 必须在主线程执行；"
                    + "工作线程只应获取 ShaderHandle 写入 RenderCommand，由主线程 Flush 时再 Apply。");
            }
        }

#if DEBUG
        internal void SetLogger(ILog? logger)
        {
            Volatile.Write(ref _logger, logger);
            if (logger is null)
            {
                lock (_warnedKeys)
                {
                    _warnedKeys.Clear();
                }
            }
        }

        internal void LogDebugWarningOnce(string key, string message)
        {
            lock (_warnedKeys)
            {
                if (!_warnedKeys.Add(key))
                    return;
            }

            Volatile.Read(ref _logger)?.Warn(message);
        }
#endif

        /// <summary>注册或获取指定资源路径对应的受管着色器句柄（服务端返回无效句柄）。</summary>
        public ShaderHandle GetOrLoad(string assetPath, string[]? parameterNames = null)
        {
            if (Main.dedServ || string.IsNullOrWhiteSpace(assetPath))
                return ShaderHandle.Invalid;

            lock (_registerLock)
            {
                if (_pathToHandle.TryGetValue(assetPath, out ShaderHandle existing)
                    && TryGet(existing, out ManagedShader? existingShader))
                {
                    if (parameterNames is { Length: > 0 } && existingShader.ParameterSlotCount == 0)
                        existingShader.EnsureParameterLayout(parameterNames);

                    return existing;
                }

                return RegisterLocked(assetPath, parameterNames ?? Array.Empty<string>());
            }
        }

        /// <summary>校验句柄是否指向当前代际下的有效受管着色器（无锁、线程安全）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsValid(ShaderHandle handle)
        {
            if (handle.RawValue == RuntimeConstants.IndexNone || handle.Generation != Volatile.Read(ref _generation))
                return false;

            byte slot = handle.SlotIndex;
            return slot != RuntimeConstants.IndexNone
                   && slot <= Volatile.Read(ref _count)
                   && Volatile.Read(ref _slots[slot]) is not null;
        }

        /// <summary>O(1) 无锁安全解析句柄对应的唯一 <see cref="ManagedShader"/> 实例。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(ShaderHandle handle, [NotNullWhen(true)] out ManagedShader? shader)
        {
            if (handle.Generation == Volatile.Read(ref _generation))
            {
                byte slot = handle.SlotIndex;
                if (slot != RuntimeConstants.IndexNone && slot <= Volatile.Read(ref _count))
                {
                    shader = Volatile.Read(ref _slots[slot]);
                    return shader is not null;
                }
            }

            shader = null;
            return false;
        }

        /// <summary>O(1) 获取句柄对应的唯一 <see cref="ManagedShader"/> 实例；无效时返回 <c>null</c>。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ManagedShader? GetOrNull(ShaderHandle handle)
            => TryGet(handle, out ManagedShader? shader) ? shader : null;

        /// <summary>
        /// 渲染热路径无分支直取（调用方需先确保 <paramref name="handle"/> 有效，供后续合批器 <c>RenderCommand</c> 消费）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ManagedShader GetUnchecked(ShaderHandle handle) => _slots[handle.SlotIndex]!;

        /// <summary>
        /// 统一对所有已注册且已加载 GPU 资源的受管着色器强制同步帧级全局参数（零装箱、不触发惰性加载）。
        /// </summary>
        public void SyncAllGlobals(in RenderFrameContext context)
        {
            if (Main.dedServ)
                return;

            AssertMainThread("ShaderModule.SyncAllGlobals");

            ManagedShader?[] slots = _slots;
            int count = Volatile.Read(ref _count);
            for (int i = 1; i <= count; i++)
            {
                ManagedShader? shader = Volatile.Read(ref slots[i]);
                if (shader is null || !shader.IsGpuLoaded)
                    continue;

                shader.ForceSyncGlobals(in context);
            }
        }

        /// <summary>
        /// 通过 <see cref="IShader"/> 接口为任意自定义着色器实现统一下发帧级全局参数。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ApplyGlobalsViaInterface(IShader shader, in RenderFrameContext context)
        {
            if (shader is ManagedShader managed)
            {
                managed.ForceSyncGlobals(in context);
                return;
            }

            shader.SetTime(context.Time);
            shader.SetTransformMatrix(in context.WorldTransformMatrix);
            shader.SetScreenResolution(context.ScreenResolution);
            shader.SetZoom(context.Zoom);
        }

        /// <summary>
        /// 通过句柄直接应用着色器 Pass（供后续合批器在状态切换时按 <see cref="ShaderHandle"/> 调度）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Apply(ShaderHandle handle, in RenderFrameContext context, int techniqueIndex = 0, int passIndex = 0)
        {
            if (TryGet(handle, out ManagedShader? shader))
                shader.Apply(in context, techniqueIndex, passIndex);
        }

        /// <summary>在模组卸载时清空全部着色器槽位并递增代际号，使所有外部缓存的 <see cref="ShaderHandle"/> 立即失效。</summary>
        internal void Unload()
        {
            lock (_registerLock)
            {
                byte nextGen = unchecked((byte)(_generation + 1));
                if (nextGen == RuntimeConstants.IndexNone)
                    nextGen = RuntimeConstants.InitialGeneration;

                Volatile.Write(ref _generation, nextGen);

                int count = _count;
                if (count > 0)
                {
#if DEBUG
                    for (int i = 1; i <= count; i++)
                    {
                        _slots[i]?.DisposeHotReloadedEffect();
                    }
#endif
                    Array.Clear(_slots, 0, count + 1);
                    Volatile.Write(ref _count, 0);
                }

                _pathToHandle.Clear();
                Volatile.Write(ref _mainThreadId, 0);
            }
        }

#if DEBUG
        internal bool TryGetByAssetPath(string assetPath, [NotNullWhen(true)] out ManagedShader? shader)
        {
            lock (_registerLock)
            {
                if (_pathToHandle.TryGetValue(assetPath, out ShaderHandle handle) && TryGet(handle, out shader))
                    return true;
            }

            shader = null;
            return false;
        }

        internal string[] GetRegisteredAssetPathsSnapshot()
        {
            lock (_registerLock)
            {
                if (_pathToHandle.Count == 0)
                    return Array.Empty<string>();

                var result = new string[_pathToHandle.Count];
                int index = 0;
                foreach (string path in _pathToHandle.Keys)
                {
                    result[index++] = path;
                }

                return result;
            }
        }
#endif

        private ShaderHandle RegisterLocked(string assetPath, string[] parameterNames)
        {
            int nextSlot = _count + 1;
            if (nextSlot > byte.MaxValue)
                throw new InvalidOperationException($"受管着色器数量超出 ShaderHandle 8 位槽位上限 ({byte.MaxValue})。");

            var handle = new ShaderHandle((byte)nextSlot, _generation);
            var managed = new ManagedShader(this, handle, assetPath, parameterNames);

            // 先发布槽位实例再递增 _count，保证并发读线程绝不会看到未初始化的槽位
            Volatile.Write(ref _slots[nextSlot], managed);
            Volatile.Write(ref _count, nextSlot);
            _pathToHandle[assetPath] = handle;

            return handle;
        }
    }
}
