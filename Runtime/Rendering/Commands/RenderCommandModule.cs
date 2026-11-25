#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Apotheosis.Runtime.Rendering.Geometry;
using Apotheosis.Runtime.Rendering.Materials;
using Apotheosis.Runtime.Rendering.States;
using Apotheosis.Runtime.Rendering.Targets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;

namespace Apotheosis.Runtime.Rendering.Commands
{
    /// <summary>渲染命令提交与合批调度模块。</summary>
    public sealed class RenderCommandModule
    {
        private const int InitialCommandCapacity = 256;
        private const int InitialBatchCapacity = 64;
        private const int InitialVertexCapacity = 1024;
        private const int InitialIndexCapacity = 4096;
        private const int InitialInstanceCapacity = 256;

        private sealed class CommandBucket
        {
            public readonly object Lock = new();
            public RenderCommand[] Commands = new RenderCommand[InitialCommandCapacity];
            public int Count;
        }

        private struct SpriteBucketSlot
        {
            public ulong BucketKey;
            public MeshHandle MeshHandle;
            public DynamicMesh Mesh;
        }

        private sealed class SpriteTimingBucket
        {
            public readonly Dictionary<ulong, int> ActiveMap = new();
            public readonly List<SpriteBucketSlot> ActiveSlots = new();
        }

        private readonly CommandBucket[] _buckets;

        // 按 RenderTiming 独立分桶的 2D 精灵复用池：Flush 时原子摘取当前批，彻底消除工作线程读写竞态与同帧漏绘
        private readonly object _spriteLock = new();
        private readonly SpriteTimingBucket[] _spriteBuckets;
        private readonly Stack<SpriteBucketSlot> _idleSpriteSlots = new();
        private readonly List<SpriteBucketSlot> _flushingSpriteSlots = new();

        // 主线程 Flush 专用 CPU 聚拢缓冲（按峰值常驻复用，稳态 0 GC）
        private RenderCommand[] _sortScratch = new RenderCommand[InitialCommandCapacity];
        private DrawBatch[] _batches = new DrawBatch[InitialBatchCapacity];
        private int _batchCount;
        private UnifiedVertex[] _uploadVerts = new UnifiedVertex[InitialVertexCapacity];
        private int _uploadVertCount;
        private int[] _uploadIndices = new int[InitialIndexCapacity];
        private int _uploadIndexCount;
        private InstanceData[] _uploadInstances = new InstanceData[InitialInstanceCapacity];
        private int _uploadInstanceCount;

        // 主线程共享 GPU 动态环形缓冲
        private DynamicVertexBuffer? _gpuVertexBuffer;
        private DynamicIndexBuffer? _gpuIndexBuffer;
        private DynamicVertexBuffer? _gpuInstanceBuffer;
        private readonly VertexBufferBinding[] _instancedBindings = new VertexBufferBinding[2];

        // 双缓冲统计：_currentStats 记录本帧累计，EndFrame 时翻转到 _lastFrameStats 供 ImGui 稳定显示
        private readonly RenderTimingStats[] _currentStats = new RenderTimingStats[RenderTimingConstants.Count];
        private readonly RenderTimingStats[] _lastFrameStats = new RenderTimingStats[RenderTimingConstants.Count];

        public RenderCommandModule()
        {
            _buckets = new CommandBucket[RenderTimingConstants.Count];
            _spriteBuckets = new SpriteTimingBucket[RenderTimingConstants.Count];
            for (int i = 0; i < RenderTimingConstants.Count; i++)
            {
                _buckets[i] = new CommandBucket();
                _spriteBuckets[i] = new SpriteTimingBucket();
            }
        }

        /// <summary>获取上一完整帧指定 <see cref="RenderTiming"/> 的渲染统计快照。</summary>
        public RenderTimingStats GetLastFrameStats(RenderTiming timing) => _lastFrameStats[(int)timing];

        /// <summary>获取上一完整帧所有阶段汇总后的总计统计。</summary>
        public RenderTimingStats GetLastFrameTotalStats()
        {
            int cmd = 0, dc = 0, merged = 0, verts = 0, prims = 0, inst = 0;
            for (int i = 0; i < _lastFrameStats.Length; i++)
            {
                ref readonly RenderTimingStats s = ref _lastFrameStats[i];
                cmd += s.CommandsSubmitted;
                dc += s.DrawCallsIssued;
                merged += s.BatchesMerged;
                verts += s.DynamicVertices;
                prims += s.PrimitivesDrawn;
                inst += s.InstancesDrawn;
            }

            return new RenderTimingStats(cmd, dc, merged, verts, prims, inst);
        }

