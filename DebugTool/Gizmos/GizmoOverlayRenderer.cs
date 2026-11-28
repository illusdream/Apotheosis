#if DEBUG
#nullable enable

using System;
using System.Collections.Generic;
using Apotheosis.DebugTool.Theme;
using Apotheosis.Runtime.Foundation.Diagnostics;
using Hexa.NET.ImGui;
using Microsoft.Xna.Framework;
using Terraria.ModLoader;
using NumVec2 = System.Numerics.Vector2;
using NumVec4 = System.Numerics.Vector4;

namespace Apotheosis.DebugTool.Gizmos
{
    /// <summary>
    /// 基于 <c>ImGui.GetBackgroundDrawList()</c> 的世界空间 Gizmos 渲染器。
    /// <para>
    /// 使用 5 种强类型原语轨道渲染，自动校正屏幕空间顺时针（CW）填充绕序，
    /// 确保在任意缩放、重力药剂上下翻转、负角度扇形与任意顶点顺序多边形下均精确无瑕疵。
    /// </para>
    /// </summary>
    [NoJIT]
    internal sealed class GizmoOverlayRenderer
    {
        private delegate bool DrawPayloadDelegate<TPayload>(
            ref RenderContext ctx,
            in GizmoMeta meta,
            in TPayload payload,
            float alphaFactor,
            float thickness) where TPayload : struct;

        private struct RenderContext
        {
            // 注意：值类型字段不直接引用 ImDrawListPtr，避免 tML 加载期 GetLoadableTypes() 布局 struct 时提前解析 Hexa.NET.ImGui
            public nint DrawListHandle;
            public WorldScreenTransform Transform;
            public GizmoSettings Settings;
            public ulong[] EnabledChannelBits;
            public int EnabledChannelWordCount;
            public List<NumVec2> ScreenScratch;
            public List<NumVec2> SectorScratch;
            public IReadOnlyList<Vector2>? ActivePolyVertices;
            public float GlobalOpacity;
            public float ThicknessScale;
            public int Submitted;
            public int Rendered;
            public int Culled;

            public unsafe ImDrawListPtr DrawList => new((ImDrawList*)DrawListHandle);
        }

        private readonly List<NumVec2> _screenScratch = new(128);
        private readonly List<NumVec2> _sectorScratch = new(128);
        private ulong[] _enabledChannelBits = new ulong[2];

        private static readonly DrawPayloadDelegate<MarkerPayload> DrawMarkerHandler = DrawMarker;
        private static readonly DrawPayloadDelegate<SegmentPayload> DrawSegmentHandler = DrawSegment;
        private static readonly DrawPayloadDelegate<PolyPayload> DrawPolyHandler = DrawPoly;
        private static readonly DrawPayloadDelegate<RadialPayload> DrawRadialHandler = DrawRadial;
        private static readonly DrawPayloadDelegate<LabelPayload> DrawLabelHandler = DrawLabel;

        /// <summary>
        /// 执行单帧世界空间 Gizmos 采集与绘制。
        /// </summary>
        public unsafe void Render(GizmoCommandBuffer buffer, int backBufferWidth, int backBufferHeight)
        {
            if (!buffer.IsEnabled)
                return;

            GizmoSettings settings = buffer.Settings;
            WorldScreenTransform transform = WorldScreenTransform.Capture(backBufferWidth, backBufferHeight);
            ImDrawListPtr drawList = ImGui.GetBackgroundDrawList();

            var ctx = new RenderContext
            {
                DrawListHandle = (nint)drawList.Handle,
                Transform = transform,
                Settings = settings,
                ScreenScratch = _screenScratch,
                SectorScratch = _sectorScratch,
                GlobalOpacity = Math.Clamp(settings.GlobalOpacity, 0.05f, 1.0f),
                ThicknessScale = Math.Clamp(settings.ThicknessScale, 0.25f, 4.0f)
            };

            lock (buffer.SyncRoot)
            {
                buffer.CollectOverlayProvidersLocked();

                // 在采集完 Provider 之后单次加锁快照全部频道位图，后续逐图元无锁 O(1) 判定
                settings.CopyEnabledBitsSnapshot(ref _enabledChannelBits, out int enabledWordCount);
                ctx.EnabledChannelBits = _enabledChannelBits;
                ctx.EnabledChannelWordCount = enabledWordCount;

                RenderTrack(ref ctx, buffer.Markers, DrawMarkerHandler);
                RenderTrack(ref ctx, buffer.Segments, DrawSegmentHandler);
                RenderPolyTrack(ref ctx, buffer.Polys, buffer.FramePolyVertices, buffer.OverlayPolyVertices);
                RenderTrack(ref ctx, buffer.Radials, DrawRadialHandler);

                if (settings.ShowLabels)
                    RenderTrack(ref ctx, buffer.Labels, DrawLabelHandler);
                else
                    CountSkippedTrack(ref ctx, buffer.Labels);

                buffer.CompleteRenderFrameLocked(ctx.Submitted, ctx.Rendered, ctx.Culled);
            }
        }

