#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Apotheosis.Runtime.Foundation.Collections;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Geometry
{
    /// <summary>统一网格注册与池化管理模块。</summary>
    public sealed class MeshModule
    {
        private const int InitialSlotCapacity = 256;
        private const int MaxPooledMeshes = 512;

        private enum RetirementKind : byte
        {
            ReturnDynamicPool = 0,
            ReturnInstancedPool = 1,
            UnregisterOnly = 2,
            UnregisterAndDispose = 3,
        }

        private readonly struct PendingRetirement
        {
            public readonly ushort Id;
            public readonly RetirementKind Kind;

            public PendingRetirement(ushort id, RetirementKind kind)
            {
                Id = id;
                Kind = kind;
            }
        }

        private readonly object _lock = new();
        private readonly UShortSlotTable<IMesh> _table = new(InitialSlotCapacity);
        private readonly BoundedObjectPool<DynamicMesh> _dynamicPool = new(32, MaxPooledMeshes);
        private readonly BoundedObjectPool<InstancedMesh> _instancedPool = new(16, MaxPooledMeshes);
        private readonly List<PendingRetirement> _pendingRetirements = new();

        private EpochCachedHandle<MeshHandle> _unitQuad2D;
        private EpochCachedHandle<MeshHandle> _unitCube3D;

        /// <summary>当前已注册且活跃的网格总数。</summary>
        public int ActiveCount => _table.ActiveCount;

        internal int Epoch => _table.Epoch;

        /// <summary>内置中心对齐单位 2D 四边形（<c>[-0.5..+0.5]</c>），供硬件实例化与精灵/SDF 复用。</summary>
        public MeshHandle UnitQuad2D => _unitQuad2D.GetOrCreate(_lock, this, static () => RenderManager.IsUnloading, static m => m._table.Epoch,
            static m => m.RegisterLocked(GeometryBuilder.CreateUnitQuad2D(), protectFromExternalUnregister: true));

        /// <summary>内置中心对齐单位 3D 立方体（<c>[-0.5..+0.5]^3</c>），供 3D 验证与基础实例化复用。</summary>
        public MeshHandle UnitCube3D => _unitCube3D.GetOrCreate(_lock, this, static () => RenderManager.IsUnloading, static m => m._table.Epoch,
            static m => m.RegisterLocked(GeometryBuilder.CreateUnitCube3D(), protectFromExternalUnregister: true));

        /// <summary>注册任意实现 <see cref="IMesh"/> 的自定义网格并返回 2 字节 <see cref="MeshHandle"/>（线程安全）。</summary>
        public MeshHandle Register(IMesh mesh)
        {
            ArgumentNullException.ThrowIfNull(mesh);
            lock (_lock)
                return RegisterLocked(mesh, protectFromExternalUnregister: false);
        }

        /// <summary>注册内部受保护网格（如 <c>DrawSprite</c> 永久复用槽位），防止外部误删。</summary>
        internal MeshHandle RegisterProtected(IMesh mesh)
        {
            ArgumentNullException.ThrowIfNull(mesh);
            lock (_lock)
                return RegisterLocked(mesh, protectFromExternalUnregister: true);
        }

        /// <summary>注册静态网格并返回句柄。</summary>
        public MeshHandle RegisterStatic(ReadOnlySpan<UnifiedVertex> vertices, ReadOnlySpan<int> indices, PrimitiveType primitiveType = PrimitiveType.TriangleList)
            => Register(new StaticMesh(vertices, indices, primitiveType));

        /// <summary>从对象池租借一个动态网格并注册返回句柄。</summary>
        public MeshHandle AcquireDynamic(int initialVertexCapacity = 64, int initialIndexCapacity = 192, PrimitiveType primitiveType = PrimitiveType.TriangleList)
        {
            lock (_lock)
            {
                DynamicMesh mesh;
                if (_dynamicPool.TryPop(out DynamicMesh? pooled))
                {
                    mesh = pooled;
                    mesh.ResetForPool();
                    mesh.PrimitiveType = primitiveType;
                }
                else
                {
                    mesh = new DynamicMesh(initialVertexCapacity, initialIndexCapacity, primitiveType);
                }

                return RegisterLocked(mesh, protectFromExternalUnregister: false);
            }
        }

        /// <summary>标记动态网格待归还对象池：延迟到帧末 <see cref="EndFrame"/> 安全回收复用。</summary>
        public void ReleaseDynamic(MeshHandle handle) => ScheduleRetirement(handle, RetirementKind.ReturnDynamicPool);

        /// <summary>从内置对象池租借一个单组 <see cref="InstancedMesh"/> 并注册获得 2 字节 <see cref="MeshHandle"/>。</summary>
        public MeshHandle AcquireInstanced(MeshHandle baseMesh, int initialCapacity = 128)
        {
            lock (_lock)
            {
                InstancedMesh mesh;
                if (_instancedPool.TryPop(out InstancedMesh? pooled))
                {
                    mesh = pooled;
                    mesh.ResetForPool(baseMesh);
                }
                else
                {
                    mesh = new InstancedMesh(baseMesh, initialCapacity);
                }

                return RegisterLocked(mesh, protectFromExternalUnregister: false);
            }
        }

        /// <summary>标记实例化网格待归还对象池：延迟到帧末 <see cref="EndFrame"/> 安全回收。</summary>
        public void ReleaseInstanced(MeshHandle handle) => ScheduleRetirement(handle, RetirementKind.ReturnInstancedPool);

        /// <summary>标记自定义网格待注销：延迟到帧末 <see cref="EndFrame"/> 回收槽位并在主线程安全释放 GPU 资源。</summary>
        public void Unregister(MeshHandle handle, bool disposeMesh = true)
            => ScheduleRetirement(handle, disposeMesh ? RetirementKind.UnregisterAndDispose : RetirementKind.UnregisterOnly);

        /// <summary>解除内部受保护标记并延迟至帧末注销释放（供管线内部池溢出回收）。</summary>
        internal void UnregisterProtected(MeshHandle handle, bool disposeMesh = true)
        {
            if (!handle.IsValid)
                return;

            lock (_lock)
            {
                _table.UnprotectLocked(handle.Id);
                if (_table.TryMarkPendingReleaseLocked(handle.Id))
                    _pendingRetirements.Add(new PendingRetirement(handle.Id, disposeMesh ? RetirementKind.UnregisterAndDispose : RetirementKind.UnregisterOnly));
            }
        }

        /// <summary>O(1) 无锁安全解析句柄对应的 <see cref="IMesh"/>。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(MeshHandle handle, [NotNullWhen(true)] out IMesh? mesh) => _table.TryGet(handle.Id, out mesh);

        /// <summary>按强类型获取已注册网格实例（如 <c>Get&lt;DynamicMesh&gt;(h)</c> 或 <c>Get&lt;TrailMesh&gt;(h)</c>）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TMesh Get<TMesh>(MeshHandle handle) where TMesh : class, IMesh
        {
            if (TryGet(handle, out IMesh? mesh) && mesh is TMesh typed)
                return typed;

            throw new InvalidOperationException($"句柄 {handle} 未找到或类型不匹配 {typeof(TMesh).Name}。");
        }

        /// <summary>便捷获取 <see cref="DynamicMesh"/>。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DynamicMesh GetDynamic(MeshHandle handle) => Get<DynamicMesh>(handle);

        /// <summary>每帧绘制全部结束后在主线程统一执行延迟回收与 GPU Buffer 释放，杜绝同帧 Use-After-Free / ABA。</summary>
        internal void EndFrame()
        {
            List<IMesh>? toDispose = null;

            lock (_lock)
            {
                if (_pendingRetirements.Count == 0)
                    return;

                for (int i = 0; i < _pendingRetirements.Count; i++)
                {
                    PendingRetirement item = _pendingRetirements[i];
                    IMesh? removed = _table.RemoveLocked(item.Id);
                    if (removed is null)
                        continue;

                    switch (item.Kind)
                    {
                        case RetirementKind.ReturnDynamicPool when removed is DynamicMesh dyn:
                            dyn.ResetForPool();
                            _dynamicPool.Return(dyn);
                            break;

                        case RetirementKind.ReturnInstancedPool when removed is InstancedMesh inst:
                            inst.ResetForPool(default);
                            _instancedPool.Return(inst);
                            break;

                        case RetirementKind.UnregisterAndDispose:
                        case RetirementKind.ReturnDynamicPool:
                        case RetirementKind.ReturnInstancedPool:
                            toDispose ??= new List<IMesh>();
                            toDispose.Add(removed);
                            break;
                    }
                }

                _pendingRetirements.Clear();
            }

            GpuResourceHelper.DisposeAllOnMainThread(toDispose);
        }

        internal void Unload()
        {
            List<IMesh>? toDispose = null;

            lock (_lock)
            {
                _table.ClearLocked(ref toDispose);
                _dynamicPool.Clear();
                _instancedPool.Clear();
                _pendingRetirements.Clear();
                _unitQuad2D.Invalidate();
                _unitCube3D.Invalidate();
            }

            GpuResourceHelper.DisposeAllOnMainThread(toDispose);
        }

        private void ScheduleRetirement(MeshHandle handle, RetirementKind kind)
        {
            if (!handle.IsValid)
                return;

            lock (_lock)
            {
                if (_table.TryMarkPendingReleaseLocked(handle.Id))
                    _pendingRetirements.Add(new PendingRetirement(handle.Id, kind));
            }
        }

        private MeshHandle RegisterLocked(IMesh mesh, bool protectFromExternalUnregister)
        {
            if (RenderManager.IsUnloading)
                return MeshHandle.Invalid;

            ushort id = _table.AllocateIdLocked($"网格数量已达 2 字节 MeshHandle 上限 ({ushort.MaxValue})。");
            _table.PublishLocked(id, mesh, protectFromExternalUnregister);
            return new MeshHandle(id);
        }
    }
}
