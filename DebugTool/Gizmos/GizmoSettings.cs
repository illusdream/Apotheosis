#if DEBUG
#nullable enable

using System;
using System.Collections.Generic;
using Terraria.ModLoader;

namespace Apotheosis.DebugTool.Gizmos
{
    /// <summary>
    /// 世界空间 Gizmos 运行时显示配置与线程安全频道开关状态。
    /// </summary>
    [NoJIT]
    internal sealed class GizmoSettings
    {
        private readonly object _channelLock = new();
        private readonly Dictionary<string, bool> _channelEnabled = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _keyScratch = new();

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

        /// <summary>查询指定频道是否启用（首次出现的频道默认为启用）。</summary>
        public bool IsChannelEnabled(string channel)
        {
            lock (_channelLock)
            {
                if (_channelEnabled.TryGetValue(channel, out bool enabled))
                    return enabled;

                _channelEnabled[channel] = true;
                return true;
            }
        }

        /// <summary>设置指定频道启用状态。</summary>
        public void SetChannelEnabled(string channel, bool enabled)
        {
            lock (_channelLock)
            {
                _channelEnabled[channel] = enabled;
            }
        }

        /// <summary>批量设置已记录频道的启用状态。</summary>
        public void SetAllChannels(bool enabled)
        {
            lock (_channelLock)
            {
                if (_channelEnabled.Count == 0)
                    return;

                _keyScratch.Clear();
                foreach (string key in _channelEnabled.Keys)
                    _keyScratch.Add(key);

                for (int i = 0; i < _keyScratch.Count; i++)
                    _channelEnabled[_keyScratch[i]] = enabled;

                _keyScratch.Clear();
            }
        }

        /// <summary>重置所有频道开关记录。</summary>
        public void ClearChannels()
        {
            lock (_channelLock)
            {
                _channelEnabled.Clear();
                _keyScratch.Clear();
            }
        }
    }
}
#endif