        private static void RenderTrack<TPayload>(
            ref RenderContext ctx,
            GizmoTrack<TPayload> track,
            DrawPayloadDelegate<TPayload> handler) where TPayload : struct
        {
            RenderEntries(ref ctx, track.Timed, handler);
            RenderEntries(ref ctx, track.Frame, handler);
            RenderEntries(ref ctx, track.Overlay, handler);
        }

        private static void RenderPolyTrack(
            ref RenderContext ctx,
            GizmoTrack<PolyPayload> track,
            IReadOnlyList<Vector2> frameVertices,
            IReadOnlyList<Vector2> overlayVertices)
        {
            ctx.ActivePolyVertices = null;
            RenderEntries(ref ctx, track.Timed, DrawPolyHandler);

            ctx.ActivePolyVertices = frameVertices;
            RenderEntries(ref ctx, track.Frame, DrawPolyHandler);

            ctx.ActivePolyVertices = overlayVertices;
            RenderEntries(ref ctx, track.Overlay, DrawPolyHandler);

            ctx.ActivePolyVertices = null;
        }

        private static void CountSkippedTrack<TPayload>(ref RenderContext ctx, GizmoTrack<TPayload> track) where TPayload : struct
        {
            int skipped = track.TimedCount + track.FrameCount + track.OverlayCount;
            ctx.Submitted += skipped;
            ctx.Culled += skipped;
        }

        private static void RenderEntries<TPayload>(
            ref RenderContext ctx,
            IReadOnlyList<GizmoEntry<TPayload>> entries,
            DrawPayloadDelegate<TPayload> handler) where TPayload : struct
        {
            for (int i = 0; i < entries.Count; i++)
            {
                GizmoEntry<TPayload> entry = entries[i];
                ctx.Submitted++;

                if (!GizmoSettings.IsEnabledInSnapshot(ctx.EnabledChannelBits, ctx.EnabledChannelWordCount, entry.Meta.ChannelId))
                {
                    ctx.Culled++;
                    continue;
                }

                float alphaFactor = ComputeAlphaFactor(in ctx, in entry.Meta);
                if (alphaFactor <= 0.004f)
                {
                    ctx.Culled++;
                    continue;
                }

                float thickness = MathF.Max(0.75f, entry.Meta.Thickness * ctx.ThicknessScale);
                if (handler(ref ctx, in entry.Meta, in entry.Payload, alphaFactor, thickness))
                    ctx.Rendered++;
                else
                    ctx.Culled++;
            }
        }

        private static float ComputeAlphaFactor(in RenderContext ctx, in GizmoMeta meta)
        {
            float alpha = ctx.GlobalOpacity;
            if (meta.IsTimed && ctx.Settings.FadeTimedGizmos && meta.TotalDuration > 0.001f)
            {
                float ratio = meta.RemainingDuration / meta.TotalDuration;
                if (ratio < 0.25f)
                    alpha *= Math.Clamp(ratio / 0.25f, 0f, 1f);
            }

            return alpha;
        }

        // ==================== 5 种底层原语渲染实现 ====================

