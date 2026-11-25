#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Xna.Framework.Graphics;
using Terraria;

namespace Apotheosis.Runtime.Rendering
{
    /// <summary>GPU 资源主线程安全释放工具。</summary>
    internal static class GpuResourceHelper
    {
        private const int MaxDynamicBufferCapacity = 0x4000_0000;

        /// <summary>若当前已在主线程则立即执行，否则派发至主线程队列。</summary>
        public static void RunOnMainThread(Action action)
        {
            if (RenderManager.Shaders.IsOnMainThread)
                action();
            else
                Main.QueueMainThreadAction(action);
        }

        /// <summary>安全释放单个 GPU/托管资源（非主线程自动派发至主线程，忽略卸载期异常）。</summary>
        public static void DisposeOnMainThread(IDisposable? resource)
        {
            if (resource is null)
                return;

            if (resource is GraphicsResource gr && gr.IsDisposed)
                return;

            if (RenderManager.Shaders.IsOnMainThread)
                DisposeSafe(resource);
            else
                Main.QueueMainThreadAction(() => DisposeSafe(resource));
        }

        /// <summary>批量在主线程安全释放资源列表。</summary>
        public static void DisposeAllOnMainThread<T>(List<T>? resources) where T : class, IDisposable
        {
            if (resources is not { Count: > 0 })
                return;

            if (RenderManager.Shaders.IsOnMainThread)
            {
                for (int i = 0; i < resources.Count; i++)
                    DisposeSafe(resources[i]);
                return;
            }

            // 异步派发前拷贝数组快照，防止调用方复用并清空原 List 导致漏释放
            T[] snapshot = resources.ToArray();
            Main.QueueMainThreadAction(() =>
            {
                for (int i = 0; i < snapshot.Length; i++)
                    DisposeSafe(snapshot[i]);
            });
        }

        internal static void DisposeSafe(IDisposable? resource)
        {
            if (resource is null)
                return;

            if (resource is GraphicsResource gr && gr.IsDisposed)
                return;

            try { resource.Dispose(); }
            catch { /* 卸载或设备重置期忽略释放异常 */ }
        }

        internal static int ComputePowerOfTwoCapacity(int requiredCount, int minCapacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(requiredCount);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(requiredCount, MaxDynamicBufferCapacity);
            return (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(requiredCount, minCapacity));
        }
    }

    /// <summary>可按需 2 的幂扩容并以 <see cref="SetDataOptions.Discard"/> 上传的动态 GPU 顶点缓冲封装。</summary>
    internal sealed class DynamicGpuVertexBuffer<TVertex> where TVertex : struct, IVertexType
    {
        private readonly VertexDeclaration _declaration;
        private readonly int _minCapacity;
        private DynamicVertexBuffer? _buffer;

        public DynamicGpuVertexBuffer(VertexDeclaration declaration, int minCapacity = 256)
        {
            _declaration = declaration;
            _minCapacity = Math.Max(16, minCapacity);
        }

        public DynamicVertexBuffer? Buffer => _buffer;
        public int Capacity => _buffer is { IsDisposed: false } b ? b.VertexCount : 0;

        /// <summary>确保底层 GPU 顶点缓冲就绪且容量充足，然后以 Discard 模式上传数据。</summary>
        public DynamicVertexBuffer UploadDiscard(GraphicsDevice gd, TVertex[] data, int count)
        {
            DynamicVertexBuffer buf = EnsureCapacity(gd, count);
            if (count > 0)
                buf.SetData(data, 0, count, SetDataOptions.Discard);
            return buf;
        }

        /// <summary>确保底层 GPU 顶点缓冲就绪且容量充足，然后按显式顶点步长以 Discard 模式上传数据。</summary>
        public DynamicVertexBuffer UploadDiscard(GraphicsDevice gd, TVertex[] data, int count, int vertexStride)
        {
            DynamicVertexBuffer buf = EnsureCapacity(gd, count);
            if (count > 0)
                buf.SetData(0, data, 0, count, vertexStride, SetDataOptions.Discard);
            return buf;
        }