        /// <summary>提交一条渲染命令。</summary>
        public void Submit(RenderTargetHandle target, MaterialHandle material, MeshHandle mesh, byte subLayer = 0, short sortOrder = 0)
        {
            if (Main.dedServ || RenderManager.IsUnloading || !material.IsValid || !mesh.IsValid)
                return;

            if (!RenderManager.Targets.TryGetTiming(target, out RenderTiming timing))
                return;

            RenderCommand cmd = RenderCommand.Pack(target, subLayer, sortOrder, material, mesh);
            EnqueueCommand(timing, cmd);
        }

        /// <summary>向内置渲染目标提交一条渲染命令。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Submit(BuiltInRenderTarget target, MaterialHandle material, MeshHandle mesh, byte subLayer = 0, short sortOrder = 0)
        {
            Submit(new RenderTargetHandle((byte)target), material, mesh, subLayer, sortOrder);
        }

        /// <summary>提交一个 2D 精灵四边形（自动按贴图与状态复用材质并合批）。</summary>
        public void DrawSprite(RenderTargetHandle target, Texture2D texture, Vector2 worldPosition, Color color,
            Rectangle? sourceRect = null, float rotation = 0f, Vector2 origin = default, Vector2? scale = null,
            BlendState? blendState = null, SamplerState? samplerState = null,
            byte subLayer = 0, short sortOrder = 0, Vector4 custom = default, float depthZ = 0f)
        {
            if (Main.dedServ || RenderManager.IsUnloading || texture is null || texture.IsDisposed)
                return;

            MaterialHandle material = RenderManager.Materials.GetOrCreateSpriteMaterial(texture, blendState, samplerState);
            DrawSpriteWithMaterial(target, material, texture, worldPosition, color, sourceRect, rotation, origin, scale, subLayer, sortOrder, custom, depthZ);
        }

        /// <summary>使用指定材质提交一个 2D 精灵四边形。</summary>
        public void DrawSpriteWithMaterial(RenderTargetHandle target, MaterialHandle material, Texture2D? referenceTextureForSize,
            Vector2 worldPosition, Color color, Rectangle? sourceRect = null, float rotation = 0f, Vector2 origin = default,
            Vector2? scale = null, byte subLayer = 0, short sortOrder = 0, Vector4 custom = default, float depthZ = 0f)
        {
            if (Main.dedServ || RenderManager.IsUnloading || !material.IsValid)
                return;

            if (!RenderManager.Targets.TryGetTiming(target, out RenderTiming timing))
                return;

            Vector2 sc = scale ?? Vector2.One;
            float baseW;
            float baseH;
            Vector4 uvRect;

            if (sourceRect.HasValue)
            {
                Rectangle r = sourceRect.Value;
                baseW = r.Width;
                baseH = r.Height;
                if (referenceTextureForSize is not null && referenceTextureForSize.Width > 0 && referenceTextureForSize.Height > 0)
                {
                    float invW = 1f / referenceTextureForSize.Width;
                    float invH = 1f / referenceTextureForSize.Height;
                    uvRect = new Vector4(r.X * invW, r.Y * invH, r.Width * invW, r.Height * invH);
                }
                else
                {
                    uvRect = InstanceData.FullUvRect;
                }
            }
            else if (referenceTextureForSize is not null)
            {
                baseW = referenceTextureForSize.Width;
                baseH = referenceTextureForSize.Height;
                uvRect = InstanceData.FullUvRect;
            }
            else
            {
                baseW = 1f;
                baseH = 1f;
                uvRect = InstanceData.FullUvRect;
            }

            Vector2 size = new(baseW * sc.X, baseH * sc.Y);
            Vector2 scaledOrigin = new(origin.X * sc.X, origin.Y * sc.Y);
            ulong bucketKey = RenderCommand.PackSpriteBucketKey(target, subLayer, sortOrder, material);

            lock (_spriteLock)
            {
                if (RenderManager.IsUnloading)
                    return;

                SpriteTimingBucket timingBucket = _spriteBuckets[(int)timing];
                if (!timingBucket.ActiveMap.TryGetValue(bucketKey, out int listIndex))
                {
                    SpriteBucketSlot slot = AcquireIdleSpriteSlotLocked(bucketKey);
                    listIndex = timingBucket.ActiveSlots.Count;
                    timingBucket.ActiveSlots.Add(slot);
                    timingBucket.ActiveMap[bucketKey] = listIndex;
                }

                timingBucket.ActiveSlots[listIndex].Mesh.AddSpriteQuad(
                    worldPosition,
                    size,
                    color,
                    rotation,
                    scaledOrigin,
                    uvRect,
                    custom,
                    depthZ);
            }
        }

