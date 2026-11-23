#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis.Runtime.Rendering.Shaders
{
    /// <summary>
    /// 常用全局参数在 HLSL（<c>.fx</c> / <c>.xnb</c>）中的约定名称。
    /// </summary>
    public static class ShaderGlobalParamNames
    {
        public const string Time = "uTime";
        public const string TransformMatrix = "uTransformMatrix";
        public const string ScreenResolution = "uScreenResolution";
        public const string Zoom = "uZoom";
    }

    /// <summary>受管着色器实例，封装底层 FNA <see cref="Effect"/> 与参数/Pass 缓存。</summary>
    public sealed class ManagedShader : IShader
    {
        [Flags]
        private enum GlobalOverrideFlags : byte
        {
            None = 0,
            Time = 1 << 0,
            TransformMatrix = 1 << 1,
            ScreenResolution = 1 << 2,
            Zoom = 1 << 3,
            All = Time | TransformMatrix | ScreenResolution | Zoom,
        }

        private readonly ShaderModule _owner;
        private Asset<Effect>? _asset;
#if DEBUG
        private Effect? _hotReloadedEffect;
#endif
        private string[] _parameterNames;
        private string[] _boundParameterNames = Array.Empty<string>();
        private EffectParameter?[] _parameterCache;

        private Effect? _boundEffect;
        private EffectParameter? _timeParam;
        private EffectParameter? _transformMatrixParam;
        private EffectParameter? _screenResolutionParam;
        private EffectParameter? _zoomParam;

        private EffectTechnique[] _techniques = Array.Empty<EffectTechnique>();
        private EffectPass[][] _passes = Array.Empty<EffectPass[]>();

        private ulong _lastSyncedRevision;
        private bool _lastSyncedUsedProjection;
        private GlobalOverrideFlags _pendingManualOverrides;
        private GlobalOverrideFlags _dirtyGlobalParams;

        internal ManagedShader(ShaderModule owner, ShaderHandle handle, string assetPath, string[] parameterNames)
        {
            _owner = owner;
            Handle = handle;
            AssetPath = assetPath;
            _parameterNames = parameterNames;
            _parameterCache = parameterNames.Length > 0
                ? new EffectParameter?[parameterNames.Length]
                : Array.Empty<EffectParameter?>();
        }

        /// <inheritdoc />
        public ShaderHandle Handle { get; }

        /// <summary>对应的 tModLoader 资源路径（不含扩展名）。</summary>
        public string AssetPath { get; }

        /// <summary>无副作用检查：底层 GPU <see cref="Effect"/> 是否已绑定且未释放。</summary>
        internal bool IsGpuLoaded
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if DEBUG
                if (Volatile.Read(ref _hotReloadedEffect) is { IsDisposed: false })
                    return true;
#endif
                return Volatile.Read(ref _boundEffect) is { IsDisposed: false };
            }
        }

        /// <inheritdoc />
        /// <remarks>无副作用查询：绝不隐式触发 GPU 加载，可安全用于状态检查；需要显式主线程加载请调用 <see cref="EnsureLoaded"/>。</remarks>
        public bool IsReady
        {
            get
            {
                if (IsGpuLoaded)
                    return true;

                Asset<Effect>? asset = Volatile.Read(ref _asset);
                return asset is { IsLoaded: true, Value.IsDisposed: false };
            }
        }

        /// <summary>在主线程显式触发底层 <see cref="Effect"/> 加载与参数绑定，返回是否已就绪。</summary>
        public bool EnsureLoaded() => EnsureBound();

        /// <summary>
        /// 获取底层 FNA <see cref="Effect"/> 实例（必须在主线程调用，可用于与原版 <see cref="SpriteBatch"/> 互操作）。
        /// </summary>
        public Effect? RawEffect => EnsureBound() ? _boundEffect : null;

        /// <summary>已注册的自定义参数槽位总数。</summary>
        public int ParameterSlotCount => Volatile.Read(ref _parameterNames).Length;

        /// <summary>已缓存的 Technique 数量。</summary>
        public int TechniqueCount => EnsureBound() ? _techniques.Length : 0;

        /// <summary>为以空参数表注册的实例补充自定义参数名布局。</summary>
        internal void EnsureParameterLayout(string[] parameterNames)
        {
            if (parameterNames.Length == 0 || Volatile.Read(ref _parameterNames).Length > 0)
                return;

            Volatile.Write(ref _parameterNames, parameterNames);
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTime(float time)
        {
            if (!EnsureBound())
                return;

            _timeParam?.SetValue(time);
            MarkManualOverride(GlobalOverrideFlags.Time);
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTransformMatrix(in Matrix matrix)
        {
            if (!EnsureBound())
                return;

            _transformMatrixParam?.SetValue(matrix);
            MarkManualOverride(GlobalOverrideFlags.TransformMatrix);
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetScreenResolution(Vector2 resolution)
        {
            if (!EnsureBound())
                return;

            _screenResolutionParam?.SetValue(resolution);
            MarkManualOverride(GlobalOverrideFlags.ScreenResolution);
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetZoom(Vector2 zoom)
        {
            if (!EnsureBound())
                return;

            _zoomParam?.SetValue(zoom);
            MarkManualOverride(GlobalOverrideFlags.Zoom);
        }

        /// <summary>
        /// 强制用给定的 <see cref="RenderFrameContext"/> 覆盖同步全部通用全局参数（并清除所有手动覆写与脏状态）。
        /// </summary>
        public void ForceSyncGlobals(in RenderFrameContext context)
        {
            if (!EnsureBound())
                return;

            _timeParam?.SetValue(context.Time);
            _transformMatrixParam?.SetValue(context.WorldTransformMatrix);
            _screenResolutionParam?.SetValue(context.ScreenResolution);
            _zoomParam?.SetValue(context.Zoom);

            _lastSyncedRevision = context.ContextRevision;
            _lastSyncedUsedProjection = false;
            _pendingManualOverrides = GlobalOverrideFlags.None;
            _dirtyGlobalParams = GlobalOverrideFlags.None;
        }

        /// <summary>同步当前绘制所需的帧级全局参数（保留本次手动覆写并自动恢复后续默认）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SyncFrameGlobalsIfNeeded(in RenderFrameContext context, bool useSpriteBatchProjection = false)
        {
            if (context.ContextRevision == 0)
                return;

            bool revisionChanged = _lastSyncedRevision != context.ContextRevision;
            bool modeChanged = _lastSyncedUsedProjection != useSpriteBatchProjection;

            if (!revisionChanged
                && !modeChanged
                && _pendingManualOverrides == GlobalOverrideFlags.None
                && _dirtyGlobalParams == GlobalOverrideFlags.None)
            {
                return;
            }

            if (!EnsureBound())
                return;

            GlobalOverrideFlags manual = _pendingManualOverrides;
            _pendingManualOverrides = GlobalOverrideFlags.None;

            GlobalOverrideFlags needSync = revisionChanged
                ? GlobalOverrideFlags.All
                : _dirtyGlobalParams;

            if (modeChanged)
                needSync |= GlobalOverrideFlags.TransformMatrix;

            GlobalOverrideFlags toWrite = needSync & ~manual;

            if ((toWrite & GlobalOverrideFlags.Time) != GlobalOverrideFlags.None)
                _timeParam?.SetValue(context.Time);
            if ((toWrite & GlobalOverrideFlags.TransformMatrix) != GlobalOverrideFlags.None)
                _transformMatrixParam?.SetValue(useSpriteBatchProjection ? context.ProjectionMatrix : context.WorldTransformMatrix);
            if ((toWrite & GlobalOverrideFlags.ScreenResolution) != GlobalOverrideFlags.None)
                _screenResolutionParam?.SetValue(context.ScreenResolution);
            if ((toWrite & GlobalOverrideFlags.Zoom) != GlobalOverrideFlags.None)
                _zoomParam?.SetValue(context.Zoom);

            _dirtyGlobalParams = (_dirtyGlobalParams & ~toWrite) | manual;
            _lastSyncedRevision = context.ContextRevision;
            _lastSyncedUsedProjection = useSpriteBatchProjection;
        }

        /// <inheritdoc />
        public void Apply(int techniqueIndex = 0, int passIndex = 0)
        {
            Apply(in RenderManager.FrameContextRef, techniqueIndex, passIndex);
        }

        /// <summary>同步帧全局参数并应用指定 Technique/Pass。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Apply(in RenderFrameContext context, int techniqueIndex = 0, int passIndex = 0)
        {
            TryApply(in context, techniqueIndex, passIndex);
        }

        /// <summary>尝试同步帧全局参数并应用指定 Pass；仅在底层实际应用成功时返回 <c>true</c>。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryApply(in RenderFrameContext context, int techniqueIndex = 0, int passIndex = 0)
        {
            if (!EnsureBound())
                return false;

            SyncFrameGlobalsIfNeeded(in context, useSpriteBatchProjection: false);

            EffectTechnique[] techniques = _techniques;
            if ((uint)techniqueIndex >= (uint)techniques.Length)
            {
#if DEBUG
                _owner.LogDebugWarningOnce(
                    $"tech:{AssetPath}:{techniqueIndex}",
                    $"[Shader] '{AssetPath}' Technique index {techniqueIndex} out of range (count={techniques.Length}).");
#endif
                return false;
            }

            EffectPass[] passes = _passes[techniqueIndex];
            if ((uint)passIndex >= (uint)passes.Length)
            {
#if DEBUG
                _owner.LogDebugWarningOnce(
                    $"pass:{AssetPath}:{techniqueIndex}:{passIndex}",
                    $"[Shader] '{AssetPath}' Pass index {passIndex} out of range for technique {techniqueIndex} (count={passes.Length}).");
#endif
                return false;
            }

            Effect effect = _boundEffect!;
            EffectTechnique targetTechnique = techniques[techniqueIndex];
            if (!ReferenceEquals(effect.CurrentTechnique, targetTechnique))
                effect.CurrentTechnique = targetTechnique;

            passes[passIndex].Apply();
            return true;
        }

        /// <summary>同步 <see cref="SpriteBatch"/> 所需的全局参数并返回底层 <see cref="Effect"/>。</summary>
        public Effect? PrepareForSpriteBatch(int techniqueIndex = 0)
        {
            if (!EnsureBound())
                return null;

            SyncFrameGlobalsIfNeeded(in RenderManager.FrameContextRef, useSpriteBatchProjection: true);

            Effect effect = _boundEffect!;
            if ((uint)techniqueIndex < (uint)_techniques.Length)
            {
                EffectTechnique targetTechnique = _techniques[techniqueIndex];
                if (!ReferenceEquals(effect.CurrentTechnique, targetTechnique))
                    effect.CurrentTechnique = targetTechnique;
            }
#if DEBUG
            else
            {
                _owner.LogDebugWarningOnce(
                    $"sbtech:{AssetPath}:{techniqueIndex}",
                    $"[Shader] '{AssetPath}' PrepareForSpriteBatch technique index {techniqueIndex} out of range (count={_techniques.Length}).");
            }
#endif

            return effect;
        }

        // ==================== O(1) 缓存数组参数设置（由生成的强类型 Shader 结构体调用） ====================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBool(int slot, bool value)
        {
            EffectParameter? param = GetCachedParameter(slot);
            param?.SetValue(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetInt(int slot, int value)
        {
            EffectParameter? param = GetCachedParameter(slot);
            param?.SetValue(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetFloat(int slot, float value)
        {
            EffectParameter? param = GetCachedParameter(slot);
            param?.SetValue(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetFloatArray(int slot, float[] values)
        {
            EffectParameter? param = GetCachedParameter(slot);
            param?.SetValue(values);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVector2(int slot, Vector2 value)
        {
            EffectParameter? param = GetCachedParameter(slot);
            param?.SetValue(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVector2Array(int slot, Vector2[] values)
        {
            EffectParameter? param = GetCachedParameter(slot);
            param?.SetValue(values);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVector3(int slot, Vector3 value)
        {
            EffectParameter? param = GetCachedParameter(slot);
            param?.SetValue(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVector3Array(int slot, Vector3[] values)
        {
            EffectParameter? param = GetCachedParameter(slot);
            param?.SetValue(values);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVector4(int slot, Vector4 value)
        {
            EffectParameter? param = GetCachedParameter(slot);
            param?.SetValue(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVector4Array(int slot, Vector4[] values)
        {
            EffectParameter? param = GetCachedParameter(slot);
            param?.SetValue(values);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetColor(int slot, Color value)
        {
            EffectParameter? param = GetCachedParameter(slot);
            param?.SetValue(value.ToVector4());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetMatrix(int slot, in Matrix value)
        {
            EffectParameter? param = GetCachedParameter(slot);
            param?.SetValue(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetMatrixArray(int slot, Matrix[] values)
        {
            EffectParameter? param = GetCachedParameter(slot);
            param?.SetValue(values);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTexture(int slot, Texture? value)
        {
            EffectParameter? param = GetCachedParameter(slot);
            if (param is null)
                return;

            if (param.ParameterType is EffectParameterType.Texture
                or EffectParameterType.Texture1D
                or EffectParameterType.Texture2D
                or EffectParameterType.Texture3D
                or EffectParameterType.TextureCube)
            {
                param.SetValue(value);
            }
        }

        /// <summary>按名称兜底查找底层 <see cref="EffectParameter"/>（非常规/动态调试用途，热路径请使用槽位 Setter）。</summary>
        public EffectParameter? TryGetParameter(string name)
        {
            return EnsureBound() ? _boundEffect!.Parameters[name] : null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MarkManualOverride(GlobalOverrideFlags flag)
        {
            _pendingManualOverrides |= flag;
            _dirtyGlobalParams |= flag;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private EffectParameter? GetCachedParameter(int slot)
        {
            if (!EnsureBound())
                return null;

            EffectParameter?[] cache = _parameterCache;
            return (uint)slot < (uint)cache.Length ? cache[slot] : null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool EnsureBound()
        {
            if (Main.dedServ)
                return false;

            // 所有 GPU Effect 绑定与参数操作必须在主线程执行（无论资源是否已加载）
            _owner.AssertMainThread(AssetPath);

            string[] currentNames = Volatile.Read(ref _parameterNames);

#if DEBUG
            Effect? hotEffect = Volatile.Read(ref _hotReloadedEffect);
            if (hotEffect is { IsDisposed: false })
            {
                if (!ReferenceEquals(_boundEffect, hotEffect))
                    BindEffect(hotEffect, currentNames);
                else if (!ReferenceEquals(_boundParameterNames, currentNames))
                    BindCustomParameterSlots(hotEffect, currentNames);

                return true;
            }
#endif

            Asset<Effect>? asset = Volatile.Read(ref _asset);
            if (asset is null)
            {
                asset = ModContent.Request<Effect>(AssetPath, AssetRequestMode.ImmediateLoad);
                Volatile.Write(ref _asset, asset);
            }

            if (!asset.IsLoaded)
                return false;

            Effect? effect = asset.Value;
            if (effect is null || effect.IsDisposed)
                return false;

            if (!ReferenceEquals(_boundEffect, effect))
                BindEffect(effect, currentNames);
            else if (!ReferenceEquals(_boundParameterNames, currentNames))
                BindCustomParameterSlots(effect, currentNames);

            return true;
        }

#if DEBUG
        /// <summary>
        /// 在主线程无缝替换为热重载编译出的新 <see cref="Effect"/> 实例，并重新绑定全部缓存参数槽与 Technique/Pass。
        /// 仅释放上一次热重载创建的副本，绝不触碰 tModLoader 托管的原始 <see cref="Asset{Effect}"/>。
        /// </summary>
        internal void ApplyHotReloadedEffect(Effect newEffect)
        {
            Effect? oldHot = _hotReloadedEffect;
            Volatile.Write(ref _hotReloadedEffect, newEffect);
            BindEffect(newEffect, Volatile.Read(ref _parameterNames));

            if (oldHot is not null && !ReferenceEquals(oldHot, newEffect) && !oldHot.IsDisposed)
            {
                oldHot.Dispose();
            }
        }

        internal void DisposeHotReloadedEffect()
        {
            Effect? oldHot = _hotReloadedEffect;
            Volatile.Write(ref _hotReloadedEffect, null);

            if (ReferenceEquals(_boundEffect, oldHot))
                Volatile.Write(ref _boundEffect, null);

            if (oldHot is not null && !oldHot.IsDisposed)
                oldHot.Dispose();
        }
#endif

        private void BindCustomParameterSlots(Effect effect, string[] names)
        {
            var cache = names.Length > 0 ? new EffectParameter?[names.Length] : Array.Empty<EffectParameter?>();
            EffectParameterCollection parameters = effect.Parameters;

            for (int i = 0; i < names.Length; i++)
            {
                cache[i] = parameters[names[i]];
            }

            _parameterCache = cache;
            _boundParameterNames = names;
        }

        private void BindEffect(Effect effect, string[] parameterNames)
        {
            Volatile.Write(ref _boundEffect, effect);

            EffectParameterCollection parameters = effect.Parameters;

            // 1. 绑定编译期参数名数组到 O(1) 缓存槽
            BindCustomParameterSlots(effect, parameterNames);

            // 2. 直连绑定常用全局参数（未在 HLSL 中声明或被编译器裁剪的保持 null，空安全跳过）
            _timeParam = parameters[ShaderGlobalParamNames.Time];
            _transformMatrixParam = parameters[ShaderGlobalParamNames.TransformMatrix];
            _screenResolutionParam = parameters[ShaderGlobalParamNames.ScreenResolution];
            _zoomParam = parameters[ShaderGlobalParamNames.Zoom];

            // 3. 预缓存 Technique 与 Pass 数组，消灭绘制时的集合遍历与字符串查找
            EffectTechniqueCollection techCollection = effect.Techniques;
            int techCount = techCollection.Count;
            if (techCount == 0)
            {
                _techniques = Array.Empty<EffectTechnique>();
                _passes = Array.Empty<EffectPass[]>();
            }
            else
            {
                var techs = new EffectTechnique[techCount];
                var passTable = new EffectPass[techCount][];

                for (int t = 0; t < techCount; t++)
                {
                    EffectTechnique tech = techCollection[t];
                    techs[t] = tech;

                    EffectPassCollection passCollection = tech.Passes;
                    int passCount = passCollection.Count;
                    var passes = passCount > 0 ? new EffectPass[passCount] : Array.Empty<EffectPass>();
                    for (int p = 0; p < passCount; p++)
                    {
                        passes[p] = passCollection[p];
                    }

                    passTable[t] = passes;
                }

                _techniques = techs;
                _passes = passTable;
            }

            _lastSyncedRevision = 0;
            _lastSyncedUsedProjection = false;
            _pendingManualOverrides = GlobalOverrideFlags.None;
            _dirtyGlobalParams = GlobalOverrideFlags.None;
        }
    }
}