        private static bool DrawMarker(
            ref RenderContext ctx,
            in GizmoMeta meta,
            in MarkerPayload p,
            float alphaFactor,
            float thickness)
        {
            NumVec2 screenPos = ctx.Transform.WorldToScreen(p.Position);
            float margin = (p.IsCross ? p.SizePixels * 0.5f : p.SizePixels) + 8f;
            if (!ctx.Transform.IsPointVisible(screenPos, margin))
                return false;

            uint col = ToImU32(meta.Color, alphaFactor);
            if (p.IsCross)
            {
                float half = p.SizePixels * 0.5f;
                ctx.DrawList.AddLine(new NumVec2(screenPos.X - half, screenPos.Y), new NumVec2(screenPos.X + half, screenPos.Y), col, thickness);
                ctx.DrawList.AddLine(new NumVec2(screenPos.X, screenPos.Y - half), new NumVec2(screenPos.X, screenPos.Y + half), col, thickness);
            }
            else
            {
                ctx.DrawList.AddCircleFilled(screenPos, p.SizePixels, col, 14);
            }

            return true;
        }

        private static bool DrawSegment(
            ref RenderContext ctx,
            in GizmoMeta meta,
            in SegmentPayload p,
            float alphaFactor,
            float thickness)
        {
            NumVec2 a = ctx.Transform.WorldToScreen(p.Start);
            NumVec2 b = ctx.Transform.WorldToScreen(p.End);
            float margin = p.Style == GizmoSegmentStyle.Arrow ? p.DashOrHeadPixels + 16f : 32f;
            if (!ctx.Transform.IsSegmentVisible(a, b, margin))
                return false;

            uint col = ToImU32(meta.Color, alphaFactor);
            switch (p.Style)
            {
                case GizmoSegmentStyle.Dashed:
                    DrawDashedLine(ctx.DrawList, in ctx.Transform, a, b, p.DashOrHeadPixels, p.GapPixels, col, thickness);
                    break;

                case GizmoSegmentStyle.Arrow:
                    DrawArrow(ctx.DrawList, a, b, p.DashOrHeadPixels, col, thickness);
                    break;

                default:
                    ctx.DrawList.AddLine(a, b, col, thickness);
                    break;
            }

            return true;
        }

        private static bool DrawPoly(
            ref RenderContext ctx,
            in GizmoMeta meta,
            in PolyPayload p,
            float alphaFactor,
            float thickness)
        {
            if (p.VertexCount < 2)
                return false;

            List<NumVec2> scratch = ctx.ScreenScratch;
            scratch.Clear();

            float minX = float.PositiveInfinity;
            float minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity;

            if (p.OwnedVertices is not null)
            {
                Vector2[] verts = p.OwnedVertices;
                int limit = Math.Min(p.VertexCount, verts.Length);
                for (int i = 0; i < limit; i++)
                {
                    NumVec2 sp = ctx.Transform.WorldToScreen(verts[i]);
                    scratch.Add(sp);
                    ExpandBounds(sp, ref minX, ref minY, ref maxX, ref maxY);
                }
            }
            else if (ctx.ActivePolyVertices is not null)
            {
                IReadOnlyList<Vector2> pool = ctx.ActivePolyVertices;
                int start = p.VertexOffset;
                int end = Math.Min(start + p.VertexCount, pool.Count);
                for (int i = start; i < end; i++)
                {
                    NumVec2 sp = ctx.Transform.WorldToScreen(pool[i]);
                    scratch.Add(sp);
                    ExpandBounds(sp, ref minX, ref minY, ref maxX, ref maxY);
                }
            }

            if (scratch.Count < 2 || !ctx.Transform.IsBoundsVisible(new NumVec2(minX, minY), new NumVec2(maxX, maxY)))
                return false;

            if (p.Closed && p.Filled && p.FillAlpha > 0.001f && scratch.Count >= 3)
            {
                uint fillCol = ToImU32(meta.Color, alphaFactor * p.FillAlpha);
                FillConvexCW(ctx.DrawList, scratch, fillCol);
            }

            StrokePath(ctx.DrawList, scratch, p.Closed, ToImU32(meta.Color, alphaFactor), thickness);
            return true;
        }

