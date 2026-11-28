#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Core;
using Apotheosis.Runtime.Rendering;
using Apotheosis.Runtime.Rendering.Geometry;
using Apotheosis.Runtime.Rendering.Materials;
using Apotheosis.Runtime.Rendering.Targets;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Systems
{
    /// <summary>以 Shader 为粒度的硬件实例化渲染系统基类：同一系统内无分支处理 2D/3D 双姿态、跨 Store 合批与三缓冲交付。</summary>
    public abstract class InstancedShaderRenderSystemBase<TRenderData> : IParticleRenderSystem
        where TRenderData : struct, IComponent
    {
        private const int PruneIntervalFrames = 300;
        private const int IdlePruneThresholdFrames = 600;
        private const int InitialInstanceBucketCapacity = 128;

        // 8 字节紧凑桶键（与 RenderCommand 位布局同宽：[Target(8b) | SubLayer(8b) | SortOrder(16b) | Material(16b) | BaseMesh(16b)]）
        [StructLayout(LayoutKind.Sequential, Size = 8)]
        private readonly struct RenderBucketKey : IEquatable<RenderBucketKey>
        {
            public readonly ulong Key;

            public RenderTargetHandle Target => new((byte)(Key >> 56));
            public byte SubLayer => (byte)(Key >> 48);
            public short SortOrder => (short)(((ushort)(Key >> 32)) ^ 0x8000);
            public MaterialHandle Material => new((ushort)(Key >> 16));
            public MeshHandle BaseMesh => new((ushort)Key);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public RenderBucketKey(RenderTargetHandle target, byte subLayer, short sortOrder, MaterialHandle material, MeshHandle baseMesh)
            {
                ulong biasedOrder = (ushort)(sortOrder ^ 0x8000);
                Key = ((ulong)target.Id << 56) | ((ulong)subLayer << 48) | (biasedOrder << 32) | ((ulong)material.Id << 16) | baseMesh.Id;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Equals(RenderBucketKey other) => Key == other.Key;

            public override bool Equals(object? obj) => obj is RenderBucketKey other && Key == other.Key;
            public override int GetHashCode() => Key.GetHashCode();
            public static bool operator ==(RenderBucketKey left, RenderBucketKey right) => left.Key == right.Key;
            public static bool operator !=(RenderBucketKey left, RenderBucketKey right) => left.Key != right.Key;
        }

        private sealed class BucketEntry
        {
            public readonly RenderTargetHandle Target;
            public readonly byte SubLayer;
            public readonly short SortOrder;
            public readonly MaterialHandle Material;
            public readonly MeshHandle MeshHandle;
            public readonly InstancedMesh Mesh;
            public bool ActiveThisFrame;
            public int LastActiveFrame;

            public BucketEntry(RenderBucketKey key, MeshHandle meshHandle, InstancedMesh mesh, int frameStamp)
            {
                Target = key.Target;
                SubLayer = key.SubLayer;
                SortOrder = key.SortOrder;
                Material = key.Material;
                MeshHandle = meshHandle;
                Mesh = mesh;
                LastActiveFrame = frameStamp;
            }
        }

        private sealed class FrameBucketSet
        {
            public readonly Dictionary<RenderBucketKey, BucketEntry> Buckets = new(16);
            public readonly List<BucketEntry> ActiveBuckets = new(16);
            private readonly List<RenderBucketKey> _pruneScratch = new();
            private int _meshEpoch = -1;
            private int _frameStamp;
            private int _pruneFrameCounter;

            public void ClearActiveForNewFrame()
            {
                int currentEpoch = RenderManager.Meshes.Epoch;
                if (_meshEpoch != currentEpoch)
                {
                    // MeshModule 发生重载/卸载切换代际：旧句柄已失效，直接丢弃容器引用避免误注销新代际句柄
                    Buckets.Clear();
                    ActiveBuckets.Clear();
                    _pruneScratch.Clear();
                    _meshEpoch = currentEpoch;
                    _frameStamp = 0;
                    _pruneFrameCounter = 0;
                    return;
                }

                _frameStamp++;

                for (int i = 0; i < ActiveBuckets.Count; i++)
                {
                    BucketEntry entry = ActiveBuckets[i];
                    entry.Mesh.Clear();
                    entry.ActiveThisFrame = false;
                }

                ActiveBuckets.Clear();

                if (Buckets.Count > 0 && ++_pruneFrameCounter >= PruneIntervalFrames)
                {
                    _pruneFrameCounter = 0;
                    PruneStaleBuckets();
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public InstancedMesh? GetOrCreateMesh(RenderBucketKey key)
            {
                if (!Buckets.TryGetValue(key, out BucketEntry? entry))
                {
                    if (RenderManager.IsUnloading)
                        return null;

                    var instanced = new InstancedMesh(key.BaseMesh, InitialInstanceBucketCapacity);
                    MeshHandle handle = RenderManager.Meshes.RegisterProtected(instanced);
                    if (!handle.IsValid)
                        return null;

                    entry = new BucketEntry(key, handle, instanced, _frameStamp);
                    Buckets[key] = entry;
                }

                if (!entry.ActiveThisFrame)
                {
                    entry.ActiveThisFrame = true;
                    entry.LastActiveFrame = _frameStamp;
                    ActiveBuckets.Add(entry);
                }

                return entry.Mesh;
            }

            public void DisposeAllMeshes()
            {
                // 严禁在后台卸载线程调用 entry.Mesh.Clear()（可能与主线程同帧在途 Flush 并发读 ValueBuffer）；
                // 仅通过 UnregisterProtected 延迟至帧末安全回收槽位，原 InstancedMesh 随后自然交由 GC 回收
                bool sameEpoch = RenderManager.Meshes.Epoch == _meshEpoch;
                if (sameEpoch)
                {
                    foreach (BucketEntry entry in Buckets.Values)
                    {
                        if (entry.MeshHandle.IsValid)
                            RenderManager.Meshes.UnregisterProtected(entry.MeshHandle, disposeMesh: false);
                    }
                }

                Buckets.Clear();
                ActiveBuckets.Clear();
                _pruneScratch.Clear();
                _meshEpoch = -1;
                _frameStamp = 0;
                _pruneFrameCounter = 0;
            }

            private void PruneStaleBuckets()
            {
                // 定期清理目标/材质/基础网格已失效或长期闲置的桶，防止长跑游玩耗尽 2 字节 MeshHandle 上限
                _pruneScratch.Clear();
                foreach (KeyValuePair<RenderBucketKey, BucketEntry> kvp in Buckets)
                {
                    RenderBucketKey key = kvp.Key;
                    BucketEntry entry = kvp.Value;

                    bool idleExpired = (_frameStamp - entry.LastActiveFrame) >= IdlePruneThresholdFrames;
                    bool stale = idleExpired
                        || !RenderManager.Targets.TryGet(key.Target, out _)
                        || !RenderManager.Materials.TryGet(key.Material, out _)
                        || !RenderManager.Meshes.TryGet(key.BaseMesh, out _);

                    if (stale)
                        _pruneScratch.Add(key);
                }

                for (int i = 0; i < _pruneScratch.Count; i++)
                {
                    RenderBucketKey staleKey = _pruneScratch[i];
                    if (Buckets.Remove(staleKey, out BucketEntry? removed) && removed.MeshHandle.IsValid)
                        RenderManager.Meshes.UnregisterProtected(removed.MeshHandle, disposeMesh: false);
                }

                _pruneScratch.Clear();
            }
        }

        // 按泛型特化类型 TRenderData 静态分配查询槽位，避免重复实例化泄露全局 ParticleQuerySlot 编号
        private static readonly ParticleQuerySlot Query2DSlot = ParticleQuerySlot.Allocate();
        private static readonly ParticleQuerySlot Query3DSlot = ParticleQuerySlot.Allocate();

        private readonly ParticleTripleBuffer<FrameBucketSet> _tripleBuffer = new(static () => new FrameBucketSet());
        private FrameBucketSet? _currentWriteSet;

        /// <summary>每帧开始打包前在工作线程触发的可选钩子（可用于每帧单次缓存基础网格句柄）。</summary>
        protected virtual void OnBeginPack() { }

        /// <summary>为单个粒子解析基础静态网格句柄（例如默认 2D 面片返回 <c>RenderManager.Meshes.UnitQuad2D</c>）。</summary>
        protected abstract MeshHandle ResolveBaseMesh(in ParticleRenderBase render, in TRenderData shaderData);

        /// <summary>为单个粒子打包传入 Shader 的 <see cref="InstanceData.Custom"/> 四通道自定义数据。</summary>
        protected abstract Vector4 PackCustomData(in ParticleRenderBase render, in TRenderData shaderData);

        public void BeginPack()
        {
            OnBeginPack();
            FrameBucketSet writeSet = _tripleBuffer.WriteSlot;
            writeSet.ClearActiveForNewFrame();
            _currentWriteSet = writeSet;
        }

        public void PackStore(ParticleStoreSlot slot, in ParticleRenderContext ctx)
        {
            FrameBucketSet? writeSet = _currentWriteSet;
            if (writeSet is null || slot.EntityCount == 0 || RenderManager.IsUnloading)
                return;

            Pack2DQuery(slot, writeSet, in ctx);
            Pack3DQuery(slot, writeSet, in ctx);
        }

        public void EndPack()
        {
            if (_currentWriteSet is null)
                return;

            _currentWriteSet = null;
            _tripleBuffer.PublishWrite();
        }

        /// <summary>异常中止本次打包而不发布半成品快照，保留主线程上一完整帧。</summary>
        public void AbortPack() => _currentWriteSet = null;

        public void PublishClearedSnapshot()
        {
            _currentWriteSet = null;
            _tripleBuffer.PublishClearedWrite(static set => set.ClearActiveForNewFrame());
        }

        public void SubmitOnMainThread()
        {
            if (RenderManager.IsUnloading)
                return;

            FrameBucketSet readSet = _tripleBuffer.AcquireRead(out _);
            List<BucketEntry> active = readSet.ActiveBuckets;
            int count = active.Count;
            if (count == 0)
                return;

            var commands = RenderManager.Commands;
            for (int i = 0; i < count; i++)
            {
                BucketEntry entry = active[i];
                if (!entry.Mesh.IsEmpty)
                    commands.Submit(entry.Target, entry.Material, entry.MeshHandle, entry.SubLayer, entry.SortOrder);
            }
        }

        public void DisposeResources()
        {
            _currentWriteSet = null;
            _tripleBuffer.ForEachSlot(static set => set.DisposeAllMeshes());
        }

        // ==================== 2D 快路径打包 ====================

        private void Pack2DQuery(ParticleStoreSlot slot, FrameBucketSet writeSet, in ParticleRenderContext ctx)
        {
            var query = slot.GetOrCreateQuery(Query2DSlot, static s => s.Query<ParticleSpatial, ParticlePose2D, ParticleRenderBase, TRenderData>());
            bool isLocal = ctx.IsLocalSpace;
            bool hasTargetOverride = ctx.TargetOverride.HasValue;
            RenderTargetHandle overrideTarget = hasTargetOverride ? ctx.TargetOverride!.Value : default;

            bool hasLastBucket = false;
            RenderBucketKey lastKey = default;
            InstancedMesh? lastMesh = null;

            foreach (var (spatialChunk, poseChunk, renderChunk, dataChunk, _) in query.Chunks)
            {
                ReadOnlySpan<ParticleSpatial> spatials = spatialChunk.Span;
                ReadOnlySpan<ParticlePose2D> poses = poseChunk.Span;
                ReadOnlySpan<ParticleRenderBase> renders = renderChunk.Span;
                ReadOnlySpan<TRenderData> datas = dataChunk.Span;

                for (int i = 0; i < spatials.Length; i++)
                {
                    ref readonly ParticleRenderBase render = ref renders[i];
                    // 仅当四通道均非正时剔除，避免误杀预乘 Alpha 纯加法发光粒子（RGB > 0, A == 0）
                    if (!render.Material.IsValid || IsInvisibleColor(render.Color))
                        continue;

                    ref readonly ParticlePose2D pose = ref poses[i];
                    float sx = render.BaseSize.X * pose.Scale.X;
                    float sy = render.BaseSize.Y * pose.Scale.Y;
                    if (!(MathF.Abs(sx) > 1e-5f) || !(MathF.Abs(sy) > 1e-5f))
                        continue;

                    ref readonly TRenderData data = ref datas[i];
                    MeshHandle baseMesh = ResolveBaseMesh(in render, in data);
                    if (!baseMesh.IsValid)
                        continue;

                    RenderTargetHandle target = hasTargetOverride ? overrideTarget : render.Target;
                    var key = new RenderBucketKey(target, render.SubLayer, render.SortOrder, render.Material, baseMesh);

                    InstancedMesh? bucketMesh;
                    if (hasLastBucket && key == lastKey)
                    {
                        bucketMesh = lastMesh;
                    }
                    else
                    {
                        bucketMesh = writeSet.GetOrCreateMesh(key);
                        lastKey = key;
                        lastMesh = bucketMesh;
                        hasLastBucket = bucketMesh is not null;
                    }

                    if (bucketMesh is null)
                        continue;

                    Vector3 pos = spatials[i].Position;
                    (float sin, float cos) = MathF.SinCos(pose.Rotation);
                    float m11 = sx * cos;
                    float m12 = sx * sin;
                    float m21 = -sy * sin;
                    float m22 = sy * cos;

                    float posX = pos.X;
                    float posY = pos.Y;
                    if (render.Pivot.X != 0f || render.Pivot.Y != 0f)
                    {
                        posX -= m11 * render.Pivot.X + m21 * render.Pivot.Y;
                        posY -= m12 * render.Pivot.X + m22 * render.Pivot.Y;
                    }

                    Vector4 custom = PackCustomData(in render, in data);
                    InstanceData inst;
                    if (!isLocal)
                    {
                        inst = new InstanceData
                        {
                            TransformCol0 = new Vector4(m11, m21, 0f, posX),
                            TransformCol1 = new Vector4(m12, m22, 0f, posY),
                            TransformCol2 = new Vector4(0f, 0f, 1f, pos.Z),
                            Color = render.Color,
                            Custom = custom,
                        };
                    }
                    else
                    {
                        var localMat = new Matrix(
                            m11, m12, 0f, 0f,
                            m21, m22, 0f, 0f,
                            0f, 0f, 1f, 0f,
                            posX, posY, pos.Z, 1f);
                        Matrix worldMat = localMat * ctx.OriginWorldMatrix;
                        inst = PackInstanceFromMatrix(in worldMat, render.Color, custom);
                    }

                    bucketMesh.Add(in inst);
                }
            }
        }

        // ==================== 3D 完整路径打包（翻滚 / CameraFacing Billboard / 伪透视） ====================

        private void Pack3DQuery(ParticleStoreSlot slot, FrameBucketSet writeSet, in ParticleRenderContext ctx)
        {
            var query = slot.GetOrCreateQuery(Query3DSlot, static s => s.Query<ParticleSpatial, ParticlePose3D, ParticleRenderBase, TRenderData>());
            bool isLocal = ctx.IsLocalSpace;
            bool hasTargetOverride = ctx.TargetOverride.HasValue;
            RenderTargetHandle overrideTarget = hasTargetOverride ? ctx.TargetOverride!.Value : default;

            // 懒计算局部原点无符号缩放幅度（仅首次遇到 CameraFacing 时求值，刻意取无符号模长避免宿主水平翻转镜像反转 Billboard）
            bool originScaleComputed = !isLocal;
            float originScaleX = 1f;
            float originScaleY = 1f;

            bool hasLastBucket = false;
            RenderBucketKey lastKey = default;
            InstancedMesh? lastMesh = null;

            foreach (var (spatialChunk, poseChunk, renderChunk, dataChunk, _) in query.Chunks)
            {
                ReadOnlySpan<ParticleSpatial> spatials = spatialChunk.Span;
                ReadOnlySpan<ParticlePose3D> poses = poseChunk.Span;
                ReadOnlySpan<ParticleRenderBase> renders = renderChunk.Span;
                ReadOnlySpan<TRenderData> datas = dataChunk.Span;

                for (int i = 0; i < spatials.Length; i++)
                {
                    ref readonly ParticleRenderBase render = ref renders[i];
                    if (!render.Material.IsValid || IsInvisibleColor(render.Color))
                        continue;

                    ref readonly ParticlePose3D pose = ref poses[i];
                    float sx = render.BaseSize.X * pose.Scale.X;
                    float sy = render.BaseSize.Y * pose.Scale.Y;
                    if (!(MathF.Abs(sx) > 1e-5f) || !(MathF.Abs(sy) > 1e-5f))
                        continue;

                    ref readonly TRenderData data = ref datas[i];
                    MeshHandle baseMesh = ResolveBaseMesh(in render, in data);
                    if (!baseMesh.IsValid)
                        continue;

                    Vector3 pos = spatials[i].Position;
                    Quaternion q = NormalizeSafe(pose.Rotation);
                    InstanceData inst;

                    if (pose.Billboard == ParticleBillboardMode.CameraFacing)
                    {
                        if (!originScaleComputed)
                        {
                            ref readonly Matrix om = ref ctx.OriginWorldMatrix;
                            originScaleX = new Vector3(om.M11, om.M12, om.M13).Length();
                            originScaleY = new Vector3(om.M21, om.M22, om.M23).Length();
                            originScaleComputed = true;
                        }

                        float bsx = sx * originScaleX;
                        float bsy = sy * originScaleY;
                        if (!(MathF.Abs(bsx) > 1e-5f) || !(MathF.Abs(bsy) > 1e-5f))
                            continue;

                        Vector3 worldPos = isLocal ? Vector3.Transform(pos, ctx.OriginWorldMatrix) : pos;
                        if (pose.PerspectiveFactor > 0f)
                        {
                            float persp = ComputePerspectiveScale(worldPos.Z, pose.PerspectiveFactor);
                            bsx *= persp;
                            bsy *= persp;
                        }

                        // Z 轴 Swing-Twist 分解提取滚转角：无 yawY = ±90° 万向节死锁，且纯 Z 旋转与欧拉 Z 完全等价
                        float zwLenSq = q.Z * q.Z + q.W * q.W;
                        float rollZ = zwLenSq > 1e-12f ? 2f * MathF.Atan2(q.Z, q.W) : 0f;
                        (float sin, float cos) = MathF.SinCos(rollZ);
                        float m11 = bsx * cos;
                        float m12 = bsx * sin;
                        float m21 = -bsy * sin;
                        float m22 = bsy * cos;

                        float posX = worldPos.X;
                        float posY = worldPos.Y;
                        if (render.Pivot.X != 0f || render.Pivot.Y != 0f)
                        {
                            posX -= m11 * render.Pivot.X + m21 * render.Pivot.Y;
                            posY -= m12 * render.Pivot.X + m22 * render.Pivot.Y;
                        }

                        inst = new InstanceData
                        {
                            TransformCol0 = new Vector4(m11, m21, 0f, posX),
                            TransformCol1 = new Vector4(m12, m22, 0f, posY),
                            TransformCol2 = new Vector4(0f, 0f, 1f, worldPos.Z),
                            Color = render.Color,
                            Custom = PackCustomData(in render, in data),
                        };
                    }
                    else
                    {
                        float psx = sx;
                        float psy = sy;
                        float psz = pose.Scale.Z;
                        if (pose.PerspectiveFactor > 0f)
                        {
                            // 先按粒子世界中心深度计算伪透视并缩放局部尺寸，保证 Pivot 偏移与面片同步等比缩放无漂移
                            float worldZ = isLocal
                                ? pos.X * ctx.OriginWorldMatrix.M13 + pos.Y * ctx.OriginWorldMatrix.M23 + pos.Z * ctx.OriginWorldMatrix.M33 + ctx.OriginWorldMatrix.M43
                                : pos.Z;
                            float persp = ComputePerspectiveScale(worldZ, pose.PerspectiveFactor);
                            psx *= persp;
                            psy *= persp;
                            psz *= persp;
                        }

                        // 直接由单位四元数展开 XNA 行向量 S * R 基向量，省去热循环内中间 4x4 矩阵构造与乘法
                        float xx = q.X * q.X, yy = q.Y * q.Y, zz = q.Z * q.Z;
                        float xy = q.X * q.Y, xz = q.X * q.Z, yz = q.Y * q.Z;
                        float wx = q.W * q.X, wy = q.W * q.Y, wz = q.W * q.Z;

                        float m11 = (1f - 2f * (yy + zz)) * psx, m12 = (2f * (xy + wz)) * psx, m13 = (2f * (xz - wy)) * psx;
                        float m21 = (2f * (xy - wz)) * psy, m22 = (1f - 2f * (xx + zz)) * psy, m23 = (2f * (yz + wx)) * psy;
                        float m31 = (2f * (xz + wy)) * psz, m32 = (2f * (yz - wx)) * psz, m33 = (1f - 2f * (xx + yy)) * psz;

                        float tx = pos.X, ty = pos.Y, tz = pos.Z;
                        if (render.Pivot.X != 0f || render.Pivot.Y != 0f)
                        {
                            tx -= m11 * render.Pivot.X + m21 * render.Pivot.Y;
                            ty -= m12 * render.Pivot.X + m22 * render.Pivot.Y;
                            tz -= m13 * render.Pivot.X + m23 * render.Pivot.Y;
                        }

                        Vector4 custom = PackCustomData(in render, in data);
                        if (!isLocal)
                        {
                            inst = new InstanceData
                            {
                                TransformCol0 = new Vector4(m11, m21, m31, tx),
                                TransformCol1 = new Vector4(m12, m22, m32, ty),
                                TransformCol2 = new Vector4(m13, m23, m33, tz),
                                Color = render.Color,
                                Custom = custom,
                            };
                        }
                        else
                        {
                            var localMat = new Matrix(
                                m11, m12, m13, 0f,
                                m21, m22, m23, 0f,
                                m31, m32, m33, 0f,
                                tx, ty, tz, 1f);
                            Matrix worldMat = localMat * ctx.OriginWorldMatrix;
                            inst = PackInstanceFromMatrix(in worldMat, render.Color, custom);
                        }
                    }

                    RenderTargetHandle target = hasTargetOverride ? overrideTarget : render.Target;
                    var key = new RenderBucketKey(target, render.SubLayer, render.SortOrder, render.Material, baseMesh);

                    InstancedMesh? bucketMesh;
                    if (hasLastBucket && key == lastKey)
                    {
                        bucketMesh = lastMesh;
                    }
                    else
                    {
                        bucketMesh = writeSet.GetOrCreateMesh(key);
                        lastKey = key;
                        lastMesh = bucketMesh;
                        hasLastBucket = bucketMesh is not null;
                    }

                    bucketMesh?.Add(in inst);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Quaternion NormalizeSafe(Quaternion q)
        {
            float lenSq = q.LengthSquared();
            if (!(lenSq > 1e-12f) || !float.IsFinite(lenSq))
                return Quaternion.Identity;

            return MathF.Abs(lenSq - 1f) <= 1e-5f ? q : q * (1f / MathF.Sqrt(lenSq));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsInvisibleColor(Vector4 color)
            => !(color.W > 0f) && !(color.X > 0f) && !(color.Y > 0f) && !(color.Z > 0f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ComputePerspectiveScale(float worldDepthZ, float perspectiveFactor)
        {
            // +Z 指向屏幕内：Z > 0 变远缩小，Z < 0 变近放大；拦截 NaN 且分母下限 0.1 防奇异倒翻
            float denom = 1f + worldDepthZ * perspectiveFactor * 0.001f;
            if (!float.IsFinite(denom))
                return 1f;
            return denom > 0.1f ? 1f / denom : 10f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static InstanceData PackInstanceFromMatrix(in Matrix m, Vector4 color, Vector4 custom) => new()
        {
            TransformCol0 = new Vector4(m.M11, m.M21, m.M31, m.M41),
            TransformCol1 = new Vector4(m.M12, m.M22, m.M32, m.M42),
            TransformCol2 = new Vector4(m.M13, m.M23, m.M33, m.M43),
            Color = color,
            Custom = custom,
        };
    }
}
