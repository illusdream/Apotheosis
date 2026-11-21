#if DEBUG
#nullable enable

using Apotheosis.Runtime.Foundation.Diagnostics;
using Microsoft.Xna.Framework;

namespace Apotheosis.DebugTool.Gizmos
{
    /// <summary>
    /// 全部底层原语共享的样式与生命周期元数据头。
    /// </summary>
    internal struct GizmoMeta
    {
        public string Channel;
        public Color Color;
        public float Thickness;
        public float RemainingDuration;
        public float TotalDuration;

        public readonly bool IsTimed => TotalDuration > 0f;
    }

    /// <summary>
    /// 泛型原语条目：公共元数据 + 强类型负载，零装箱、无匿名通用槽位。
    /// </summary>
    internal struct GizmoEntry<TPayload> where TPayload : struct
    {
        public GizmoMeta Meta;
        public TPayload Payload;

        public GizmoEntry(in GizmoMeta meta, in TPayload payload)
        {
            Meta = meta;
            Payload = payload;
        }
    }

    /// <summary>原语 1：点或十字标记（屏幕像素尺寸）。</summary>
    internal readonly struct MarkerPayload
    {
        public readonly Vector2 Position;
        public readonly float SizePixels;
        public readonly bool IsCross;

        public MarkerPayload(Vector2 position, float sizePixels, bool isCross)
        {
            Position = position;
            SizePixels = sizePixels;
            IsCross = isCross;
        }
    }

    /// <summary>原语 2：实线、屏幕等距虚线或带实心箭头的有向线段。</summary>
    internal readonly struct SegmentPayload
    {
        public readonly Vector2 Start;
        public readonly Vector2 End;
        public readonly GizmoSegmentStyle Style;
        public readonly float DashOrHeadPixels;
        public readonly float GapPixels;

        public SegmentPayload(Vector2 start, Vector2 end, GizmoSegmentStyle style, float dashOrHeadPixels, float gapPixels)
        {
            Start = start;
            End = end;
            Style = style;
            DashOrHeadPixels = dashOrHeadPixels;
            GapPixels = gapPixels;
        }
    }

    /// <summary>原语 3：折线与闭合多边形（AABB / OBB / 贝塞尔曲线均展开为此原语）。</summary>
    internal readonly struct PolyPayload
    {
        public readonly int VertexOffset;
        public readonly int VertexCount;
        public readonly Vector2[]? OwnedVertices;
        public readonly bool Closed;
        public readonly bool Filled;
        public readonly float FillAlpha;

        public PolyPayload(int vertexOffset, int vertexCount, Vector2[]? ownedVertices, bool closed, bool filled, float fillAlpha)
        {
            VertexOffset = vertexOffset;
            VertexCount = vertexCount;
            OwnedVertices = ownedVertices;
            Closed = closed;
            Filled = filled;
            FillAlpha = fillAlpha;
        }
    }

    /// <summary>原语 4：圆、圆弧与扇形。</summary>
    internal readonly struct RadialPayload
    {
        public readonly Vector2 Center;
        public readonly float Radius;
        public readonly float StartAngle;
        public readonly float SweepAngle;
        public readonly int Segments;
        public readonly GizmoRadialMode Mode;
        public readonly bool Filled;
        public readonly float FillAlpha;

        public RadialPayload(
            Vector2 center,
            float radius,
            float startAngle,
            float sweepAngle,
            int segments,
            GizmoRadialMode mode,
            bool filled,
            float fillAlpha)
        {
            Center = center;
            Radius = radius;
            StartAngle = startAngle;
            SweepAngle = sweepAngle;
            Segments = segments;
            Mode = mode;
            Filled = filled;
            FillAlpha = fillAlpha;
        }
    }

    /// <summary>原语 5：带半透明暗色底框的调试文本标签。</summary>
    internal readonly struct LabelPayload
    {
        public readonly Vector2 WorldPos;
        public readonly Vector2 ScreenOffset;
        public readonly string Text;

        public LabelPayload(Vector2 worldPos, Vector2 screenOffset, string text)
        {
            WorldPos = worldPos;
            ScreenOffset = screenOffset;
            Text = text;
        }
    }

    internal readonly struct GizmoChannelSnapshot
    {
        public readonly string Name;
        public readonly int LastFrameCount;
        public readonly int TimedCount;

        public GizmoChannelSnapshot(string name, int lastFrameCount, int timedCount)
        {
            Name = name;
            LastFrameCount = lastFrameCount;
            TimedCount = timedCount;
        }
    }
}
#endif