        private static bool DrawRadial(
            ref RenderContext ctx,
            in GizmoMeta meta,
            in RadialPayload p,
            float alphaFactor,
            float thickness)
        {
            NumVec2 screenCenter = ctx.Transform.WorldToScreen(p.Center);
            if (!ctx.Transform.IsCircleVisible(screenCenter, p.Radius))
                return false;

            float screenRadius = p.Radius * ctx.Transform.PixelsPerWorldUnit;
            uint strokeCol = ToImU32(meta.Color, alphaFactor);
            List<NumVec2> scratch = ctx.ScreenScratch;

            switch (p.Mode)
            {
                case GizmoRadialMode.Circle:
                {
                    int segs = ResolveCircleSegments(screenRadius, MathF.Tau, p.Segments);
                    SampleArcToScratch(in ctx.Transform, scratch, p.Center, p.Radius, 0f, MathF.Tau, segs, includeEndPoint: false);

                    if (p.Filled && p.FillAlpha > 0.001f)
                        FillConvexCW(ctx.DrawList, scratch, ToImU32(meta.Color, alphaFactor * p.FillAlpha));

                    StrokePath(ctx.DrawList, scratch, closed: true, strokeCol, thickness);
                    return true;
                }

                case GizmoRadialMode.Arc:
                {
                    int segs = ResolveCircleSegments(screenRadius, MathF.Abs(p.SweepAngle), p.Segments);
                    SampleArcToScratch(in ctx.Transform, scratch, p.Center, p.Radius, p.StartAngle, p.SweepAngle, segs, includeEndPoint: true);
                    StrokePath(ctx.DrawList, scratch, closed: false, strokeCol, thickness);
                    return true;
                }

                case GizmoRadialMode.Sector:
                {
                    float absSweep = MathF.Abs(p.SweepAngle);
                    int segs = ResolveCircleSegments(screenRadius, absSweep, p.Segments);
                    SampleArcToScratch(in ctx.Transform, scratch, p.Center, p.Radius, p.StartAngle, p.SweepAngle, segs, includeEndPoint: true);

                    if (p.Filled && p.FillAlpha > 0.001f)
                        FillSectorCW(ctx.DrawList, screenCenter, scratch, ctx.SectorScratch, absSweep, ToImU32(meta.Color, alphaFactor * p.FillAlpha));

                    if (scratch.Count >= 2)
                    {
                        ctx.DrawList.AddLine(screenCenter, scratch[0], strokeCol, thickness);
                        StrokePath(ctx.DrawList, scratch, closed: false, strokeCol, thickness);
                        ctx.DrawList.AddLine(scratch[^1], screenCenter, strokeCol, thickness);
                    }

                    return true;
                }

                default:
                    return false;
            }
        }

        private static bool DrawLabel(
            ref RenderContext ctx,
            in GizmoMeta meta,
            in LabelPayload p,
            float alphaFactor,
            float thickness)
        {
            if (string.IsNullOrEmpty(p.Text))
                return false;

            NumVec2 anchor = ctx.Transform.WorldToScreen(p.WorldPos);
            NumVec2 pos = new(anchor.X + p.ScreenOffset.X, anchor.Y + p.ScreenOffset.Y);
            if (!ctx.Transform.IsPointVisible(pos, 220f))
                return false;

            NumVec2 textSize = ImGui.CalcTextSize(p.Text);
            NumVec2 pad = new(5f, 2f);
            NumVec2 max = new(pos.X + textSize.X + pad.X * 2f, pos.Y + textSize.Y + pad.Y * 2f);

            uint bgCol = CyberpunkWidgets.ToU32(CyberpunkTheme.BgObsidian, 0.80f * alphaFactor);
            uint borderCol = ToImU32(meta.Color, 0.65f * alphaFactor);
            uint textCol = ToImU32(meta.Color, alphaFactor);

            ctx.DrawList.AddRectFilled(pos, max, bgCol, 2f);
            ctx.DrawList.AddRect(pos, max, borderCol, 2f);
            ctx.DrawList.AddText(pos + pad, textCol, p.Text);
            return true;
        }

        // ==================== 几何采样与顺时针绕序保护 ====================

