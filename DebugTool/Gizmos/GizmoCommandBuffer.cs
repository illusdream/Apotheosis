#if DEBUG
#nullable enable

using System;
using System.Collections.Generic;
using Apotheosis.Runtime.Foundation.Diagnostics;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis.DebugTool.Gizmos
{
    /// <summary>
    /// 强类型原语轨道：
    /// <list type="bullet">
    /// <item><description><see cref="Frame"/>：逻辑帧双缓冲快照（<c>DoUpdate</c> 结束时 O(1) 交换，高刷屏零闪烁、暂停精准定格）。</description></item>
    /// <item><description><see cref="Overlay"/>：渲染帧即时缓冲（<c>PreDraw</c>/<c>PostDraw</c> 与 <see cref="IGizmoProvider"/> 写入，画完即清）。</description></item>
    /// <item><description><see cref="Timed"/>：跨帧延时缓冲（非暂停逻辑帧衰减，暂停定格）。</description></item>
    /// </list>
    /// </summary>
    [NoJIT]
    internal sealed class GizmoTrack<TPayload> where TPayload : struct
    {
        private List<GizmoEntry<TPayload>> _writing = new(64);
        private List<GizmoEntry<TPayload>> _frame = new(64);
        private readonly List<GizmoEntry<TPayload>> _overlay = new(32);
        private readonly List<GizmoEntry<TPayload>> _timed = new(16);

        public IReadOnlyList<GizmoEntry<TPayload>> Frame => _frame;
        public IReadOnlyList<GizmoEntry<TPayload>> Overlay => _overlay;
        public IReadOnlyList<GizmoEntry<TPayload>> Timed => _timed;

        public int WritingCount => _writing.Count;
        public int FrameCount => _frame.Count;
        public int OverlayCount => _overlay.Count;
        public int TimedCount => _timed.Count;

        public void AddWriting(in GizmoMeta meta, in TPayload payload) => _writing.Add(new GizmoEntry<TPayload>(in meta, in payload));
        public void AddOverlay(in GizmoMeta meta, in TPayload payload) => _overlay.Add(new GizmoEntry<TPayload>(in meta, in payload));
        public void AddTimed(in GizmoMeta meta, in TPayload payload) => _timed.Add(new GizmoEntry<TPayload>(in meta, in payload));

        /// <summary>将本逻辑帧写入缓冲区 O(1) 交换为只读展示帧，并清空旧缓冲区准备下一帧。</summary>
        public void CommitWritingToFrame()
        {
            (_frame, _writing) = (_writing, _frame);
            _writing.Clear();
        }

        public void ClearWriting() => _writing.Clear();
        public void ClearFrame() => _frame.Clear();
        public void ClearOverlay() => _overlay.Clear();
        public void ClearTimed() => _timed.Clear();

        public void ClearAll()
        {
            _writing.Clear();
            _frame.Clear();
            _overlay.Clear();
            _timed.Clear();
        }

        public void DecayTimed(float deltaSeconds, Action<ushort> onExpired)
        {
            for (int i = _timed.Count - 1; i >= 0; i--)
            {
                GizmoEntry<TPayload> entry = _timed[i];
                entry.Meta.RemainingDuration -= deltaSeconds;
                if (entry.Meta.RemainingDuration <= 0f)
                {
                    onExpired(entry.Meta.ChannelId);

                    int last = _timed.Count - 1;
                    if (i != last)
                        _timed[i] = _timed[last];

                    _timed.RemoveAt(last);
                }
                else
                {
                    _timed[i] = entry;
                }
            }
        }
    }

    /// <summary>
    /// 世界空间 Gizmos 命令缓冲与频道管理中心（5 种强类型原语轨道、逻辑帧双缓冲、线程安全）。
    /// </summary>
    [NoJIT]
    internal sealed class GizmoCommandBuffer : IGizmoSink
    {
        private sealed class ChannelCounter
        {
            public readonly ushort ChannelId;
            public readonly string Name;
            public int WritingFrameCount;
            public int ActiveFrameCount;
            public int PendingOverlayCount;
            public int LastFrameCount;
            public int TimedCount;

            public ChannelCounter(ushort channelId, string name)
            {
                ChannelId = channelId;
                Name = name;
            }
        }

        private readonly object _sync = new();
        private readonly GizmoSettings _settings;
        private readonly Action<ushort> _onTimedExpired;

        private readonly GizmoTrack<MarkerPayload> _markers = new();
        private readonly GizmoTrack<SegmentPayload> _segments = new();
        private readonly GizmoTrack<PolyPayload> _polys = new();
        private readonly GizmoTrack<RadialPayload> _radials = new();
        private readonly GizmoTrack<LabelPayload> _labels = new();

        private List<Vector2> _writingPolyVertices = new(256);
        private List<Vector2> _framePolyVertices = new(256);
        private readonly List<Vector2> _overlayPolyVertices = new(128);

        private readonly List<IGizmoProvider> _providers = new();
        private readonly List<ChannelCounter?> _countersById = new(16);
        private readonly List<ChannelCounter> _activeCounters = new(16);

        private bool _inDrawPhase;
        private bool _collectingOverlay;
        private bool _channelListDirty;
        private bool _writingFrameOverflowed;
        private bool _activeFrameOverflowed;
        private bool _pendingOverlayOverflowed;

        public GizmoSettings Settings => _settings;

        /// <summary>
        /// 与 <c>IsGizmoOverlayActive</c> 保持严格一致：关闭、主菜单、全屏大地图或全屏设置/图鉴界面时均返回 <c>false</c>。
        /// </summary>
        public bool IsEnabled => _settings.Enabled && !Main.gameMenu && !Main.mapFullscreen && !Main.inFancyUI;

        public int LastSubmittedCount { get; private set; }
        public int LastRenderedCount { get; private set; }
        public int LastCulledCount { get; private set; }
        public int TimedCount { get; private set; }
        public int ProviderCount { get; private set; }
        public bool LastFrameOverflowed { get; private set; }
        public bool TimedOverflowed { get; private set; }

        internal object SyncRoot => _sync;
        internal GizmoTrack<MarkerPayload> Markers => _markers;
        internal GizmoTrack<SegmentPayload> Segments => _segments;
        internal GizmoTrack<PolyPayload> Polys => _polys;
        internal GizmoTrack<RadialPayload> Radials => _radials;
        internal GizmoTrack<LabelPayload> Labels => _labels;
        internal IReadOnlyList<Vector2> FramePolyVertices => _framePolyVertices;
        internal IReadOnlyList<Vector2> OverlayPolyVertices => _overlayPolyVertices;

        public GizmoCommandBuffer(GizmoSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _onTimedExpired = DecrementChannelTimedLocked;
        }

        // ==================== IGizmoSink（5 种底层原语入口） ====================

        public void AddMarker(
            Vector2 position,
            Color color,
            float sizePixels,
            float thickness,
            bool isCross,
            float duration,
            string? channel)
        {
            if (!IsEnabled || !IsFinite(position))
                return;

            lock (_sync)
            {
                if (!IsEnabled)
                    return;

                float minSize = isCross ? 2f : 1f;
                float safeSize = float.IsFinite(sizePixels) ? MathF.Max(minSize, sizePixels) : minSize;
                var payload = new MarkerPayload(position, safeSize, isCross);
                EnqueueLocked(_markers, payload, color, thickness, duration, channel);
            }
        }

        public void AddSegment(
            Vector2 start,
            Vector2 end,
            Color color,
            float thickness,
            GizmoSegmentStyle style,
            float dashOrHeadPixels,
            float gapPixels,
            float duration,
            string? channel)
        {
            if (!IsEnabled || !IsFinite(start) || !IsFinite(end))
                return;

            lock (_sync)
            {
                if (!IsEnabled)
                    return;

                float pA = style switch
                {
                    GizmoSegmentStyle.Dashed => float.IsFinite(dashOrHeadPixels) ? MathF.Max(2f, dashOrHeadPixels) : 8f,
                    GizmoSegmentStyle.Arrow => float.IsFinite(dashOrHeadPixels) ? MathF.Max(3f, dashOrHeadPixels) : 9f,
                    _ => 0f
                };
                float pB = style == GizmoSegmentStyle.Dashed
                    ? (float.IsFinite(gapPixels) ? MathF.Max(2f, gapPixels) : 5f)
                    : 0f;

                var payload = new SegmentPayload(start, end, style, pA, pB);
                EnqueueLocked(_segments, payload, color, thickness, duration, channel);
            }
        }

        public void AddPoly(
            ReadOnlySpan<Vector2> points,
            Color color,
            bool closed,
            bool filled,
            float fillAlpha,
            float thickness,
            float duration,
            string? channel)
        {
            if (!IsEnabled || points.Length < 2)
                return;

            for (int i = 0; i < points.Length; i++)
            {
                if (!IsFinite(points[i]))
                    return;
            }

            lock (_sync)
            {
                if (!IsEnabled)
                    return;

                int limit = GetCommandLimit();
                float safeDuration = SanitizeDuration(duration);
                float alpha = float.IsFinite(fillAlpha) ? Math.Clamp(fillAlpha, 0f, 1f) : 0f;

                if (safeDuration > 0f)
                {
                    if (TotalTimedCountLocked() >= limit)
                    {
                        TimedOverflowed = true;
                        return;
                    }

                    ChannelCounter timedCounter = EnsureChannelLocked(NormalizeChannel(channel));
                    GizmoMeta timedMeta = CreateMeta(timedCounter.ChannelId, color, thickness, safeDuration);

                    Vector2[] copy = new Vector2[points.Length];
                    points.CopyTo(copy);

                    _polys.AddTimed(in timedMeta, new PolyPayload(0, copy.Length, copy, closed, filled, alpha));
                    timedCounter.TimedCount++;
                    TimedCount = TotalTimedCountLocked();
                    return;
                }

                bool routeToOverlay = ShouldRouteToOverlayLocked();
                if (routeToOverlay)
                {
                    if (TotalOverlayCountLocked() >= limit)
                    {
                        _pendingOverlayOverflowed = true;
                        return;
                    }
                }
                else if (TotalWritingCountLocked() >= limit)
                {
                    _writingFrameOverflowed = true;
                    return;
                }

                ChannelCounter counter = EnsureChannelLocked(NormalizeChannel(channel));
                GizmoMeta meta = CreateMeta(counter.ChannelId, color, thickness, safeDuration);

                List<Vector2> vertPool = routeToOverlay ? _overlayPolyVertices : _writingPolyVertices;
                int offset = vertPool.Count;
                for (int i = 0; i < points.Length; i++)
                    vertPool.Add(points[i]);

                var payload = new PolyPayload(offset, points.Length, ownedVertices: null, closed, filled, alpha);

                if (routeToOverlay)
                {
                    _polys.AddOverlay(in meta, in payload);
                    counter.PendingOverlayCount++;
                }
                else
                {
                    _polys.AddWriting(in meta, in payload);
                    counter.WritingFrameCount++;
                }
            }
        }

        public void AddRadial(
            Vector2 center,
            float radius,
            float startAngle,
            float sweepAngle,
            Color color,
            GizmoRadialMode mode,
            bool filled,
            float fillAlpha,
            int segments,
            float thickness,
            float duration,
            string? channel)
        {
            if (!IsEnabled || !IsFinite(center) || !float.IsFinite(radius) || radius <= 0f)
                return;

            if (!float.IsFinite(startAngle) || !float.IsFinite(sweepAngle))
                return;

            if (mode != GizmoRadialMode.Circle && MathF.Abs(sweepAngle) < 1e-5f)
                return;

            lock (_sync)
            {
                if (!IsEnabled)
                    return;

                float clampedSweep = mode == GizmoRadialMode.Sector
                    ? Math.Clamp(sweepAngle, -MathF.Tau, MathF.Tau)
                    : sweepAngle;
                float safeAlpha = float.IsFinite(fillAlpha) ? Math.Clamp(fillAlpha, 0f, 1f) : 0f;

                var payload = new RadialPayload(
                    center,
                    radius,
                    startAngle,
                    clampedSweep,
                    segments,
                    mode,
                    filled,
                    safeAlpha);

                EnqueueLocked(_radials, payload, color, thickness, duration, channel);
            }
        }

        public void AddLabel(
            Vector2 worldPosition,
            string text,
            Color color,
            Vector2 screenOffset,
            float duration,
            string? channel)
        {
            if (!IsEnabled || string.IsNullOrEmpty(text) || !IsFinite(worldPosition))
                return;

            Vector2 safeOffset = IsFinite(screenOffset) ? screenOffset : Vector2.Zero;

            lock (_sync)
            {
                if (!IsEnabled)
                    return;

                var payload = new LabelPayload(worldPosition, safeOffset, text);
                EnqueueLocked(_labels, payload, color, thickness: 1f, duration, channel);
            }
        }

        public void RegisterProvider(IGizmoProvider provider)
        {
            if (provider is null)
                return;

            lock (_sync)
            {
                for (int i = 0; i < _providers.Count; i++)
                {
                    if (ReferenceEquals(_providers[i], provider))
                        return;
                }

                _providers.Add(provider);
                ProviderCount = _providers.Count;
                EnsureChannelLocked(NormalizeChannel(provider.Channel));
            }
        }

        public void UnregisterProvider(IGizmoProvider provider)
        {
            if (provider is null)
                return;

            lock (_sync)
            {
                _providers.Remove(provider);
                ProviderCount = _providers.Count;
            }
        }

        // ==================== Update / Draw 阶段边界钩子 ====================

        /// <summary>在 <c>orig(DoUpdate)</c> 之前调用：清空本逻辑帧写入缓冲，准备接收新一帧逻辑提交。</summary>
        public void BeginUpdate()
        {
            lock (_sync)
            {
                ClearWritingTracksLocked();
                _writingFrameOverflowed = false;
            }
        }

        /// <summary>
        /// 在 <c>orig(DoUpdate)</c> 之后调用：
        /// <list type="bullet">
        /// <item><description>未激活叠层时清空单帧与写入缓冲；</description></item>
        /// <item><description>游戏暂停时丢弃本帧写入、冻结展示帧 <c>_frame</c> 与延时队列；</description></item>
        /// <item><description>游戏正常推进时将写入缓冲 O(1) 交换为展示帧，并按 <paramref name="deltaSeconds"/> 衰减延时队列。</description></item>
        /// </list>
        /// </summary>
        public void EndUpdate(float deltaSeconds, bool overlayActiveInWorld, bool isGamePaused)
        {
            lock (_sync)
            {
                if (!overlayActiveInWorld)
                {
                    ClearWritingTracksLocked();
                    ClearFrameTracksLocked();
                    ClearOverlayTracksLocked();

                    LastSubmittedCount = 0;
                    LastRenderedCount = 0;
                    LastCulledCount = 0;
                    LastFrameOverflowed = false;
                    _writingFrameOverflowed = false;
                    _activeFrameOverflowed = false;
                    _pendingOverlayOverflowed = false;
                    return;
                }

                // 游戏暂停：保留上一个正常逻辑帧的 _frame 与 _timed，不做交换也不扣减寿命
                if (isGamePaused)
                {
                    ClearWritingTracksLocked();
                    _writingFrameOverflowed = false;
                    return;
                }

                // 游戏正常前进了一帧：O(1) 交换 _writing -> _frame，实现高刷屏零闪烁与跳帧自动覆盖
                CommitWritingToFrameLocked();

                _markers.DecayTimed(deltaSeconds, _onTimedExpired);
                _segments.DecayTimed(deltaSeconds, _onTimedExpired);
                _polys.DecayTimed(deltaSeconds, _onTimedExpired);
                _radials.DecayTimed(deltaSeconds, _onTimedExpired);
                _labels.DecayTimed(deltaSeconds, _onTimedExpired);

                TimedCount = TotalTimedCountLocked();
                if (TimedCount < GetCommandLimit())
                    TimedOverflowed = false;
            }
        }

        /// <summary>在 <c>orig(DoDraw)</c> 之前调用：标记进入渲染阶段，使 <c>PreDraw</c>/<c>PostDraw</c> 提交自动进入 Overlay 轨道。</summary>
        public void BeginDrawPhase()
        {
            lock (_sync)
            {
                _inDrawPhase = true;
                ClearOverlayTracksLocked();
                _pendingOverlayOverflowed = false;
            }
        }

        /// <summary>在 <c>Hook_DoDraw</c> 结束时调用：退出渲染阶段并兜底清理未被消费的 Overlay 轨道。</summary>
        public void EndDrawPhase()
        {
            lock (_sync)
            {
                _inDrawPhase = false;
                ClearOverlayTracksLocked();
                _pendingOverlayOverflowed = false;
            }
        }

        /// <summary>在渲染帧中追加采集全部已注册的 <see cref="IGizmoProvider"/>。</summary>
        public void CollectOverlayProvidersLocked()
        {
            if (!IsEnabled)
                return;

            _collectingOverlay = true;
            try
            {
                for (int i = 0; i < _providers.Count; i++)
                {
                    IGizmoProvider provider = _providers[i];
                    string ch = NormalizeChannel(provider.Channel);
                    ChannelCounter counter = EnsureChannelLocked(ch);

                    if (!_settings.IsChannelEnabled(counter.ChannelId))
                        continue;

                    try
                    {
                        provider.OnDrawGizmos();
                    }
                    catch
                    {
                        // 单个 Provider 异常不阻断调试绘制与游戏主循环
                    }
                }
            }
            finally
            {
                _collectingOverlay = false;
            }
        }

        /// <summary>
        /// 单帧绘制完成后更新统计与溢出快照，并清空本帧 Overlay 轨道（<c>_frame</c> 保持只读不清空，供高刷屏与暂停复用）。
        /// </summary>
        public void CompleteRenderFrameLocked(int submittedCount, int renderedCount, int culledCount)
        {
            LastSubmittedCount = submittedCount;
            LastRenderedCount = renderedCount;
            LastCulledCount = culledCount;
            LastFrameOverflowed = _activeFrameOverflowed || _pendingOverlayOverflowed;

            for (int i = 0; i < _activeCounters.Count; i++)
            {
                ChannelCounter counter = _activeCounters[i];
                counter.LastFrameCount = counter.ActiveFrameCount + counter.PendingOverlayCount;
                counter.PendingOverlayCount = 0;
            }

            ClearOverlayTracksLocked();
            _pendingOverlayOverflowed = false;
        }

        /// <summary>将有序的频道实时统计快照填充至调用方缓冲区（避免锁外暴露内部集合）。</summary>
        public void FillChannelSnapshots(List<GizmoChannelSnapshot> destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            destination.Clear();

            lock (_sync)
            {
                if (_channelListDirty)
                {
                    _activeCounters.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
                    _channelListDirty = false;
                }

                for (int i = 0; i < _activeCounters.Count; i++)
                {
                    ChannelCounter counter = _activeCounters[i];
                    destination.Add(new GizmoChannelSnapshot(counter.Name, counter.LastFrameCount, counter.TimedCount));
                }
            }
        }

        /// <summary>立即清空全部延时留存图元。</summary>
        public void ClearTimed()
        {
            lock (_sync)
            {
                _markers.ClearTimed();
                _segments.ClearTimed();
                _polys.ClearTimed();
                _radials.ClearTimed();
                _labels.ClearTimed();

                TimedCount = 0;
                TimedOverflowed = false;

                for (int i = 0; i < _activeCounters.Count; i++)
                    _activeCounters[i].TimedCount = 0;
            }
        }

        /// <summary>退出世界时清空单帧、叠层与延时图元（保留已注册 Provider 与用户频道偏好）。</summary>
        public void ClearWorldTransient()
        {
            lock (_sync)
            {
                ClearWritingTracksLocked();
                ClearFrameTracksLocked();
                ClearOverlayTracksLocked();

                _markers.ClearTimed();
                _segments.ClearTimed();
                _polys.ClearTimed();
                _radials.ClearTimed();
                _labels.ClearTimed();

                for (int i = 0; i < _activeCounters.Count; i++)
                {
                    ChannelCounter counter = _activeCounters[i];
                    counter.WritingFrameCount = 0;
                    counter.ActiveFrameCount = 0;
                    counter.PendingOverlayCount = 0;
                    counter.LastFrameCount = 0;
                    counter.TimedCount = 0;
                }

                LastSubmittedCount = 0;
                LastRenderedCount = 0;
                LastCulledCount = 0;
                TimedCount = 0;
                LastFrameOverflowed = false;
                _writingFrameOverflowed = false;
                _activeFrameOverflowed = false;
                _pendingOverlayOverflowed = false;
                TimedOverflowed = false;
            }
        }

        /// <summary>模组卸载时彻底清空全部缓冲、频道与 Provider。</summary>
        public void ClearAll()
        {
            lock (_sync)
            {
                _markers.ClearAll();
                _segments.ClearAll();
                _polys.ClearAll();
                _radials.ClearAll();
                _labels.ClearAll();
                _writingPolyVertices.Clear();
                _framePolyVertices.Clear();
                _overlayPolyVertices.Clear();
                _providers.Clear();
                _countersById.Clear();
                _activeCounters.Clear();

                ProviderCount = 0;
                TimedCount = 0;
                LastSubmittedCount = 0;
                LastRenderedCount = 0;
                LastCulledCount = 0;
                LastFrameOverflowed = false;
                _writingFrameOverflowed = false;
                _activeFrameOverflowed = false;
                _pendingOverlayOverflowed = false;
                TimedOverflowed = false;
                _inDrawPhase = false;
                _collectingOverlay = false;
                _channelListDirty = false;
            }
        }

        // ==================== 内部辅助 ====================

        private void EnqueueLocked<TPayload>(
            GizmoTrack<TPayload> track,
            in TPayload payload,
            Color color,
            float thickness,
            float duration,
            string? channel) where TPayload : struct
        {
            int limit = GetCommandLimit();
            float safeDuration = SanitizeDuration(duration);

            if (safeDuration > 0f)
            {
                if (TotalTimedCountLocked() >= limit)
                {
                    TimedOverflowed = true;
                    return;
                }

                ChannelCounter timedCounter = EnsureChannelLocked(NormalizeChannel(channel));
                GizmoMeta timedMeta = CreateMeta(timedCounter.ChannelId, color, thickness, safeDuration);
                track.AddTimed(in timedMeta, in payload);
                timedCounter.TimedCount++;
                TimedCount = TotalTimedCountLocked();
                return;
            }

            if (ShouldRouteToOverlayLocked())
            {
                if (TotalOverlayCountLocked() >= limit)
                {
                    _pendingOverlayOverflowed = true;
                    return;
                }

                ChannelCounter overlayCounter = EnsureChannelLocked(NormalizeChannel(channel));
                GizmoMeta overlayMeta = CreateMeta(overlayCounter.ChannelId, color, thickness, safeDuration);
                track.AddOverlay(in overlayMeta, in payload);
                overlayCounter.PendingOverlayCount++;
            }
            else
            {
                if (TotalWritingCountLocked() >= limit)
                {
                    _writingFrameOverflowed = true;
                    return;
                }

                ChannelCounter frameCounter = EnsureChannelLocked(NormalizeChannel(channel));
                GizmoMeta frameMeta = CreateMeta(frameCounter.ChannelId, color, thickness, safeDuration);
                track.AddWriting(in frameMeta, in payload);
                frameCounter.WritingFrameCount++;
            }
        }

        /// <summary>
        /// Draw 阶段（含 <c>PreDraw</c>/<c>PostDraw</c>）及 Provider 采集期写入 Overlay 轨道；Update 阶段写入双缓冲逻辑帧轨道。
        /// </summary>
        private bool ShouldRouteToOverlayLocked() => _inDrawPhase || _collectingOverlay;

        private void CommitWritingToFrameLocked()
        {
            _markers.CommitWritingToFrame();
            _segments.CommitWritingToFrame();
            _polys.CommitWritingToFrame();
            _radials.CommitWritingToFrame();
            _labels.CommitWritingToFrame();

            (_framePolyVertices, _writingPolyVertices) = (_writingPolyVertices, _framePolyVertices);
            _writingPolyVertices.Clear();

            _activeFrameOverflowed = _writingFrameOverflowed;
            _writingFrameOverflowed = false;

            for (int i = 0; i < _activeCounters.Count; i++)
            {
                ChannelCounter counter = _activeCounters[i];
                counter.ActiveFrameCount = counter.WritingFrameCount;
                counter.WritingFrameCount = 0;
            }
        }

        private void ClearWritingTracksLocked()
        {
            _markers.ClearWriting();
            _segments.ClearWriting();
            _polys.ClearWriting();
            _radials.ClearWriting();
            _labels.ClearWriting();
            _writingPolyVertices.Clear();

            for (int i = 0; i < _activeCounters.Count; i++)
                _activeCounters[i].WritingFrameCount = 0;
        }

        private void ClearFrameTracksLocked()
        {
            _markers.ClearFrame();
            _segments.ClearFrame();
            _polys.ClearFrame();
            _radials.ClearFrame();
            _labels.ClearFrame();
            _framePolyVertices.Clear();

            for (int i = 0; i < _activeCounters.Count; i++)
                _activeCounters[i].ActiveFrameCount = 0;
        }

        private void ClearOverlayTracksLocked()
        {
            _markers.ClearOverlay();
            _segments.ClearOverlay();
            _polys.ClearOverlay();
            _radials.ClearOverlay();
            _labels.ClearOverlay();
            _overlayPolyVertices.Clear();

            for (int i = 0; i < _activeCounters.Count; i++)
                _activeCounters[i].PendingOverlayCount = 0;
        }

        private int TotalWritingCountLocked()
            => _markers.WritingCount + _segments.WritingCount + _polys.WritingCount + _radials.WritingCount + _labels.WritingCount;

        private int TotalOverlayCountLocked()
            => _markers.OverlayCount + _segments.OverlayCount + _polys.OverlayCount + _radials.OverlayCount + _labels.OverlayCount;

        private int TotalTimedCountLocked()
            => _markers.TimedCount + _segments.TimedCount + _polys.TimedCount + _radials.TimedCount + _labels.TimedCount;

        private int GetCommandLimit() => Math.Clamp(_settings.MaxCommandsPerFrame, 128, 32768);

        private ChannelCounter EnsureChannelLocked(string channel)
        {
            ushort id = _settings.GetOrRegisterChannel(channel);
            while (_countersById.Count <= id)
                _countersById.Add(null);

            ChannelCounter? counter = _countersById[id];
            if (counter is null)
            {
                string canonicalName = _settings.TryGetChannelName(id) ?? channel;
                counter = new ChannelCounter(id, canonicalName);
                _countersById[id] = counter;
                _activeCounters.Add(counter);
                _channelListDirty = true;
            }

            return counter;
        }

        private void DecrementChannelTimedLocked(ushort channelId)
        {
            if (channelId < _countersById.Count && _countersById[channelId] is { TimedCount: > 0 } counter)
                counter.TimedCount--;
        }

        private static GizmoMeta CreateMeta(ushort channelId, Color color, float thickness, float safeDuration) => new()
        {
            ChannelId = channelId,
            Color = color,
            Thickness = float.IsFinite(thickness) ? MathF.Max(0.5f, thickness) : 1.5f,
            RemainingDuration = safeDuration,
            TotalDuration = safeDuration
        };

        private static bool IsFinite(Vector2 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y);

        private static float SanitizeDuration(float duration)
            => float.IsFinite(duration) && duration > 0f ? duration : 0f;

        private static string NormalizeChannel(string? channel)
            => string.IsNullOrWhiteSpace(channel) ? DebugGizmos.DefaultChannel : channel.Trim();
    }
}
#endif