        /// <summary>翻转帧统计并清理未消费命令桶与残留精灵槽。</summary>
        internal void EndFrame()
        {
            for (int i = 0; i < _currentStats.Length; i++)
            {
                _lastFrameStats[i] = _currentStats[i];
                _currentStats[i] = default;
            }

            for (int i = 0; i < _buckets.Length; i++)
            {
                CommandBucket bucket = _buckets[i];
                lock (bucket.Lock)
                {
                    bucket.Count = 0;
                }
            }

            lock (_spriteLock)
            {
                for (int i = 0; i < _spriteBuckets.Length; i++)
                {
                    SpriteTimingBucket sb = _spriteBuckets[i];
                    if (sb.ActiveSlots.Count == 0)
                        continue;

                    for (int j = 0; j < sb.ActiveSlots.Count; j++)
                    {
                        SpriteBucketSlot slot = sb.ActiveSlots[j];
                        slot.Mesh.Clear();
                        slot.BucketKey = 0;
                        _idleSpriteSlots.Push(slot);
                    }

                    sb.ActiveSlots.Clear();
                    sb.ActiveMap.Clear();
                }
            }
        }

        /// <summary>在主线程执行指定时机的命令排序、合批上传与绘制。</summary>
        internal void Flush(RenderTiming timing)
        {
            if (Main.dedServ || RenderManager.IsUnloading)
                return;

            // 1. 原子摘取普通命令桶
            CommandBucket bucket = _buckets[(int)timing];
            int count;
            lock (bucket.Lock)
            {
                count = bucket.Count;
                if (count > 0)
                {
                    EnsureSortScratchCapacity(count);
                    Array.Copy(bucket.Commands, _sortScratch, count);
                    bucket.Count = 0;
                }
            }

            // 2. 原子摘取本 timing 的 2D 精灵复用槽（后续工作线程写新槽位与本 Flush 彻底隔离，零竞态）
            _flushingSpriteSlots.Clear();
            lock (_spriteLock)
            {
                SpriteTimingBucket sb = _spriteBuckets[(int)timing];
                if (sb.ActiveSlots.Count > 0)
                {
                    _flushingSpriteSlots.AddRange(sb.ActiveSlots);
                    sb.ActiveSlots.Clear();
                    sb.ActiveMap.Clear();
                }
            }

            int spriteSlotCount = _flushingSpriteSlots.Count;
            if (count == 0 && spriteSlotCount == 0)
                return;

            try
            {
                if (spriteSlotCount > 0)
                {
                    int totalCount = count + spriteSlotCount;
                    EnsureSortScratchCapacity(totalCount);
                    for (int i = 0; i < spriteSlotCount; i++)
                    {
                        SpriteBucketSlot slot = _flushingSpriteSlots[i];
                        _sortScratch[count + i] = new RenderCommand(slot.BucketKey | slot.MeshHandle.Id);
                    }
                    count = totalCount;
                }

                GraphicsDevice? gd = Main.graphics?.GraphicsDevice;
                if (gd is null || gd.IsDisposed)
                    return;

                RenderManager.Shaders.AssertMainThread($"RenderCommandModule.Flush({timing})");

                // Pass 1: 原地排序 8B RenderCommand（纯 ulong[]，L2 Cache 友好）
                if (count > 1)
                    Array.Sort(_sortScratch, 0, count);

                // Pass 2: CPU 聚拢与合批
                _batchCount = 0;
                _uploadVertCount = 0;
                _uploadIndexCount = 0;
                _uploadInstanceCount = 0;
                int mergedCount = 0;

                GatherAndBatchCommands(gd, count, ref mergedCount);

                if (_batchCount == 0)
                    return;

                // Pass 3: 单次上传动态 GPU Buffer + 保护原版管线状态顺序执行全部 DrawBatch
                UploadDynamicBuffers(gd);

                int drawCalls = 0;
                int primitivesDrawn = 0;
                int instancesDrawn = 0;

                using (RenderStateScope.Capture(gd))
                {
                    ExecuteBatches(gd, ref drawCalls, ref primitivesDrawn, ref instancesDrawn);
                }

                ref RenderTimingStats cur = ref _currentStats[(int)timing];
                cur = new RenderTimingStats(
                    cur.CommandsSubmitted + count,
                    cur.DrawCallsIssued + drawCalls,
                    cur.BatchesMerged + mergedCount,
                    cur.DynamicVertices + _uploadVertCount,
                    cur.PrimitivesDrawn + primitivesDrawn,
                    cur.InstancesDrawn + instancesDrawn);
            }
            finally
            {
                // 无论成功或提前返回，均将摘取的 2D 精灵槽清空并归还空闲池（0 GC）
                if (_flushingSpriteSlots.Count > 0)
                {
                    lock (_spriteLock)
                    {
                        for (int i = 0; i < _flushingSpriteSlots.Count; i++)
                        {
                            SpriteBucketSlot slot = _flushingSpriteSlots[i];
                            slot.Mesh.Clear();
                            slot.BucketKey = 0;
                            if (!RenderManager.IsUnloading)
                                _idleSpriteSlots.Push(slot);
                        }
                    }

                    _flushingSpriteSlots.Clear();
                }
            }
        }

