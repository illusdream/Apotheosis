#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Apotheosis.Runtime.Rendering.Shaders;
using Apotheosis.Shaders;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Materials
{
    /// <summary>材质管理模块，负责材质句柄分配、缓存与生命周期回收。</summary>
    public sealed class MaterialModule
    {
        private const int InitialSlotCapacity = 256;
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
                => ReferenceEquals(x.Texture, y.Texture)
                && ReferenceEquals(x.Blend, y.Blend)
                && ReferenceEquals(x.Sampler, y.Sampler);

            public int GetHashCode(SpriteMaterialKey obj)
                => HashCode.Combine(
                    RuntimeHelpers.GetHashCode(obj.Texture),
                    RuntimeHelpers.GetHashCode(obj.Blend),
                    RuntimeHelpers.GetHashCode(obj.Sampler));
        }

        private readonly object _lock = new();
        private ManagedMaterial?[] _slots = new ManagedMaterial?[InitialSlotCapacity];
        private readonly Stack<ushort> _freeIds = new();
        private readonly HashSet<ushort> _protectedIds = new();
        private readonly List<ushort> _transientIds = new();
        private readonly List<ushort> _pendingReleaseIds = new();
        private readonly HashSet<ushort> _pendingReleaseSet = new();
        private readonly Stack<ManagedMaterial> _materialPool = new();
        private readonly Dictionary<SpriteMaterialKey, MaterialHandle> _spriteMaterialCache = new(SpriteMaterialKeyComparer.Instance);
        private readonly List<SpriteMaterialKey> _pruneScratch = new();

        private int _highWaterMark; // 最大已分配 ID（1..65535）
        private int _activeCount;
        private int _pruneFrameCounter;

        // 使用 Volatile.Read/Write 保证无锁快速路径的内存可见性
        private ushort _defaultAlpha2DId;
        private ushort _defaultAdditive2DId;
        private ushort _defaultInstancedAlphaId;
        private ushort _defaultInstancedAdditiveId;

        /// <summary>当前活跃受管材质总数（含持久化与本帧瞬时材质）。</summary>
        public int ActiveCount => Volatile.Read(ref _activeCount);

        /// <summary>内置默认 2D Alpha 混合材质（基于 <c>DefaultBatch.fx / StandardTech</c>）。</summary>
        public MaterialHandle DefaultAlpha2D
        {
            get
            {
                ushort id = Volatile.Read(ref _defaultAlpha2DId);
                if (id != 0)
                    return new MaterialHandle(id);

                lock (_lock)
                {
                    if (_defaultAlpha2DId == 0)
                    {
                        MaterialHandle handle = CreateLocked(
                            Assets.Shaders.Core.DefaultBatch,
                            Core_DefaultBatchShader.Technique_StandardTech,
                            Core_DefaultBatchShader.Pass_StandardTech_DefaultPass,
                            BlendState.AlphaBlend,
                            DepthStencilState.None,
                            RasterizerState.CullNone,
                            SamplerState.LinearClamp,
                            fallbackToMagicPixel: true,
                            protectFromExternalRelease: true);
                        Volatile.Write(ref _defaultAlpha2DId, handle.Id);
                    }

                    return new MaterialHandle(_defaultAlpha2DId);
                }
            }
        }

        /// <summary>内置默认 2D Additive 加法混合材质（基于 <c>DefaultBatch.fx / StandardTech</c>）。</summary>
        public MaterialHandle DefaultAdditive2D
        {
            get
            {
                ushort id = Volatile.Read(ref _defaultAdditive2DId);
                if (id != 0)
                    return new MaterialHandle(id);

                lock (_lock)
                {
                    if (_defaultAdditive2DId == 0)
                    {
                        MaterialHandle handle = CreateLocked(
                            Assets.Shaders.Core.DefaultBatch,
                            Core_DefaultBatchShader.Technique_StandardTech,
                            Core_DefaultBatchShader.Pass_StandardTech_DefaultPass,
                            BlendState.Additive,
                            DepthStencilState.None,
                            RasterizerState.CullNone,
                            SamplerState.LinearClamp,
                            fallbackToMagicPixel: true,
                            protectFromExternalRelease: true);
                        Volatile.Write(ref _defaultAdditive2DId, handle.Id);
                    }

                    return new MaterialHandle(_defaultAdditive2DId);
                }
            }
        }

        /// <summary>内置默认硬件实例化 Alpha 混合材质（基于 <c>DefaultBatch.fx / InstancedTech</c>）。</summary>
        public MaterialHandle DefaultInstancedAlpha
        {
            get
            {
                ushort id = Volatile.Read(ref _defaultInstancedAlphaId);
                if (id != 0)
                    return new MaterialHandle(id);

                lock (_lock)
                {
                    if (_defaultInstancedAlphaId == 0)
                    {
                        MaterialHandle handle = CreateLocked(
                            Assets.Shaders.Core.DefaultBatch,
                            Core_DefaultBatchShader.Technique_InstancedTech,
                            Core_DefaultBatchShader.Pass_InstancedTech_DefaultPass,
                            BlendState.AlphaBlend,
                            DepthStencilState.None,
                            RasterizerState.CullNone,
                            SamplerState.LinearClamp,
                            fallbackToMagicPixel: true,
                            protectFromExternalRelease: true);
                        Volatile.Write(ref _defaultInstancedAlphaId, handle.Id);
                    }

                    return new MaterialHandle(_defaultInstancedAlphaId);
                }
            }
        }

        /// <summary>内置默认硬件实例化 Additive 加法混合材质（基于 <c>DefaultBatch.fx / InstancedTech</c>）。</summary>
        public MaterialHandle DefaultInstancedAdditive
        {
            get
            {
                ushort id = Volatile.Read(ref _defaultInstancedAdditiveId);
                if (id != 0)
                    return new MaterialHandle(id);

                lock (_lock)
                {
                    if (_defaultInstancedAdditiveId == 0)
                    {
                        MaterialHandle handle = CreateLocked(
                            Assets.Shaders.Core.DefaultBatch,
                            Core_DefaultBatchShader.Technique_InstancedTech,
                            Core_DefaultBatchShader.Pass_InstancedTech_DefaultPass,
                            BlendState.Additive,
                            DepthStencilState.None,
                            RasterizerState.CullNone,
                            SamplerState.LinearClamp,
                            fallbackToMagicPixel: true,
                            protectFromExternalRelease: true);
                        Volatile.Write(ref _defaultInstancedAdditiveId, handle.Id);
                    }

                    return new MaterialHandle(_defaultInstancedAdditiveId);
                }
            }
        }

        /// <summary>创建持久化材质并返回句柄。</summary>
        public MaterialHandle Create(ShaderHandle shader, int techniqueIndex = 0, int passIndex = 0,
            BlendState? blendState = null, DepthStencilState? depthStencilState = null,
            RasterizerState? rasterizerState = null, SamplerState? sampler0 = null, bool fallbackToMagicPixel = false)
        {
            lock (_lock)
            {
                return CreateLocked(shader, techniqueIndex, passIndex, blendState, depthStencilState, rasterizerState, sampler0, fallbackToMagicPixel, protectFromExternalRelease: false);
            }
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
                if (_spriteMaterialCache.TryGetValue(key, out MaterialHandle cached))
                    return cached;

                // 先完整配置贴图再发布到 _slots，避免并发读线程观察到未绑定贴图的半成品材质
                MaterialHandle handle = CreateLocked(
                    Assets.Shaders.Core.DefaultBatch,
                    Core_DefaultBatchShader.Technique_StandardTech,
                    Core_DefaultBatchShader.Pass_StandardTech_DefaultPass,
                    blend, DepthStencilState.None, RasterizerState.CullNone, sampler,
                    fallbackToMagicPixel: true, protectFromExternalRelease: true,
                    initialTextureSlot: Core_DefaultBatchShader.Slot_uImage0, initialTexture: texture);

                _spriteMaterialCache[key] = handle;
                return handle;
            }
        }

        /// <summary>克隆一份单帧有效的瞬时材质副本，帧末自动回收。</summary>
        public MaterialHandle CloneTransient(MaterialHandle sourceHandle)
        {
            lock (_lock)
            {
                if (!TryGetLocked(sourceHandle, out ManagedMaterial? source))
                    return MaterialHandle.Invalid;

                ushort id = AllocateIdLocked();
                var handle = new MaterialHandle(id);
                ManagedMaterial clone = _materialPool.Count > 0 ? _materialPool.Pop() : new ManagedMaterial();
                clone.CopyFrom(source, handle);

                Volatile.Write(ref _slots[id], clone);
                _transientIds.Add(id);
                Volatile.Write(ref _activeCount, _activeCount + 1);
                return handle;
            }
        }

        /// <summary>释放持久化材质句柄（延迟到帧末安全回收）。</summary>
        public void Release(MaterialHandle handle)
        {
            if (!handle.IsValid)
                return;

            lock (_lock)
            {
                ushort id = handle.Id;
                if (id >= _slots.Length || _slots[id] is null || _protectedIds.Contains(id))
                    return;

                if (_pendingReleaseSet.Add(id))
                    _pendingReleaseIds.Add(id);
            }
        }

        /// <summary>O(1) 无锁安全解析句柄对应的 <see cref="ManagedMaterial"/> 实例。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(MaterialHandle handle, [NotNullWhen(true)] out ManagedMaterial? material)
        {
            int id = handle.Id;
            if (id != 0)
            {
                ManagedMaterial?[] slots = Volatile.Read(ref _slots);
                if ((uint)id < (uint)slots.Length)
                {
                    material = Volatile.Read(ref slots[id]);
                    return material is not null;
                }
            }

            material = null;
            return false;
        }

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
                    _pendingReleaseSet.Clear();
                }

                if (_spriteMaterialCache.Count > 0 && ++_pruneFrameCounter >= SpriteCachePruneIntervalFrames)
                {
                    _pruneFrameCounter = 0;
                    _pruneScratch.Clear();
                    foreach (var kvp in _spriteMaterialCache)
                    {
                        if (kvp.Key.Texture.IsDisposed)
                            _pruneScratch.Add(kvp.Key);
                    }

                    for (int i = 0; i < _pruneScratch.Count; i++)
                    {
                        SpriteMaterialKey key = _pruneScratch[i];
                        if (_spriteMaterialCache.Remove(key, out MaterialHandle handle))
                        {
                            _protectedIds.Remove(handle.Id);
                            RecycleSlotLocked(handle.Id);
                        }
                    }

                    _pruneScratch.Clear();
                }
            }
        }

        internal void Unload()
        {
            lock (_lock)
            {
                Array.Clear(_slots, 0, _slots.Length);
                _freeIds.Clear();
                _protectedIds.Clear();
                _transientIds.Clear();
                _pendingReleaseIds.Clear();
                _pendingReleaseSet.Clear();
                _materialPool.Clear();
                _spriteMaterialCache.Clear();
                _pruneScratch.Clear();

                _highWaterMark = 0;
                _pruneFrameCounter = 0;
                Volatile.Write(ref _activeCount, 0);

                Volatile.Write(ref _defaultAlpha2DId, 0);
                Volatile.Write(ref _defaultAdditive2DId, 0);
                Volatile.Write(ref _defaultInstancedAlphaId, 0);
                Volatile.Write(ref _defaultInstancedAdditiveId, 0);
            }
        }

        private MaterialHandle CreateLocked(ShaderHandle shader, int techniqueIndex, int passIndex,
            BlendState? blendState, DepthStencilState? depthStencilState, RasterizerState? rasterizerState, SamplerState? sampler0,
            bool fallbackToMagicPixel, bool protectFromExternalRelease, int initialTextureSlot = -1, Texture? initialTexture = null)
        {
            ushort id = AllocateIdLocked();
            var handle = new MaterialHandle(id);
            ManagedMaterial mat = _materialPool.Count > 0 ? _materialPool.Pop() : new ManagedMaterial();
            mat.Reset(handle, shader, techniqueIndex, passIndex, blendState, depthStencilState, rasterizerState, sampler0, fallbackToMagicPixel);

            if (initialTextureSlot >= 0 && initialTexture is not null)
                mat.SetTexture(initialTextureSlot, initialTexture);

            if (protectFromExternalRelease)
                _protectedIds.Add(id);

            Volatile.Write(ref _slots[id], mat);
            Volatile.Write(ref _activeCount, _activeCount + 1);
            return handle;
        }

        private void RecycleSlotLocked(ushort id)
        {
            if (id == 0 || id >= _slots.Length)
                return;

            ManagedMaterial? mat = _slots[id];
            if (mat is null)
                return;

            Volatile.Write(ref _slots[id], null);
            _materialPool.Push(mat);
            _freeIds.Push(id);
            Volatile.Write(ref _activeCount, Math.Max(0, _activeCount - 1));
        }

        private ushort AllocateIdLocked()
        {
            if (_freeIds.Count > 0)
                return _freeIds.Pop();

            int next = _highWaterMark + 1;
            if (next > ushort.MaxValue)
                throw new InvalidOperationException($"材质数量已达 2 字节 MaterialHandle 上限 ({ushort.MaxValue})。");

            if (next >= _slots.Length)
            {
                int newLen = Math.Min(ushort.MaxValue + 1, _slots.Length * 2);
                var grown = new ManagedMaterial?[newLen];
                Array.Copy(_slots, grown, _slots.Length);
                Volatile.Write(ref _slots, grown);
            }

            _highWaterMark = next;
            return (ushort)next;
        }

        private bool TryGetLocked(MaterialHandle handle, [NotNullWhen(true)] out ManagedMaterial? mat)
        {
            int id = handle.Id;
            if (id != 0 && id < _slots.Length)
            {
                mat = _slots[id];
                return mat is not null;
            }

            mat = null;
            return false;
        }
    }
}
