#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Xna.Framework.Graphics;
using Terraria;

namespace Apotheosis.Runtime.Rendering.Geometry
{
    /// <summary>统一网格注册与池化管理模块。</summary>
    public sealed class MeshModule
    {
        private const int InitialSlotCapacity = 256;

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
        private IMesh?[] _slots = new IMesh?[InitialSlotCapacity];
        private readonly Stack<ushort> _freeIds = new();
        private readonly HashSet<ushort> _protectedIds = new();
        private readonly Stack<DynamicMesh> _dynamicPool = new();
        private readonly Stack<InstancedMesh> _instancedPool = new();
        private readonly List<PendingRetirement> _pendingRetirements = new();
        private readonly HashSet<ushort> _pendingRetirementSet = new();

        private int _highWaterMark;
        private int _activeCount;

        private ushort _unitQuad2DId;
        private ushort _unitCube3DId;

        /// <summary>当前已注册且活跃的网格总数。</summary>
        public int ActiveCount => Volatile.Read(ref _activeCount);

        /// <summary>内置中心对齐单位 2D 四边形（<c>[-0.5..+0.5]</c>），供硬件实例化与精灵/SDF 复用。</summary>
        public MeshHandle UnitQuad2D
        {
            get
            {
                ushort id = Volatile.Read(ref _unitQuad2DId);
                if (id != 0)
                    return new MeshHandle(id);

                lock (_lock)
                {
                    if (_unitQuad2DId == 0)
                    {
                        MeshHandle handle = RegisterLocked(GeometryBuilder.CreateUnitQuad2D(), protectFromExternalUnregister: true);
                        Volatile.Write(ref _unitQuad2DId, handle.Id);
                    }
                    return new MeshHandle(_unitQuad2DId);
                }
            }
        }

        /// <summary>内置中心对齐单位 3D 立方体（<c>[-0.5..+0.5]^3</c>），供 3D 验证与基础实例化复用。</summary>
        public MeshHandle UnitCube3D
        {
            get
            {
                ushort id = Volatile.Read(ref _unitCube3DId);
                if (id != 0)
                    return new MeshHandle(id);

                lock (_lock)
                {
                    if (_unitCube3DId == 0)
                    {
                        MeshHandle handle = RegisterLocked(GeometryBuilder.CreateUnitCube3D(), protectFromExternalUnregister: true);
                        Volatile.Write(ref _unitCube3DId, handle.Id);
                    }
                    return new MeshHandle(_unitCube3DId);
                }
            }
        }

        /// <summary>注册任意实现 <see cref="IMesh"/> 的自定义网格并返回 2 字节 <see cref="MeshHandle"/>（线程安全）。</summary>
        public MeshHandle Register(IMesh mesh)
        {
            ArgumentNullException.ThrowIfNull(mesh);
            lock (_lock)
            {
                return RegisterLocked(mesh, protectFromExternalUnregister: false);
            }
        }

        /// <summary>注册内部受保护网格（如 <c>DrawSprite</c> 永久复用槽位），防止外部误删。</summary>
        internal MeshHandle RegisterProtected(IMesh mesh)
        {
            ArgumentNullException.ThrowIfNull(mesh);
            lock (_lock)
            {
                return RegisterLocked(mesh, protectFromExternalUnregister: true);
            }
        }

        /// <summary>注册静态网格并返回句柄。</summary>
        public MeshHandle RegisterStatic(ReadOnlySpan<UnifiedVertex> vertices, ReadOnlySpan<int> indices, PrimitiveType primitiveType = PrimitiveType.TriangleList)
        {
            return Register(new StaticMesh(vertices, indices, primitiveType));
        }

