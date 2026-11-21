#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Foundation.Diagnostics
{
    /// <summary>
    /// 持久型世界空间 Gizmos 绘制提供者接口。
    /// <para>
    /// 当不希望在业务类中穿插调试绘制代码时，可实现本接口并通过
    /// <see cref="DebugGizmos.RegisterProvider"/> 挂载；仅在 Gizmos 叠层开启且对应频道启用时按帧回调。
    /// </para>
    /// </summary>
    public interface IGizmoProvider
    {
        /// <summary>所属频道名称（用于面板独立过滤开关）。</summary>
        string Channel { get; }

        /// <summary>每渲染帧回调一次，在内部调用 <see cref="DebugGizmos"/> 静态方法提交图元。</summary>
        void OnDrawGizmos();
    }

#if DEBUG
    internal enum GizmoSegmentStyle : byte
    {
        Solid = 0,
        Dashed = 1,
        Arrow = 2
    }

    internal enum GizmoRadialMode : byte
    {
        Circle = 0,
        Arc = 1,
        Sector = 2
    }

    /// <summary>
    /// 供 <c>Apotheosis.DebugTool</c> 实现的极简 5 原语接收器接口（隔离 ImGui 依赖，避免上帝命令结构）。
    /// </summary>
    internal interface IGizmoSink
    {
        bool IsEnabled { get; }

        void AddMarker(Vector2 position, Color color, float sizePixels, float thickness, bool isCross, float duration, string? channel);
        void AddSegment(Vector2 start, Vector2 end, Color color, float thickness, GizmoSegmentStyle style, float dashOrHeadPixels, float gapPixels, float duration, string? channel);
        void AddPoly(ReadOnlySpan<Vector2> points, Color color, bool closed, bool filled, float fillAlpha, float thickness, float duration, string? channel);
        void AddRadial(Vector2 center, float radius, float startAngle, float sweepAngle, Color color, GizmoRadialMode mode, bool filled, float fillAlpha, int segments, float thickness, float duration, string? channel);
        void AddLabel(Vector2 worldPosition, string text, Color color, Vector2 screenOffset, float duration, string? channel);

        void RegisterProvider(IGizmoProvider provider);
        void UnregisterProvider(IGizmoProvider provider);
    }
#endif

    /// <summary>
    /// 世界空间调试绘制（Gizmos）统一静态门面。
    /// <para>
    /// 所有绘制方法均标注 <see cref="ConditionalAttribute"/>(<c>"DEBUG"</c>)，仅依赖 XNA 基础数学类型：
    /// 业务代码可直接调用而无需包裹 <c>#if DEBUG</c>，在 <c>Release</c> 构建下调用点会被编译器完全消除（零开销、零 ImGui 依赖）。
    /// 上层组合形状（如 <see cref="Rect(Rectangle, Color, bool, float, float, float, string?)"/>、<see cref="OBB"/>、
    /// <see cref="Axes2D(Vector2, float, float, float, float, string?)"/>、贝塞尔曲线等）均在此层展开为底层 5 种基础原语。
    /// </para>
    /// </summary>
    public static class DebugGizmos
    {
        /// <summary>未指定频道时使用的默认分组名称。</summary>
        public const string DefaultChannel = "General";

#if DEBUG
        private static readonly object _providerSync = new();
        private static readonly List<IGizmoProvider> _pendingProviders = new();
        private static IGizmoSink? _sink;

        internal static void AttachSink(IGizmoSink sink)
        {
            IGizmoProvider[]? pending = null;
            lock (_providerSync)
            {
                _sink = sink;
                if (_pendingProviders.Count > 0)
                {
                    pending = _pendingProviders.ToArray();
                    _pendingProviders.Clear();
                }
            }

            if (pending is not null)
            {
                for (int i = 0; i < pending.Length; i++)
                    sink.RegisterProvider(pending[i]);
            }
        }

        internal static void DetachSink(IGizmoSink sink)
        {
            lock (_providerSync)
            {
                if (ReferenceEquals(_sink, sink))
                    _sink = null;

                _pendingProviders.Clear();
            }
        }
#endif

        /// <summary>
        /// 当前世界空间 Gizmos 叠层是否处于激活状态（主菜单、全屏大地图或关闭时均为 <c>false</c>）。
        /// 可在构造较昂贵的调试字符串或采样点前用于提前短路；在 <c>Release</c> 构建下恒为 <c>false</c>。
        /// </summary>
        public static bool IsEnabled
        {
            get
            {
#if DEBUG
                return _sink?.IsEnabled ?? false;
#else
                return false;
#endif
            }
        }

        // ==================== 点与 2D 基准轴 ====================

        /// <summary>在世界坐标绘制实心圆点（半径为屏幕像素）。</summary>
        [Conditional("DEBUG")]
        public static void Point(
            Vector2 position,
            Color color,
            float radiusPixels = 4f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            _sink?.AddMarker(position, color, radiusPixels, thickness: 1f, isCross: false, duration, channel);
#endif
        }

        /// <summary>在世界坐标绘制十字准星（尺寸为屏幕像素）。</summary>
        [Conditional("DEBUG")]
        public static void Cross(
            Vector2 position,
            Color color,
            float sizePixels = 8f,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            _sink?.AddMarker(position, color, sizePixels, thickness, isCross: true, duration, channel);
#endif
        }

        /// <summary>绘制纯 2D 局部坐标轴（+X 红，+Y 绿，绕 +Z 顺时针旋转弧度）。</summary>
        [Conditional("DEBUG")]
        public static void Axes2D(
            Vector2 origin,
            float rotation = 0f,
            float length = 36f,
            float thickness = 1.75f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            float cos = MathF.Cos(rotation);
            float sin = MathF.Sin(rotation);
            Vector2 xAxis = new(cos * length, sin * length);
            Vector2 yAxis = new(-sin * length, cos * length);
            Axes2D(origin, xAxis, yAxis, thickness, duration, channel);
#endif
        }

        /// <summary>使用显式 X/Y 基向量绘制 2D 局部坐标轴（+X 红，+Y 绿，原点白点）。</summary>
        [Conditional("DEBUG")]
        public static void Axes2D(
            Vector2 origin,
            Vector2 xAxis,
            Vector2 yAxis,
            float thickness = 1.75f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            IGizmoSink? sink = _sink;
            if (sink is null || !sink.IsEnabled)
                return;

            sink.AddSegment(origin, origin + xAxis, new Color(255, 72, 92), thickness, GizmoSegmentStyle.Arrow, 7f, 0f, duration, channel);
            sink.AddSegment(origin, origin + yAxis, new Color(64, 240, 148), thickness, GizmoSegmentStyle.Arrow, 7f, 0f, duration, channel);
            sink.AddMarker(origin, Color.White, 3f, 1f, isCross: false, duration, channel);
#endif
        }

        // ==================== 线、虚线与矢量 ====================

        /// <summary>绘制世界空间线段。</summary>
        [Conditional("DEBUG")]
        public static void Line(
            Vector2 start,
            Vector2 end,
            Color color,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            _sink?.AddSegment(start, end, color, thickness, GizmoSegmentStyle.Solid, 0f, 0f, duration, channel);
#endif
        }

        /// <summary>绘制屏幕等距虚线。</summary>
        [Conditional("DEBUG")]
        public static void DashedLine(
            Vector2 start,
            Vector2 end,
            Color color,
            float dashPixels = 8f,
            float gapPixels = 5f,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            _sink?.AddSegment(start, end, color, thickness, GizmoSegmentStyle.Dashed, dashPixels, gapPixels, duration, channel);
#endif
        }

        /// <summary>绘制带实心箭头的世界空间有向线段（适合速度、法线与瞄准方向）。</summary>
        [Conditional("DEBUG")]
        public static void Arrow(
            Vector2 start,
            Vector2 end,
            Color color,
            float headPixels = 9f,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            _sink?.AddSegment(start, end, color, thickness, GizmoSegmentStyle.Arrow, headPixels, 0f, duration, channel);
#endif
        }

        /// <summary>从起点沿方向向量绘制射线箭头（<see cref="Arrow"/> 的语义封装）。</summary>
        [Conditional("DEBUG")]
        public static void Ray(
            Vector2 origin,
            Vector2 directionAndLength,
            Color color,
            float headPixels = 9f,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            _sink?.AddSegment(origin, origin + directionAndLength, color, thickness, GizmoSegmentStyle.Arrow, headPixels, 0f, duration, channel);
#endif
        }

        // ==================== 矩形与旋转包围盒（组合为闭合多边形原语） ====================

        /// <summary>绘制轴对齐世界矩形（AABB）。</summary>
        [Conditional("DEBUG")]
        public static void Rect(
            Rectangle worldRect,
            Color color,
            bool filled = false,
            float fillAlpha = 0.15f,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            IGizmoSink? sink = _sink;
            if (sink is null || !sink.IsEnabled)
                return;

            float x0 = worldRect.X;
            float y0 = worldRect.Y;
            float x1 = worldRect.Right;
            float y1 = worldRect.Bottom;

            Span<Vector2> corners = stackalloc Vector2[4];
            corners[0] = new Vector2(x0, y0);
            corners[1] = new Vector2(x1, y0);
            corners[2] = new Vector2(x1, y1);
            corners[3] = new Vector2(x0, y1);

            sink.AddPoly(corners, color, closed: true, filled, fillAlpha, thickness, duration, channel);
#endif
        }

        /// <summary>以中心点与尺寸绘制轴对齐世界矩形（AABB）。</summary>
        [Conditional("DEBUG")]
        public static void Rect(
            Vector2 center,
            Vector2 size,
            Color color,
            bool filled = false,
            float fillAlpha = 0.15f,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            IGizmoSink? sink = _sink;
            if (sink is null || !sink.IsEnabled)
                return;

            float hw = MathF.Abs(size.X) * 0.5f;
            float hh = MathF.Abs(size.Y) * 0.5f;

            Span<Vector2> corners = stackalloc Vector2[4];
            corners[0] = new Vector2(center.X - hw, center.Y - hh);
            corners[1] = new Vector2(center.X + hw, center.Y - hh);
            corners[2] = new Vector2(center.X + hw, center.Y + hh);
            corners[3] = new Vector2(center.X - hw, center.Y + hh);

            sink.AddPoly(corners, color, closed: true, filled, fillAlpha, thickness, duration, channel);
#endif
        }

        /// <summary>绘制有向包围盒（OBB，旋转角为 Terraria 2D 顺时针弧度）。</summary>
        [Conditional("DEBUG")]
        public static void OBB(
            Vector2 center,
            Vector2 size,
            float rotation,
            Color color,
            bool filled = false,
            float fillAlpha = 0.15f,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            IGizmoSink? sink = _sink;
            if (sink is null || !sink.IsEnabled)
                return;

            float hw = MathF.Abs(size.X) * 0.5f;
            float hh = MathF.Abs(size.Y) * 0.5f;
            float cos = MathF.Cos(rotation);
            float sin = MathF.Sin(rotation);
            Vector2 ux = new(cos * hw, sin * hw);
            Vector2 uy = new(-sin * hh, cos * hh);

            Span<Vector2> corners = stackalloc Vector2[4];
            corners[0] = center - ux - uy;
            corners[1] = center + ux - uy;
            corners[2] = center + ux + uy;
            corners[3] = center - ux + uy;

            sink.AddPoly(corners, color, closed: true, filled, fillAlpha, thickness, duration, channel);
#endif
        }

        // ==================== 圆、圆弧与扇形 ====================

        /// <summary>绘制世界空间圆（<paramref name="segments"/> 为 0 时按屏幕像素半径自适应细分）。</summary>
        [Conditional("DEBUG")]
        public static void Circle(
            Vector2 center,
            float radius,
            Color color,
            bool filled = false,
            float fillAlpha = 0.12f,
            int segments = 0,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            _sink?.AddRadial(center, radius, 0f, MathF.Tau, color, GizmoRadialMode.Circle, filled, fillAlpha, segments, thickness, duration, channel);
#endif
        }

        /// <summary>绘制世界空间圆弧（角度为弧度，顺时针为正）。</summary>
        [Conditional("DEBUG")]
        public static void Arc(
            Vector2 center,
            float radius,
            float startAngle,
            float sweepAngle,
            Color color,
            int segments = 0,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            _sink?.AddRadial(center, radius, startAngle, sweepAngle, color, GizmoRadialMode.Arc, filled: false, fillAlpha: 0f, segments, thickness, duration, channel);
#endif
        }

        /// <summary>绘制世界空间扇形（适合近战挥舞/锥形探测范围调试）。</summary>
        [Conditional("DEBUG")]
        public static void Sector(
            Vector2 center,
            float radius,
            float startAngle,
            float sweepAngle,
            Color color,
            bool filled = false,
            float fillAlpha = 0.12f,
            int segments = 0,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            _sink?.AddRadial(center, radius, startAngle, sweepAngle, color, GizmoRadialMode.Sector, filled, fillAlpha, segments, thickness, duration, channel);
#endif
        }

        // ==================== 折线、多边形与贝塞尔曲线 ====================

        /// <summary>绘制世界空间折线（可选闭合）。</summary>
        [Conditional("DEBUG")]
        public static void Polyline(
            ReadOnlySpan<Vector2> points,
            Color color,
            bool closed = false,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            _sink?.AddPoly(points, color, closed, filled: false, fillAlpha: 0f, thickness, duration, channel);
#endif
        }

        /// <summary>绘制世界空间折线（列表重载）。</summary>
        [Conditional("DEBUG")]
        public static void Polyline(
            IReadOnlyList<Vector2> points,
            Color color,
            bool closed = false,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            if (_sink is null || !_sink.IsEnabled || points is null || points.Count < 2)
                return;

            SubmitListAsPoly(points, color, closed, filled: false, fillAlpha: 0f, thickness, duration, channel);
#endif
        }

        /// <summary>绘制世界空间闭合多边形（支持凸多边形半透明填充，自动校正屏幕绕序）。</summary>
        [Conditional("DEBUG")]
        public static void Polygon(
            ReadOnlySpan<Vector2> points,
            Color color,
            bool filled = false,
            float fillAlpha = 0.12f,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            if (points.Length < 3)
                return;

            _sink?.AddPoly(points, color, closed: true, filled, fillAlpha, thickness, duration, channel);
#endif
        }

        /// <summary>绘制世界空间闭合多边形（列表重载）。</summary>
        [Conditional("DEBUG")]
        public static void Polygon(
            IReadOnlyList<Vector2> points,
            Color color,
            bool filled = false,
            float fillAlpha = 0.12f,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            if (_sink is null || !_sink.IsEnabled || points is null || points.Count < 3)
                return;

            SubmitListAsPoly(points, color, closed: true, filled, fillAlpha, thickness, duration, channel);
#endif
        }

        /// <summary>绘制二阶贝塞尔曲线。</summary>
        [Conditional("DEBUG")]
        public static void BezierQuadratic(
            Vector2 p0,
            Vector2 p1,
            Vector2 p2,
            Color color,
            int segments = 20,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            IGizmoSink? sink = _sink;
            if (sink is null || !sink.IsEnabled)
                return;

            int count = Math.Clamp(segments, 2, 128);
            Span<Vector2> pts = stackalloc Vector2[count + 1];
            float inv = 1f / count;
            for (int i = 0; i <= count; i++)
            {
                float t = i * inv;
                float u = 1f - t;
                pts[i] = (u * u) * p0 + (2f * u * t) * p1 + (t * t) * p2;
            }

            sink.AddPoly(pts, color, closed: false, filled: false, fillAlpha: 0f, thickness, duration, channel);
#endif
        }

        /// <summary>绘制三阶贝塞尔曲线。</summary>
        [Conditional("DEBUG")]
        public static void BezierCubic(
            Vector2 p0,
            Vector2 p1,
            Vector2 p2,
            Vector2 p3,
            Color color,
            int segments = 24,
            float thickness = 1.5f,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            IGizmoSink? sink = _sink;
            if (sink is null || !sink.IsEnabled)
                return;

            int count = Math.Clamp(segments, 2, 128);
            Span<Vector2> pts = stackalloc Vector2[count + 1];
            float inv = 1f / count;
            for (int i = 0; i <= count; i++)
            {
                float t = i * inv;
                float u = 1f - t;
                float uu = u * u;
                float tt = t * t;
                pts[i] = (uu * u) * p0 + (3f * uu * t) * p1 + (3f * u * tt) * p2 + (tt * t) * p3;
            }

            sink.AddPoly(pts, color, closed: false, filled: false, fillAlpha: 0f, thickness, duration, channel);
#endif
        }

        // ==================== 文本标签与持久 Provider ====================

        /// <summary>在世界坐标位置绘制带半透明底框的调试文本标签。</summary>
        [Conditional("DEBUG")]
        public static void Label(
            Vector2 worldPosition,
            string text,
            Color color,
            Vector2 screenOffset = default,
            float duration = 0f,
            string? channel = null)
        {
#if DEBUG
            if (!string.IsNullOrEmpty(text))
                _sink?.AddLabel(worldPosition, text, color, screenOffset, duration, channel);
#endif
        }

        /// <summary>注册持久型 Gizmos 提供者（支持在任意 <c>ModSystem.Load()</c> 时机调用，重复注册同一实例将被忽略）。</summary>
        [Conditional("DEBUG")]
        public static void RegisterProvider(IGizmoProvider provider)
        {
#if DEBUG
            if (provider is null)
                return;

            IGizmoSink? sink;
            lock (_providerSync)
            {
                sink = _sink;
                if (sink is null)
                {
                    for (int i = 0; i < _pendingProviders.Count; i++)
                    {
                        if (ReferenceEquals(_pendingProviders[i], provider))
                            return;
                    }

                    _pendingProviders.Add(provider);
                    return;
                }
            }

            sink.RegisterProvider(provider);
#endif
        }

        /// <summary>注销持久型 Gizmos 提供者。</summary>
        [Conditional("DEBUG")]
        public static void UnregisterProvider(IGizmoProvider provider)
        {
#if DEBUG
            if (provider is null)
                return;

            IGizmoSink? sink;
            lock (_providerSync)
            {
                sink = _sink;
                _pendingProviders.Remove(provider);
            }

            sink?.UnregisterProvider(provider);
#endif
        }

#if DEBUG
        private static void SubmitListAsPoly(
            IReadOnlyList<Vector2> points,
            Color color,
            bool closed,
            bool filled,
            float fillAlpha,
            float thickness,
            float duration,
            string? channel)
        {
            IGizmoSink? sink = _sink;
            if (sink is null || !sink.IsEnabled)
                return;

            int count = points.Count;
            if (count <= 128)
            {
                Span<Vector2> buffer = stackalloc Vector2[count];
                for (int i = 0; i < count; i++)
                    buffer[i] = points[i];

                sink.AddPoly(buffer, color, closed, filled, fillAlpha, thickness, duration, channel);
            }
            else
            {
                var buffer = new Vector2[count];
                for (int i = 0; i < count; i++)
                    buffer[i] = points[i];

                sink.AddPoly(buffer, color, closed, filled, fillAlpha, thickness, duration, channel);
            }
        }
#endif
    }
}