        private static void SampleArcToScratch(
            in WorldScreenTransform tf,
            List<NumVec2> scratch,
            Vector2 center,
            float radius,
            float startAngle,
            float sweepAngle,
            int segments,
            bool includeEndPoint)
        {
            scratch.Clear();
            float step = sweepAngle / segments;
            int count = includeEndPoint ? segments + 1 : segments;

            for (int i = 0; i < count; i++)
            {
                float angle = startAngle + step * i;
                Vector2 worldPt = new(
                    center.X + MathF.Cos(angle) * radius,
                    center.Y + MathF.Sin(angle) * radius);
                scratch.Add(tf.WorldToScreen(worldPt));
            }
        }

        private static void StrokePath(ImDrawListPtr dl, List<NumVec2> points, bool closed, uint col, float thickness)
        {
            if (points.Count < 2)
                return;

            dl.PathClear();
            for (int i = 0; i < points.Count; i++)
                dl.PathLineTo(points[i]);

            dl.PathStroke(col, closed ? ImDrawFlags.Closed : ImDrawFlags.None, thickness);
        }

        /// <summary>
        /// 按屏幕坐标系（+X 向右、+Y 向下）强制顺时针（CW）提交凸多边形填充，避免重力倒转或逆时针顶点导致 ImGui 抗锯齿羽化翻转。
        /// </summary>
        private static void FillConvexCW(ImDrawListPtr dl, List<NumVec2> points, uint fillCol)
        {
            int count = points.Count;
            if (count < 3)
                return;

            float signedArea2 = 0f;
            for (int i = 0; i < count; i++)
            {
                NumVec2 curr = points[i];
                NumVec2 next = points[(i + 1) % count];
                signedArea2 += curr.X * next.Y - next.X * curr.Y;
            }

            if (MathF.Abs(signedArea2) < 1e-4f)
                return;

            dl.PathClear();
            if (signedArea2 > 0f)
            {
                for (int i = 0; i < count; i++)
                    dl.PathLineTo(points[i]);
            }
            else
            {
                for (int i = count - 1; i >= 0; i--)
                    dl.PathLineTo(points[i]);
            }

            dl.PathFillConvex(fillCol);
        }

        /// <summary>
        /// 使用凸多边形填充扇形（消除逐三角形填充产生的内部抗锯齿辐条接缝）：
        /// <c>|sweep| &lt;= π</c> 单次凸填充；<c>&gt; π</c> 拆成两半凸扇形填充。
        /// </summary>
        private static void FillSectorCW(
            ImDrawListPtr dl,
            NumVec2 center,
            List<NumVec2> rim,
            List<NumVec2> polyScratch,
            float absSweep,
            uint fillCol)
        {
            if (rim.Count < 2)
                return;

            if (absSweep <= MathF.PI + 1e-4f || rim.Count < 3)
            {
                polyScratch.Clear();
                polyScratch.Add(center);
                for (int i = 0; i < rim.Count; i++)
                    polyScratch.Add(rim[i]);

                FillConvexCW(dl, polyScratch, fillCol);
                return;
            }

            int mid = rim.Count / 2;

            polyScratch.Clear();
            polyScratch.Add(center);
            for (int i = 0; i <= mid; i++)
                polyScratch.Add(rim[i]);
            FillConvexCW(dl, polyScratch, fillCol);

            polyScratch.Clear();
            polyScratch.Add(center);
            for (int i = mid; i < rim.Count; i++)
                polyScratch.Add(rim[i]);
            FillConvexCW(dl, polyScratch, fillCol);
        }

        private static void AddTriangleFilledCW(ImDrawListPtr dl, NumVec2 a, NumVec2 b, NumVec2 c, uint fillCol)
        {
            float cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (cross >= 0f)
                dl.AddTriangleFilled(a, b, c, fillCol);
            else
                dl.AddTriangleFilled(a, c, b, fillCol);
        }