        public DynamicVertexBuffer EnsureCapacity(GraphicsDevice gd, int requiredCount)
        {
            DynamicVertexBuffer? current = _buffer;
            if (current is null || current.IsDisposed || !ReferenceEquals(current.GraphicsDevice, gd) || current.VertexCount < requiredCount)
            {
                _buffer = null;
                GpuResourceHelper.DisposeSafe(current);
                int newCap = GpuResourceHelper.ComputePowerOfTwoCapacity(requiredCount, _minCapacity);
                current = new DynamicVertexBuffer(gd, _declaration, newCap, BufferUsage.WriteOnly);
                _buffer = current;
            }

            return current;
        }

        public void DisposeOnMainThread()
        {
            DynamicVertexBuffer? toDispose = Interlocked.Exchange(ref _buffer, null);
            GpuResourceHelper.DisposeOnMainThread(toDispose);
        }

        public void DisposeImmediate()
        {
            DynamicVertexBuffer? toDispose = Interlocked.Exchange(ref _buffer, null);
            GpuResourceHelper.DisposeSafe(toDispose);
        }
    }

    /// <summary>可按需 2 的幂扩容并以 <see cref="SetDataOptions.Discard"/> 上传的动态 GPU 索引缓冲封装。</summary>
    internal sealed class DynamicGpuIndexBuffer<TIndex> where TIndex : unmanaged
    {
        private readonly IndexElementSize _elementSize;
        private readonly int _minCapacity;
        private DynamicIndexBuffer? _buffer;

        public DynamicGpuIndexBuffer(IndexElementSize elementSize, int minCapacity = 1024)
        {
            int expectedBytes = elementSize == IndexElementSize.SixteenBits ? 2 : 4;
            if (Unsafe.SizeOf<TIndex>() != expectedBytes)
                throw new ArgumentException($"索引类型 {typeof(TIndex).Name} 大小（{Unsafe.SizeOf<TIndex>()}B）与 {elementSize} 不匹配。", nameof(elementSize));

            _elementSize = elementSize;
            _minCapacity = Math.Max(32, minCapacity);
        }

        public DynamicIndexBuffer? Buffer => _buffer;
        public int Capacity => _buffer is { IsDisposed: false } b ? b.IndexCount : 0;

        /// <summary>确保底层 GPU 索引缓冲就绪且容量充足，然后以 Discard 模式上传数据。</summary>
        public DynamicIndexBuffer UploadDiscard(GraphicsDevice gd, TIndex[] data, int count)
        {
            DynamicIndexBuffer buf = EnsureCapacity(gd, count);
            if (count > 0)
                buf.SetData(data, 0, count, SetDataOptions.Discard);
            return buf;
        }

        public DynamicIndexBuffer EnsureCapacity(GraphicsDevice gd, int requiredCount)
        {
            DynamicIndexBuffer? current = _buffer;
            if (current is null || current.IsDisposed || !ReferenceEquals(current.GraphicsDevice, gd) || current.IndexCount < requiredCount)
            {
                _buffer = null;
                GpuResourceHelper.DisposeSafe(current);
                int newCap = GpuResourceHelper.ComputePowerOfTwoCapacity(requiredCount, _minCapacity);
                current = new DynamicIndexBuffer(gd, _elementSize, newCap, BufferUsage.WriteOnly);
                _buffer = current;
            }

            return current;
        }

        public void DisposeOnMainThread()
        {
            DynamicIndexBuffer? toDispose = Interlocked.Exchange(ref _buffer, null);
            GpuResourceHelper.DisposeOnMainThread(toDispose);
        }

        public void DisposeImmediate()
        {
            DynamicIndexBuffer? toDispose = Interlocked.Exchange(ref _buffer, null);
            GpuResourceHelper.DisposeSafe(toDispose);
        }
    }
}
