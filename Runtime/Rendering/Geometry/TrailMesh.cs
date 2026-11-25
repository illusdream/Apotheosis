#nullable enable

using System;
using Apotheosis.Runtime.Foundation.Collections;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Geometry
{
    /// <summary>基于环形历史点队列的通用 2D 拖尾网格。</summary>
    public sealed class TrailMesh : IMesh
    {
        private readonly RingBuffer<Vector2> _points;
        private readonly DynamicMesh _backingMesh;
        private bool _dirty;

        public float StartWidth { get; set; } = 16f;
        public float EndWidth { get; set; } = 1f;
        public Color StartColor { get; set; } = Color.White;
        public Color EndColor { get; set; } = Color.Transparent;
        public Func<float, float>? WidthCurve { get; set; }
        public Func<float, Color>? ColorCurve { get; set; }
        public float DepthZ { get; set; }

        /// <summary>相邻采样点最小平方距离（过滤重复重叠点避免法线退化）。</summary>
        public float MinPointDistanceSq { get; set; } = 1f;

        public int MaxPoints => _points.Capacity;
        public int PointCount => _points.Count;

        public PrimitiveType PrimitiveType => PrimitiveType.TriangleList;
        public bool IsEmpty => _points.Count < 2;
        public bool IsStatic => false;

        public TrailMesh(int maxPoints = 32)
        {
            if (maxPoints < 2)
                maxPoints = 2;

            _points = new RingBuffer<Vector2>(maxPoints);
            _backingMesh = new DynamicMesh(maxPoints * 2, (maxPoints - 1) * 6, PrimitiveType.TriangleList);
        }

        /// <summary>O(1) 在头部压入一个最新世界坐标点（最旧点自动滑出）。</summary>
        public void PushPoint(Vector2 worldPos)
        {
            if (_points.Count > 0 && Vector2.DistanceSquared(_points[0], worldPos) < MinPointDistanceSq)
                return;

            _points.PushFront(in worldPos);
            _dirty = true;
        }

        /// <summary>直接用外部点列（例如原版 <c>Projectile.oldPos</c>）覆写拖尾点集。</summary>
        public void SetPoints(ReadOnlySpan<Vector2> worldPoints)
        {
            _points.OverwriteFrom(worldPoints);
            _dirty = true;
        }

        /// <summary>强制标记为脏（修改宽度/颜色曲线后调用）。</summary>
        public void MarkDirty() => _dirty = true;

        public void Clear()
        {
            _points.Clear();
            _backingMesh.Clear();
            _dirty = false;
        }

        public void PrepareForDraw()
        {
            if (!_dirty)
                return;

            _dirty = false;
            _backingMesh.Clear();
            if (_points.Count < 2)
                return;

            _points.GetSegments(out ReadOnlySpan<Vector2> first, out ReadOnlySpan<Vector2> second);
            _backingMesh.AddTrailRibbon(first, second, StartWidth, EndWidth, StartColor, EndColor, WidthCurve, ColorCurve, DepthZ);
        }

        public ReadOnlySpan<UnifiedVertex> GetVertices() => _backingMesh.GetVertices();
        public ReadOnlySpan<int> GetIndices() => _backingMesh.GetIndices();

        public void Dispose() { }
    }
}