        internal void Unload()
        {
            for (int i = 0; i < _buckets.Length; i++)
            {
                CommandBucket bucket = _buckets[i];
                lock (bucket.Lock)
                {
                    bucket.Count = 0;
                }
            }

            lock (_spriteLock)
            {
                for (int i = 0; i < _spriteBuckets.Length; i++)
                {
                    _spriteBuckets[i].ActiveSlots.Clear();
                    _spriteBuckets[i].ActiveMap.Clear();
                }

                _idleSpriteSlots.Clear();
                _flushingSpriteSlots.Clear();
            }

            Array.Clear(_batches, 0, _batchCount);
            _batchCount = 0;
            _uploadVertCount = 0;
            _uploadIndexCount = 0;
            _uploadInstanceCount = 0;
            Array.Clear(_currentStats, 0, _currentStats.Length);
            Array.Clear(_lastFrameStats, 0, _lastFrameStats.Length);

            DynamicVertexBuffer? vb = _gpuVertexBuffer;
            DynamicIndexBuffer? ib = _gpuIndexBuffer;
            DynamicVertexBuffer? instVb = _gpuInstanceBuffer;
            _gpuVertexBuffer = null;
            _gpuIndexBuffer = null;
            _gpuInstanceBuffer = null;

            if (vb is not null || ib is not null || instVb is not null)
            {
                void DisposeGpuBuffers()
                {
                    try { vb?.Dispose(); } catch { }
                    try { ib?.Dispose(); } catch { }
                    try { instVb?.Dispose(); } catch { }
                }

                if (RenderManager.Shaders.IsOnMainThread)
                    DisposeGpuBuffers();
                else
                    Main.QueueMainThreadAction(DisposeGpuBuffers);
            }
        }

        // ==================== Pass 2: Gather & Batch ====================

