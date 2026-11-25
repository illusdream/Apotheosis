#if DEBUG
#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Terraria.ModLoader;

namespace Apotheosis.DebugTool.Gizmos
{
    /// <summary>世界空间 Gizmos 运行时显示配置、密集频道驻留表与线程安全开关位图。</summary>
    [NoJIT]
    internal sealed class GizmoSettings
    {
        private readonly object _channelLock = new();
        private readonly Dictionary<string, ushort> _nameToId = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _idToName = new();
        private ulong[] _enabledBits = new ulong[2];

        /// <summary>是否开启世界空间 Gizmos 叠层绘制（收起主面板后仍保持生效）。</summary>
        public bool Enabled = false;

        /// <summary>全局不透明度倍率。</summary>
        public float GlobalOpacity = 0.92f;

        /// <summary>全局线宽缩放倍率。</summary>
        public float ThicknessScale = 1.0f;

        /// <summary>是否绘制调试文本标签。</summary>
        public bool ShowLabels = true;

        /// <summary>延时留存图元在最后 25% 寿命内是否平滑淡出。</summary>
        public bool FadeTimedGizmos = true;

        /// <summary>单帧图元数量上限（保护渲染性能与顶点缓冲）。</summary>
        public int MaxCommandsPerFrame = 4096;

        /// <summary>纯几何图元自检 Demo 开关（围绕本地玩家绘制全套图元，用于验收对齐与缩放）。</summary>
        public bool ShowSelfTestDemo = false;

        /// <summary>获取或注册频道名对应的紧凑 2 字节密集 ID（首次注册默认启用）。</summary>
        public ushort GetOrRegisterChannel(string channel)
        {
            lock (_channelLock)
                return GetOrRegisterChannelLocked(channel);
        }

        /// <summary>按密集 ID 获取频道显示名。</summary>
        public string? TryGetChannelName(ushort channelId)
        {
            lock (_channelLock)
                return channelId < _idToName.Count ? _idToName[channelId] : null;
        }

        /// <summary>查询指定频道是否启用（首次出现的频道默认为启用）。</summary>
        public bool IsChannelEnabled(string channel)
        {
            lock (_channelLock)
            {
                ushort id = GetOrRegisterChannelLocked(channel);
                return GetBitLocked(id);
            }
        }

        /// <summary>查询指定密集 ID 频道是否启用。</summary>
        public bool IsChannelEnabled(ushort channelId)
        {
            lock (_channelLock)
                return channelId < _idToName.Count ? GetBitLocked(channelId) : true;
        }

        /// <summary>设置指定频道启用状态。</summary>
        public void SetChannelEnabled(string channel, bool enabled)
        {
            lock (_channelLock)
            {
                ushort id = GetOrRegisterChannelLocked(channel);
                SetBitLocked(id, enabled);
            }
        }

        /// <summary>批量设置全部已记录频道的启用状态（O(频道数/64) 位段写入）。</summary>
        public void SetAllChannels(bool enabled)
        {
            lock (_channelLock)
            {
                int count = _idToName.Count;
                if (count == 0)
                    return;

                int words = (count + 63) >> 6;
                ulong fill = enabled ? ulong.MaxValue : 0UL;
                for (int i = 0; i < words; i++)
                    _enabledBits[i] = fill;
            }
        }

        /// <summary>单次加锁拷贝当前频道开关位图快照，供渲染热路径无锁 O(1) 位判定。</summary>
        public void CopyEnabledBitsSnapshot(ref ulong[] destination, out int wordCount)
        {
            lock (_channelLock)
            {
                int count = _idToName.Count;
                wordCount = (count + 63) >> 6;
                if (wordCount == 0)
                    return;

                if (destination.Length < wordCount)
                    destination = new ulong[Math.Max(wordCount, destination.Length * 2)];

                Array.Copy(_enabledBits, destination, wordCount);
            }
        }

        /// <summary>在帧快照位图中无锁 O(1) 判定指定密集 ID 频道是否启用。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsEnabledInSnapshot(ulong[] bits, int wordCount, ushort channelId)
        {
            int word = channelId >> 6;
            if ((uint)word >= (uint)wordCount)
                return true;

            return (bits[word] & (1UL << (channelId & 63))) != 0;
        }

        /// <summary>将所有已注册频道开关重置为默认启用（保留会话期密集 ID 映射，避免与命令缓冲失步）。</summary>
        public void ClearChannels() => SetAllChannels(true);

        private ushort GetOrRegisterChannelLocked(string channel)
        {
            if (_nameToId.TryGetValue(channel, out ushort existing))
                return existing;

            int next = _idToName.Count;
            if (next > ushort.MaxValue)
                return ushort.MaxValue;

            ushort id = (ushort)next;
            _idToName.Add(channel);
            _nameToId[channel] = id;

            int word = id >> 6;
            if (word >= _enabledBits.Length)
                Array.Resize(ref _enabledBits, Math.Max(word + 1, _enabledBits.Length * 2));

            SetBitLocked(id, true);
            return id;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool GetBitLocked(ushort id) => (_enabledBits[id >> 6] & (1UL << (id & 63))) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetBitLocked(ushort id, bool enabled)
        {
            int word = id >> 6;
            ulong mask = 1UL << (id & 63);
            if (enabled)
                _enabledBits[word] |= mask;
            else
                _enabledBits[word] &= ~mask;
        }
    }
}
#endif