        /// <summary>从对象池租借一个动态网格并注册返回句柄。</summary>
        public MeshHandle AcquireDynamic(int initialVertexCapacity = 64, int initialIndexCapacity = 192, PrimitiveType primitiveType = PrimitiveType.TriangleList)
        {
            lock (_lock)
            {
                DynamicMesh mesh;
                if (_dynamicPool.Count > 0)
                {
                    mesh = _dynamicPool.Pop();
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

        /// <summary>标记动态网格待归还对象池：延迟到当前帧全部 Flush 结束后的 <see cref="EndFrame"/> 安全回收复用。</summary>
        public void ReleaseDynamic(MeshHandle handle)
            => ScheduleRetirement(handle, RetirementKind.ReturnDynamicPool);

        /// <summary>从内置对象池租借一个单组 <see cref="InstancedMesh"/> 并注册获得 2 字节 <see cref="MeshHandle"/>。</summary>
        public MeshHandle AcquireInstanced(MeshHandle baseMesh, int initialCapacity = 128)
        {
            lock (_lock)
            {
                InstancedMesh mesh;
                if (_instancedPool.Count > 0)
                {
                    mesh = _instancedPool.Pop();
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
        public void ReleaseInstanced(MeshHandle handle)
            => ScheduleRetirement(handle, RetirementKind.ReturnInstancedPool);

        /// <summary>标记自定义网格待注销：延迟到帧末 <see cref="EndFrame"/> 回收槽位并在主线程安全释放 GPU 资源。</summary>
        public void Unregister(MeshHandle handle, bool disposeMesh = true)
            => ScheduleRetirement(handle, disposeMesh ? RetirementKind.UnregisterAndDispose : RetirementKind.UnregisterOnly);

        /// <summary>O(1) 无锁安全解析句柄对应的 <see cref="IMesh"/>。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(MeshHandle handle, [NotNullWhen(true)] out IMesh? mesh)
        {
            int id = handle.Id;
            if (id != 0)
            {
                IMesh?[] slots = Volatile.Read(ref _slots);
                if ((uint)id < (uint)slots.Length)
                {
                    mesh = Volatile.Read(ref slots[id]);
                    return mesh is not null;
                }
            }

            mesh = null;
            return false;
        }

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
                    IMesh? removed = RemoveLocked(item.Id);
                    if (removed is null)
                        continue;

                    switch (item.Kind)
                    {
                        case RetirementKind.ReturnDynamicPool when removed is DynamicMesh dyn:
                            dyn.ResetForPool();
                            _dynamicPool.Push(dyn);
                            break;

                        case RetirementKind.ReturnInstancedPool when removed is InstancedMesh inst:
                            inst.ResetForPool(default);
                            _instancedPool.Push(inst);
                            break;

                        case RetirementKind.UnregisterAndDispose:
                            toDispose ??= new List<IMesh>();
                            toDispose.Add(removed);
                            break;
                    }
                }

                _pendingRetirements.Clear();
                _pendingRetirementSet.Clear();
            }

            if (toDispose is { Count: > 0 })
            {
                for (int i = 0; i < toDispose.Count; i++)
                    DisposeMeshSafe(toDispose[i]);
            }
        }

        internal void Unload()
        {
            List<IMesh>? toDispose = null;

            lock (_lock)
            {
                int limit = Math.Min(_highWaterMark, _slots.Length - 1);
                for (int i = 1; i <= limit; i++)
                {
                    IMesh? mesh = _slots[i];
                    if (mesh is not null)
                    {
                        toDispose ??= new List<IMesh>();
                        toDispose.Add(mesh);
                        _slots[i] = null;
                    }
                }

                _freeIds.Clear();
                _protectedIds.Clear();
                _dynamicPool.Clear();
                _instancedPool.Clear();
                _pendingRetirements.Clear();
                _pendingRetirementSet.Clear();
                _highWaterMark = 0;
                Volatile.Write(ref _activeCount, 0);
                Volatile.Write(ref _unitQuad2DId, 0);
                Volatile.Write(ref _unitCube3DId, 0);
            }

            if (toDispose is { Count: > 0 })
            {
                void DisposeAll()
                {
                    for (int i = 0; i < toDispose.Count; i++)
                    {
                        try { toDispose[i].Dispose(); }
                        catch { /* 卸载期忽略清理异常 */ }
                    }
                }

                if (RenderManager.Shaders.IsOnMainThread)
                    DisposeAll();
                else
                    Main.QueueMainThreadAction(DisposeAll);
            }
        }

        private void ScheduleRetirement(MeshHandle handle, RetirementKind kind)
        {
            if (!handle.IsValid)
                return;

            lock (_lock)
            {
                ushort id = handle.Id;
                if (id >= _slots.Length || _slots[id] is null || _protectedIds.Contains(id))
                    return;

                if (_pendingRetirementSet.Add(id))
                    _pendingRetirements.Add(new PendingRetirement(id, kind));
            }
        }

        private MeshHandle RegisterLocked(IMesh mesh, bool protectFromExternalUnregister)
        {
            ushort id = AllocateIdLocked();
            if (protectFromExternalUnregister)
                _protectedIds.Add(id);

            Volatile.Write(ref _slots[id], mesh);
            Volatile.Write(ref _activeCount, _activeCount + 1);
            return new MeshHandle(id);
        }

        private IMesh? RemoveLocked(ushort id)
        {
            if (id == 0 || id >= _slots.Length)
                return null;

            IMesh? mesh = _slots[id];
            if (mesh is null)
                return null;

            Volatile.Write(ref _slots[id], null);
            _freeIds.Push(id);
            Volatile.Write(ref _activeCount, Math.Max(0, _activeCount - 1));
            return mesh;
        }

        private ushort AllocateIdLocked()
        {
            if (_freeIds.Count > 0)
                return _freeIds.Pop();

            int next = _highWaterMark + 1;
            if (next > ushort.MaxValue)
                throw new InvalidOperationException($"网格数量已达 2 字节 MeshHandle 上限 ({ushort.MaxValue})。");

            if (next >= _slots.Length)
            {
                int newLen = Math.Min(ushort.MaxValue + 1, _slots.Length * 2);
                var grown = new IMesh?[newLen];
                Array.Copy(_slots, grown, _slots.Length);
                Volatile.Write(ref _slots, grown);
            }

            _highWaterMark = next;
            return (ushort)next;
        }

        private static void DisposeMeshSafe(IMesh mesh)
        {
            if (!mesh.IsStatic || RenderManager.Shaders.IsOnMainThread)
            {
                try { mesh.Dispose(); }
                catch { /* 忽略清理异常 */ }
            }
            else
            {
                Main.QueueMainThreadAction(() =>
                {
                    try { mesh.Dispose(); }
                    catch { /* 忽略清理异常 */ }
                });
            }
        }
    }
}