        private void GatherAndBatchCommands(GraphicsDevice gd, int commandCount, ref int mergedCount)
        {
            MeshModule meshes = RenderManager.Meshes;
            MaterialModule materials = RenderManager.Materials;

            for (int i = 0; i < commandCount; i++)
            {
                RenderCommand cmd = _sortScratch[i];
                if (!meshes.TryGet(cmd.Mesh, out IMesh? mesh))
                    continue;

                if (!materials.TryGet(cmd.Material, out _))
                    continue;

                mesh.PrepareForDraw();
                if (mesh.IsEmpty)
                    continue;

                // 分支 A：硬件实例化网格（单组或多组 LOD/魔法阵元件）
                if (mesh is IInstancedMesh instanced)
                {
                    int groupCount = instanced.GroupCount;
                    for (int g = 0; g < groupCount; g++)
                    {
                        MeshHandle baseHandle = instanced.GetGroupBaseMesh(g);
                        ReadOnlySpan<InstanceData> instSpan = instanced.GetGroupInstances(g);
                        if (instSpan.IsEmpty || !baseHandle.IsValid)
                            continue;

                        if (!meshes.TryGet(baseHandle, out IMesh? baseObj) || baseObj is not IStaticGpuMesh baseStatic)
                            continue;

                        int basePrimCount = CalculatePrimitiveCount(baseStatic.PrimitiveType, baseStatic.IndexCount);
                        if (basePrimCount <= 0 || !baseStatic.EnsureGpuReady(gd))
                            continue;

                        int instOffset = AppendInstances(instSpan);

                        if (_batchCount > 0)
                        {
                            ref DrawBatch prev = ref _batches[_batchCount - 1];
                            if (prev.Kind == DrawBatchKind.Instanced
                                && prev.BatchKey == cmd.BatchKey
                                && prev.BaseMeshHandle == baseHandle
                                && prev.InstanceOffset + prev.InstanceCount == instOffset)
                            {
                                prev.InstanceCount += instSpan.Length;
                                mergedCount++;
                                continue;
                            }
                        }

                        ref DrawBatch batch = ref AddBatch();
                        batch.Kind = DrawBatchKind.Instanced;
                        batch.Target = cmd.Target;
                        batch.Material = cmd.Material;
                        batch.PrimitiveType = baseStatic.PrimitiveType;
                        batch.BatchKey = cmd.BatchKey;
                        batch.StaticMesh = baseStatic;
                        batch.BaseMeshHandle = baseHandle;
                        batch.PrimitiveCount = basePrimCount;
                        batch.InstanceOffset = instOffset;
                        batch.InstanceCount = instSpan.Length;
                        batch.HasWorldMatrix = false;
                    }

                    continue;
                }

                // 分支 B：常驻静态 GPU 网格（零每帧拷贝）
                if (mesh.IsStatic && mesh is IStaticGpuMesh staticMesh)
                {
                    int primCount = CalculatePrimitiveCount(staticMesh.PrimitiveType, staticMesh.IndexCount);
                    if (primCount <= 0 || !staticMesh.EnsureGpuReady(gd))
                        continue;

                    ref DrawBatch batch = ref AddBatch();
                    batch.Kind = DrawBatchKind.StaticIndexed;
                    batch.Target = cmd.Target;
                    batch.Material = cmd.Material;
                    batch.PrimitiveType = staticMesh.PrimitiveType;
                    batch.BatchKey = cmd.BatchKey;
                    batch.StaticMesh = staticMesh;
                    batch.BaseMeshHandle = cmd.Mesh;
                    batch.PrimitiveCount = primCount;
                    batch.HasWorldMatrix = staticMesh.TryGetWorldMatrix(out batch.WorldMatrix);
                    continue;
                }

                // 分支 C：动态网格（Trail / DynamicMesh / 2D 精灵批 / 自定义 IMesh）
                ReadOnlySpan<UnifiedVertex> verts = mesh.GetVertices();
                ReadOnlySpan<int> indices = mesh.GetIndices();
                if (verts.IsEmpty || indices.IsEmpty)
                    continue;

                PrimitiveType primType = mesh.PrimitiveType;
                int dynPrimCount = CalculatePrimitiveCount(primType, indices.Length);
                if (dynPrimCount <= 0)
                    continue;

                // 严格按有效图元截断尾索引（防止非 3/2 倍数索引污染后续合并批次）
                int usedIndexCount = primType switch
                {
                    PrimitiveType.TriangleList => dynPrimCount * 3,
                    PrimitiveType.LineList => dynPrimCount * 2,
                    _ => indices.Length,
                };

                int baseVertex = AppendDynamicVertices(verts, mesh);
                int startIndex = AppendRebasedIndices(indices.Slice(0, usedIndexCount), baseVertex);

                int indicesPerPrim = primType switch
                {
                    PrimitiveType.TriangleList => 3,
                    PrimitiveType.LineList => 2,
                    _ => 0,
                };

                // TriangleList 与 LineList 且索引严格连续时跨 Mesh 自动合批
                if (indicesPerPrim > 0 && _batchCount > 0)
                {
                    ref DrawBatch prev = ref _batches[_batchCount - 1];
                    if (prev.Kind == DrawBatchKind.DynamicIndexed
                        && prev.PrimitiveType == primType
                        && prev.BatchKey == cmd.BatchKey
                        && prev.StartIndex + prev.PrimitiveCount * indicesPerPrim == startIndex)
                    {
                        prev.PrimitiveCount += dynPrimCount;
                        mergedCount++;
                        continue;
                    }
                }

                ref DrawBatch dynBatch = ref AddBatch();
                dynBatch.Kind = DrawBatchKind.DynamicIndexed;
                dynBatch.Target = cmd.Target;
                dynBatch.Material = cmd.Material;
                dynBatch.PrimitiveType = primType;
                dynBatch.BatchKey = cmd.BatchKey;
                dynBatch.StartIndex = startIndex;
                dynBatch.PrimitiveCount = dynPrimCount;
                dynBatch.HasWorldMatrix = false;
            }
        }