        private static void DrawDashedLine(
            ImDrawListPtr dl,
            in WorldScreenTransform tf,
            NumVec2 a,
            NumVec2 b,
            float dashPixels,
            float gapPixels,
            uint col,
            float thickness)
        {
            NumVec2 delta = b - a;
            float totalLen = delta.Length();
            if (!float.IsFinite(totalLen) || totalLen <= 0.5f)
                return;

            // 先经 Liang-Barsky 裁剪到视口可见区间，避免超长虚线在屏幕外耗尽段数额度
            if (!ClipSegmentToViewport(a, b, tf.BackBufferWidth, tf.BackBufferHeight, margin: 32f, out float t0, out float t1))
                return;

            NumVec2 dir = delta / totalLen;
            float stride = dashPixels + gapPixels;

            // 起始位置对齐到 stride 整数倍，保证镜头移动时虚线相位稳定钉在世界坐标
            float startDist = MathF.Max(0f, MathF.Floor((t0 * totalLen) / stride) * stride);
            float endDist = MathF.Min(totalLen, t1 * totalLen);

            const int maxDashes = 512;
            int count = 0;
            for (float dist = startDist; dist < endDist && count < maxDashes; dist += stride, count++)
            {
                float segEnd = MathF.Min(dist + dashPixels, endDist);
                if (segEnd > dist + 0.25f)
                    dl.AddLine(a + dir * dist, a + dir * segEnd, col, thickness);
            }
        }

        private static bool ClipSegmentToViewport(
            NumVec2 a,
            NumVec2 b,
            int width,
            int height,
            float margin,
            out float t0,
            out float t1)
        {
            t0 = 0f;
            t1 = 1f;

            float dx = b.X - a.X;
            float dy = b.Y - a.Y;

            return ClipEdge(-dx, a.X + margin, ref t0, ref t1)
                && ClipEdge(dx, width + margin - a.X, ref t0, ref t1)
                && ClipEdge(-dy, a.Y + margin, ref t0, ref t1)
                && ClipEdge(dy, height + margin - a.Y, ref t0, ref t1);
        }

        private static bool ClipEdge(float p, float q, ref float t0, ref float t1)
        {
            if (MathF.Abs(p) < 1e-6f)
                return q >= 0f;

            float r = q / p;
            if (p < 0f)
            {
                if (r > t1)
                    return false;
                if (r > t0)
                    t0 = r;
            }
            else
            {
                if (r < t0)
                    return false;
                if (r < t1)
                    t1 = r;
            }

            return true;
        }

        private static void DrawArrow(ImDrawListPtr dl, NumVec2 a, NumVec2 b, float headPixels, uint col, float thickness)
        {
            NumVec2 delta = b - a;
            float len = delta.Length();
            if (len <= 0.5f)
            {
                dl.AddCircleFilled(a, MathF.Max(2f, thickness), col, 8);
                return;
            }

            NumVec2 dir = delta / len;
            float headLen = MathF.Min(headPixels, len * 0.45f);
            float headHalfW = headLen * 0.48f;
            NumVec2 perp = new(-dir.Y, dir.X);
            NumVec2 basePt = b - dir * headLen;

            dl.AddLine(a, basePt, col, thickness);
            AddTriangleFilledCW(dl, b, basePt + perp * headHalfW, basePt - perp * headHalfW, col);
        }

        private static int ResolveCircleSegments(float screenRadius, float absSweepRadians, int requestedSegments)
        {
            if (requestedSegments > 0)
                return Math.Clamp(requestedSegments, 4, 160);

            float arcFraction = Math.Clamp(absSweepRadians / MathF.Tau, 0.05f, 1f);
            int fullCircleSegs = (int)Math.Clamp(MathF.Ceiling(screenRadius * 0.45f), 16f, 96f);
            return Math.Max(4, (int)MathF.Ceiling(fullCircleSegs * arcFraction));
        }

        private static void ExpandBounds(NumVec2 p, ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }

        private static uint ToImU32(Color color, float alphaMultiplier)
        {
            float a = (color.A / 255f) * Math.Clamp(alphaMultiplier, 0f, 1f);
            return ImGui.ColorConvertFloat4ToU32(new NumVec4(
                color.R / 255f,
                color.G / 255f,
                color.B / 255f,
                a));
        }
    }
}
#endif
