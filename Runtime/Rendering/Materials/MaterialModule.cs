#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Apotheosis.Runtime.Foundation.Collections;
using Apotheosis.Runtime.Rendering.Shaders;
using Apotheosis.Shaders;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Materials
{
    /// <summary>材质管理模块，负责材质句柄分配、缓存与生命周期回收。</summary>
    public sealed class MaterialModule
    {
        private const int InitialSlotCapacity = 256;
        private const int MaxPooledMaterials = 1024;
        private const int SpriteCachePruneIntervalFrames = 180;

        private readonly struct SpriteMaterialKey
        {
            public readonly Texture2D Texture;
            public readonly BlendState Blend;
            public readonly SamplerState Sampler;

            public SpriteMaterialKey(Texture2D texture, BlendState blend, SamplerState sampler)
            {
                Texture = texture;
                Blend = blend;
                Sampler = sampler;
            }
        }

        private sealed class SpriteMaterialKeyComparer : IEqualityComparer<SpriteMaterialKey>
        {
            public static readonly SpriteMaterialKeyComparer Instance = new();

            public bool Equals(SpriteMaterialKey x, SpriteMaterialKey y)
                => ReferenceEquals(x.Texture, y.Texture) && ReferenceEquals(x.Blend, y.Blend) && ReferenceEquals(x.Sampler, y.Sampler);

            public int GetHashCode(SpriteMaterialKey obj)
                => HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Texture), RuntimeHelpers.GetHashCode(obj.Blend), RuntimeHelpers.GetHashCode(obj.Sampler));
        }

        private readonly object _lock = new();
        private readonly UShortSlotTable<ManagedMaterial> _table = new(InitialSlotCapacity);
        private readonly BoundedObjectPool<ManagedMaterial> _materialPool = new(64, MaxPooledMaterials, static () => new ManagedMaterial());
        private readonly List<ushort> _transientIds = new();
        private readonly List<ushort> _pendingReleaseIds = new();
        private readonly Dictionary<SpriteMaterialKey, MaterialHandle> _spriteMaterialCache = new(SpriteMaterialKeyComparer.Instance);

        private int _pruneFrameCounter;

        private EpochCachedHandle<MaterialHandle> _defaultAlpha2D;
        private EpochCachedHandle<MaterialHandle> _defaultAdditive2D;
        private EpochCachedHandle<MaterialHandle> _defaultInstancedAlpha;
        private EpochCachedHandle<MaterialHandle> _defaultInstancedAdditive;

        /// <summary>当前活跃受管材质总数（含持久化与本帧瞬时材质）。</summary>
        public int ActiveCount => _table.ActiveCount;

        /// <summary>当前模块代际号（每次卸载后递增，供外部静态材质预设自动失效重建）。</summary>
        internal int Epoch => _table.Epoch;

        /// <summary>内置默认 2D Alpha 混合材质（基于 <c>DefaultBatch.fx / StandardTech</c>）。</summary>
        public MaterialHandle DefaultAlpha2D => _defaultAlpha2D.GetOrCreate(_lock, this, static () => RenderManager.IsUnloading, static m => m._table.Epoch,
            static m => m.CreateDefaultBatchLocked(Core_DefaultBatchShader.Technique_StandardTech, Core_DefaultBatchShader.Pass_StandardTech_DefaultPass, BlendState.AlphaBlend));

        /// <summary>内置默认 2D Additive 加法混合材质（基于 <c>DefaultBatch.fx / StandardTech</c>）。</summary>
        public MaterialHandle DefaultAdditive2D => _defaultAdditive2D.GetOrCreate(_lock, this, static () => RenderManager.IsUnloading, static m => m._table.Epoch,
            static m => m.CreateDefaultBatchLocked(Core_DefaultBatchShader.Technique_StandardTech, Core_DefaultBatchShader.Pass_StandardTech_DefaultPass, BlendState.Additive));

        /// <summary>内置默认硬件实例化 Alpha 混合材质（基于 <c>DefaultBatch.fx / InstancedTech</c>）。</summary>
        public MaterialHandle DefaultInstancedAlpha => _defaultInstancedAlpha.GetOrCreate(_lock, this, static () => RenderManager.IsUnloading, static m => m._table.Epoch,
            static m => m.CreateDefaultBatchLocked(Core_DefaultBatchShader.Technique_InstancedTech, Core_DefaultBatchShader.Pass_InstancedTech_DefaultPass, BlendState.AlphaBlend));

        /// <summary>内置默认硬件实例化 Additive 加法混合材质（基于 <c>DefaultBatch.fx / InstancedTech</c>）。</summary>
        public MaterialHandle DefaultInstancedAdditive => _defaultInstancedAdditive.GetOrCreate(_lock, this, static () => RenderManager.IsUnloading, static m => m._table.Epoch,
            static m => m.CreateDefaultBatchLocked(Core_DefaultBatchShader.Technique_InstancedTech, Core_DefaultBatchShader.Pass_InstancedTech_DefaultPass, BlendState.Additive));

        /// <summary>创建持久化材质并返回句柄。</summary>
        public MaterialHandle Create(ShaderHandle shader, int techniqueIndex = 0, int passIndex = 0,
            BlendState? blendState = null, DepthStencilState? depthStencilState = null,
            RasterizerState? rasterizerState = null, SamplerState? sampler0 = null, bool fallbackToMagicPixel = false)
        {
            lock (_lock)
                return CreateLocked(shader, techniqueIndex, passIndex, blendState, depthStencilState, rasterizerState, sampler0, fallbackToMagicPixel, protectFromExternalRelease: false);
        }

        internal MaterialHandle CreateProtected(ShaderHandle shader, int techniqueIndex = 0, int passIndex = 0,
            BlendState? blendState = null, DepthStencilState? depthStencilState = null,
            RasterizerState? rasterizerState = null, SamplerState? sampler0 = null, bool fallbackToMagicPixel = false)
        {
            lock (_lock)
                return CreateLocked(shader, techniqueIndex, passIndex, blendState, depthStencilState, rasterizerState, sampler0, fallbackToMagicPixel, protectFromExternalRelease: true);
        }

        /// <summary>获取或创建指定贴图与采样/混合状态对应的 2D 精灵材质。</summary>
        public MaterialHandle GetOrCreateSpriteMaterial(Texture2D texture, BlendState? blendState = null, SamplerState? samplerState = null)
        {
            ArgumentNullException.ThrowIfNull(texture);

            BlendState blend = blendState ?? BlendState.AlphaBlend;
            SamplerState sampler = samplerState ?? SamplerState.LinearClamp;
            var key = new SpriteMaterialKey(texture, blend, sampler);

            lock (_lock)
            {
                if (RenderManager.IsUnloading)
                    return MaterialHandle.Invalid;

                if (_spriteMaterialCache.TryGetValue(key, out MaterialHandle cached))
                    return cached;

                // 先完整配置贴图再发布到槽位表，避免并发读线程观察到未绑定贴图的半成品材质
                MaterialHandle handle = CreateLocked(Assets.Shaders.Core.DefaultBatch,
                    Core_DefaultBatchShader.Technique_StandardTech, Core_DefaultBatchShader.Pass_StandardTech_DefaultPass,
                    blend, DepthStencilState.None, RasterizerState.CullNone, sampler,
                    fallbackToMagicPixel: true, protectFromExternalRelease: true,
                    initialTextureSlot: Core_DefaultBatchShader.Slot_uImage0, initialTexture: texture);

                if (handle.IsValid)
                    _spriteMaterialCache[key] = handle;
                return handle;
            }
        }

        /// <summary>克隆一份单帧有效的瞬时材质副本，帧末自动回收。</summary>
        public MaterialHandle CloneTransient(MaterialHandle sourceHandle)
        {
            lock (_lock)
            {
                if (RenderManager.IsUnloading || !_table.TryGetLocked(sourceHandle.Id, out ManagedMaterial? source))
                    return MaterialHandle.Invalid;

                // 预先确保队列容量与对象实例就绪，使 PublishLocked 成为不可失败的最终提交点
                _transientIds.EnsureCapacity(_transientIds.Count + 1);
                ManagedMaterial clone = _materialPool.Rent();
                ushort id = 0;
                try
                {
                    id = _table.AllocateIdLocked($"材质数量已达 2 字节 MaterialHandle 上限 ({ushort.MaxValue})。");
                    var handle = new MaterialHandle(id);
                    clone.CopyFrom(source, handle);
                    _table.PublishLocked(id, clone, protect: false);
                    _transientIds.Add(id);
                    return handle;
                }
                catch
                {
                    clone.ClearForPool();
                    _materialPool.Return(clone);
                    if (id != 0)
                        _table.RecycleUnusedIdLocked(id);
                    throw;
                }
            }
        }

        /// <summary>释放持久化材质句柄（延迟到帧末安全回收）。</summary>
        public void Release(MaterialHandle handle)
        {
            if (!handle.IsValid)
                return;

            lock (_lock)
            {
                if (_table.TryMarkPendingReleaseLocked(handle.Id))
                    _pendingReleaseIds.Add(handle.Id);
            }
        }

        /// <summary>O(1) 无锁安全解析句柄对应的 <see cref="ManagedMaterial"/> 实例。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(MaterialHandle handle, [NotNullWhen(true)] out ManagedMaterial? material) => _table.TryGet(handle.Id, out material);

        /// <summary>直接获取句柄对应的材质实例；无效时抛出异常。</summary>
        public ManagedMaterial Get(MaterialHandle handle)
        {
            if (TryGet(handle, out ManagedMaterial? mat))
                return mat;

            throw new InvalidOperationException($"无效的 MaterialHandle: {handle}");
        }

        /// <summary>每帧绘制全部结束后在主线程回收瞬时材质、延迟释放队列，并定期清理已销毁贴图的精灵材质缓存。</summary>
        internal void EndFrame()
        {
            lock (_lock)
            {
                if (_transientIds.Count > 0)
                {
                    for (int i = 0; i < _transientIds.Count; i++)
                        RecycleSlotLocked(_transientIds[i]);
                    _transientIds.Clear();
                }

                if (_pendingReleaseIds.Count > 0)
                {
                    for (int i = 0; i < _pendingReleaseIds.Count; i++)
                        RecycleSlotLocked(_pendingReleaseIds[i]);
                    _pendingReleaseIds.Clear();
                }

                if (_spriteMaterialCache.Count > 0 && ++_pruneFrameCounter >= SpriteCachePruneIntervalFrames)
                {
                    _pruneFrameCounter = 0;
                    // .NET Core 3.0+ 保证遍历 Dictionary 期间直接 Remove 不使枚举器失效
                    foreach (KeyValuePair<SpriteMaterialKey, MaterialHandle> kvp in _spriteMaterialCache)
                    {
                        if (kvp.Key.Texture.IsDisposed && _spriteMaterialCache.Remove(kvp.Key))
                        {
                            _table.UnprotectLocked(kvp.Value.Id);
                            RecycleSlotLocked(kvp.Value.Id);
                        }
                    }
                }
            }
        }

        internal void Unload()
        {
            lock (_lock)
            {
                _table.ClearLocked();
                _transientIds.Clear();
                _pendingReleaseIds.Clear();
                _materialPool.Clear();
                _spriteMaterialCache.Clear();
                _pruneFrameCounter = 0;

                _defaultAlpha2D.Invalidate();
                _defaultAdditive2D.Invalidate();
                _defaultInstancedAlpha.Invalidate();
                _defaultInstancedAdditive.Invalidate();
            }
        }

        private MaterialHandle CreateDefaultBatchLocked(int techniqueIndex, int passIndex, BlendState blend)
            => CreateLocked(Assets.Shaders.Core.DefaultBatch, techniqueIndex, passIndex, blend,
                DepthStencilState.None, RasterizerState.CullNone, SamplerState.LinearClamp,
                fallbackToMagicPixel: true, protectFromExternalRelease: true);

        private MaterialHandle CreateLocked(ShaderHandle shader, int techniqueIndex, int passIndex,
            BlendState? blendState, DepthStencilState? depthStencilState, RasterizerState? rasterizerState, SamplerState? sampler0,
            bool fallbackToMagicPixel, bool protectFromExternalRelease, int initialTextureSlot = -1, Texture? initialTexture = null)
        {
            if (RenderManager.IsUnloading)
                return MaterialHandle.Invalid;

            ManagedMaterial mat = _materialPool.Rent();
            ushort id = 0;
            try
            {
                id = _table.AllocateIdLocked($"材质数量已达 2 字节 MaterialHandle 上限 ({ushort.MaxValue})。");
                var handle = new MaterialHandle(id);
                mat.Reset(handle, shader, techniqueIndex, passIndex, blendState, depthStencilState, rasterizerState, sampler0, fallbackToMagicPixel);

                if (initialTextureSlot >= 0 && initialTexture is not null)
                    mat.SetTexture(initialTextureSlot, initialTexture);

                _table.PublishLocked(id, mat, protectFromExternalRelease);
                return handle;
            }
            catch
            {
                mat.ClearForPool();
                _materialPool.Return(mat);
                if (id != 0)
                    _table.RecycleUnusedIdLocked(id);
                throw;
            }
        }

        private void RecycleSlotLocked(ushort id)
        {
            ManagedMaterial? mat = _table.RemoveLocked(id);
            if (mat is not null)
            {
                mat.ClearForPool();
                _materialPool.Return(mat);
            }
        }
    }
}