        // ==================== Pass 3: Single Upload & Execute ====================

        private void UploadDynamicBuffers(GraphicsDevice gd)
        {
            if (_uploadVertCount > 0 && _uploadIndexCount > 0)
            {
                if (_gpuVertexBuffer is null || _gpuVertexBuffer.IsDisposed || _gpuVertexBuffer.VertexCount < _uploadVertCount)
                {
                    _gpuVertexBuffer?.Dispose();
                    int newCap = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(_uploadVertCount, InitialVertexCapacity));
                    _gpuVertexBuffer = new DynamicVertexBuffer(gd, UnifiedVertex.Declaration, newCap, BufferUsage.WriteOnly);
                }

                if (_gpuIndexBuffer is null || _gpuIndexBuffer.IsDisposed || _gpuIndexBuffer.IndexCount < _uploadIndexCount)
                {
                    _gpuIndexBuffer?.Dispose();
                    int newCap = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(_uploadIndexCount, InitialIndexCapacity));
                    _gpuIndexBuffer = new DynamicIndexBuffer(gd, IndexElementSize.ThirtyTwoBits, newCap, BufferUsage.WriteOnly);
                }

                _gpuVertexBuffer.SetData(_uploadVerts, 0, _uploadVertCount, SetDataOptions.Discard);
                _gpuIndexBuffer.SetData(_uploadIndices, 0, _uploadIndexCount, SetDataOptions.Discard);
            }

            if (_uploadInstanceCount > 0)
            {
                if (_gpuInstanceBuffer is null || _gpuInstanceBuffer.IsDisposed || _gpuInstanceBuffer.VertexCount < _uploadInstanceCount)
                {
                    _gpuInstanceBuffer?.Dispose();
                    int newCap = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(_uploadInstanceCount, InitialInstanceCapacity));
                    _gpuInstanceBuffer = new DynamicVertexBuffer(gd, InstanceData.Declaration, newCap, BufferUsage.WriteOnly);
                }

                _gpuInstanceBuffer.SetData(_uploadInstances, 0, _uploadInstanceCount, SetDataOptions.Discard);
            }
        }

        private void ExecuteBatches(GraphicsDevice gd, ref int drawCalls, ref int primitivesDrawn, ref int instancesDrawn)
        {
            RenderTargetModule targets = RenderManager.Targets;
            MaterialModule materials = RenderManager.Materials;

            RenderTargetScope offscreenScope = default;
            bool switchedOffscreen = false;
            int lastTargetId = -1;
            bool targetActive = false;

            MaterialHandle lastStateMaterial = default;
            bool lastHadWorldOverride = false;
            bool dynamicBuffersBound = false;

            try
            {
                for (int i = 0; i < _batchCount; i++)
                {
                    ref readonly DrawBatch batch = ref _batches[i];

                    // 1. 渲染目标切换（内置主屏目标 IsMainScreen==true 时零切换；无效或未就绪离屏目标严格跳过）
                    if (batch.Target.Id != lastTargetId)
                    {
                        bool prevSwitched = switchedOffscreen;
                        if (!targets.TryGet(batch.Target, out IRenderTarget? targetObj))
                        {
                            lastTargetId = batch.Target.Id;
                            targetActive = false;
                            continue;
                        }

                        if (!targetObj.IsMainScreen)
                        {
                            RenderTarget2D? resolvedRt = targetObj.Resolve(gd);
                            if (resolvedRt is null || resolvedRt.IsDisposed)
                            {
                                lastTargetId = batch.Target.Id;
                                targetActive = false;
                                continue;
                            }

                            if (!switchedOffscreen)
                            {
                                // 首次切入离屏时通过作用域栈保存原版绑定并切换目标
                                offscreenScope = targets.Push(gd, resolvedRt);
                                switchedOffscreen = true;
                            }
                            else
                            {
                                gd.SetRenderTarget(resolvedRt);
                                RenderManager.InvalidateViewContext();
                            }

                            targetObj.OnBound(gd);
                            lastStateMaterial = default;
                            lastHadWorldOverride = false;
                            lastTargetId = batch.Target.Id;
                            targetActive = true;
                        }
                        else
                        {
                            if (prevSwitched)
                            {
                                offscreenScope.Dispose();
                                offscreenScope = default;
                                switchedOffscreen = false;
                                lastStateMaterial = default;
                                lastHadWorldOverride = false;
                            }

                            lastTargetId = batch.Target.Id;
                            targetActive = true;
                        }
                    }

                    if (!targetActive)
                        continue;

                    // 2. 绑定材质（连续同材质带 WorldMatrix 覆写时免重复写管线状态与自定义 Uniform）
                    if (!materials.TryGet(batch.Material, out ManagedMaterial? mat))
                        continue;

                    bool materialChanged = batch.Material != lastStateMaterial;
                    bool needApply = materialChanged || batch.HasWorldMatrix || lastHadWorldOverride;
                    if (needApply)
                    {
                        // 始终走惰性缓存快路径，防止离屏切回主屏后读到过期上下文
                        ref readonly RenderFrameContext activeCtx = ref RenderManager.FrameContextRef;
                        if (!mat.ApplyToGpu(gd, in activeCtx, applyPipelineStates: materialChanged, applyUniforms: materialChanged, batch.HasWorldMatrix, in batch.WorldMatrix))
                            continue;

                        lastStateMaterial = batch.Material;
                        lastHadWorldOverride = batch.HasWorldMatrix;
                    }

                    // 3. 绑定几何缓冲并提交 DrawCall
                    switch (batch.Kind)
                    {
                        case DrawBatchKind.DynamicIndexed:
                        {
                            if (_gpuVertexBuffer is null || _gpuIndexBuffer is null)
                                continue;

                            if (!dynamicBuffersBound)
                            {
                                gd.SetVertexBuffer(_gpuVertexBuffer);
                                gd.Indices = _gpuIndexBuffer;
                                dynamicBuffersBound = true;
                            }

                            gd.DrawIndexedPrimitives(
                                batch.PrimitiveType,
                                baseVertex: 0,
                                minVertexIndex: 0,
                                numVertices: _uploadVertCount,
                                startIndex: batch.StartIndex,
                                primitiveCount: batch.PrimitiveCount);

                            drawCalls++;
                            primitivesDrawn += batch.PrimitiveCount;
                            break;
                        }

                        case DrawBatchKind.StaticIndexed:
                        {
                            IStaticGpuMesh? sm = batch.StaticMesh;
                            if (sm?.VertexBuffer is null || sm.IndexBuffer is null)
                                continue;

                            gd.SetVertexBuffer(sm.VertexBuffer);
                            gd.Indices = sm.IndexBuffer;
                            dynamicBuffersBound = false;

                            gd.DrawIndexedPrimitives(
                                batch.PrimitiveType,
                                baseVertex: 0,
                                minVertexIndex: 0,
                                numVertices: sm.VertexCount,
                                startIndex: 0,
                                primitiveCount: batch.PrimitiveCount);

                            drawCalls++;
                            primitivesDrawn += batch.PrimitiveCount;
                            break;
                        }

                        case DrawBatchKind.Instanced:
                        {
                            IStaticGpuMesh? baseSm = batch.StaticMesh;
                            if (baseSm?.VertexBuffer is null || baseSm.IndexBuffer is null || _gpuInstanceBuffer is null)
                                continue;

                            _instancedBindings[0] = new VertexBufferBinding(baseSm.VertexBuffer, vertexOffset: 0, instanceFrequency: 0);
                            _instancedBindings[1] = new VertexBufferBinding(_gpuInstanceBuffer, vertexOffset: batch.InstanceOffset, instanceFrequency: 1);
                            gd.SetVertexBuffers(_instancedBindings);
                            gd.Indices = baseSm.IndexBuffer;
                            dynamicBuffersBound = false;

                            gd.DrawInstancedPrimitives(
                                baseSm.PrimitiveType,
                                baseVertex: 0,
                                minVertexIndex: 0,
                                numVertices: baseSm.VertexCount,
                                startIndex: 0,
                                primitiveCount: batch.PrimitiveCount,
                                instanceCount: batch.InstanceCount);

                            drawCalls++;
                            primitivesDrawn += batch.PrimitiveCount * batch.InstanceCount;
                            instancesDrawn += batch.InstanceCount;
                            break;
                        }
                    }
                }
            }
            finally
            {
                // 解绑全部顶点流（含硬件实例化 Stream 1）与索引缓冲，杜绝污染后续原版绘制或缓冲扩容
                gd.SetVertexBuffer(null);
                gd.Indices = null;

                if (switchedOffscreen)
                    offscreenScope.Dispose();

                Array.Clear(_batches, 0, _batchCount);
                _batchCount = 0;
            }
        }

        // ==================== 内部辅助 ====================

        private void EnsureSortScratchCapacity(int required)
        {
            if (_sortScratch.Length < required)
                _sortScratch = new RenderCommand[Math.Max(required, _sortScratch.Length * 2)];
        }

        private void EnqueueCommand(RenderTiming timing, RenderCommand cmd)
        {
            CommandBucket bucket = _buckets[(int)timing];
            lock (bucket.Lock)
            {
                if (RenderManager.IsUnloading)
                    return;

                if (bucket.Count >= bucket.Commands.Length)
                    Array.Resize(ref bucket.Commands, bucket.Commands.Length * 2);

                bucket.Commands[bucket.Count++] = cmd;
            }
        }

        private SpriteBucketSlot AcquireIdleSpriteSlotLocked(ulong bucketKey)
        {
            if (_idleSpriteSlots.Count > 0)
            {
                SpriteBucketSlot recycled = _idleSpriteSlots.Pop();
                recycled.BucketKey = bucketKey;
                return recycled;
            }

            var mesh = new DynamicMesh(initialVertexCapacity: 64, initialIndexCapacity: 96, PrimitiveType.TriangleList);
            MeshHandle handle = RenderManager.Meshes.RegisterProtected(mesh);
            return new SpriteBucketSlot
            {
                BucketKey = bucketKey,
                MeshHandle = handle,
                Mesh = mesh,
            };
        }

        private ref DrawBatch AddBatch()
        {
            if (_batchCount >= _batches.Length)
                Array.Resize(ref _batches, _batches.Length * 2);

            ref DrawBatch b = ref _batches[_batchCount++];
            b = default;
            return ref b;
        }

        private int AppendDynamicVertices(ReadOnlySpan<UnifiedVertex> src, IMesh mesh)
        {
            int baseVertex = _uploadVertCount;
            int required = baseVertex + src.Length;
            if (required > _uploadVerts.Length)
                Array.Resize(ref _uploadVerts, Math.Max(required, _uploadVerts.Length * 2));

            if (mesh.TryGetWorldMatrix(out Matrix worldMatrix))
            {
                for (int i = 0; i < src.Length; i++)
                {
                    UnifiedVertex v = src[i];
                    v.Position = Vector3.Transform(v.Position, worldMatrix);
                    Vector3 n = Vector3.TransformNormal(v.Normal, worldMatrix);
                    float lenSq = n.LengthSquared();
                    v.Normal = lenSq > 1e-6f ? n / MathF.Sqrt(lenSq) : v.Normal;
                    _uploadVerts[baseVertex + i] = v;
                }
            }
            else
            {
                src.CopyTo(_uploadVerts.AsSpan(baseVertex, src.Length));
            }

            _uploadVertCount = required;
            return baseVertex;
        }

        private int AppendRebasedIndices(ReadOnlySpan<int> src, int baseVertex)
        {
            int startIndex = _uploadIndexCount;
            int required = startIndex + src.Length;
            if (required > _uploadIndices.Length)
                Array.Resize(ref _uploadIndices, Math.Max(required, _uploadIndices.Length * 2));

            if (baseVertex == 0)
            {
                src.CopyTo(_uploadIndices.AsSpan(startIndex, src.Length));
            }
            else
            {
                for (int i = 0; i < src.Length; i++)
                    _uploadIndices[startIndex + i] = src[i] + baseVertex;
            }

            _uploadIndexCount = required;
            return startIndex;
        }

        private int AppendInstances(ReadOnlySpan<InstanceData> src)
        {
            int offset = _uploadInstanceCount;
            int required = offset + src.Length;
            if (required > _uploadInstances.Length)
                Array.Resize(ref _uploadInstances, Math.Max(required, _uploadInstances.Length * 2));

            src.CopyTo(_uploadInstances.AsSpan(offset, src.Length));
            _uploadInstanceCount = required;
            return offset;
        }

        private static int CalculatePrimitiveCount(PrimitiveType type, int indexCount)
        {
            return type switch
            {
                PrimitiveType.TriangleList => indexCount / 3,
                PrimitiveType.TriangleStrip => indexCount >= 3 ? indexCount - 2 : 0,
                PrimitiveType.LineList => indexCount / 2,
                PrimitiveType.LineStrip => indexCount >= 2 ? indexCount - 1 : 0,
                _ => 0,
            };
        }
    }
}
