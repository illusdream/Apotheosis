#if DEBUG
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Terraria.ModLoader;

namespace Apotheosis.DebugTool.Core
{
    /// <summary>
    /// Apotheosis 调试面板功能模块接口。
    /// <para>
    /// 新增模组专属调试窗口时，只需在 <c>DebugTool/Panels/</c>（或任意 <c>#if DEBUG</c> 区域）实现本接口并标注 <see cref="NoJITAttribute"/>，
    /// 启动时将自动发现并注册到左侧竖向窗口选择栏中。
    /// </para>
    /// </summary>
    public interface IDebugPanel
    {
        /// <summary>模块唯一标识符（不可重复）。</summary>
        string Id { get; }

        /// <summary>显示在左侧导航栏与右侧功能区标题的名称（支持中文）。</summary>
        string DisplayName { get; }
        
        /// <summary>同分组内的排序权重，越小越靠上。</summary>
        int Order { get; }

        /// <summary>绘制右侧功能区内容。</summary>
        void OnDraw();

        /// <summary>每帧更新钩子（无论当前是否选中本面板均会调用，可用于采样遥测数据）。</summary>
        void OnUpdate(float deltaSeconds) { }

        /// <summary>模组卸载时的资源清理钩子。</summary>
        void OnUnload() { }
    }

    /// <summary>
    /// 调试面板模块注册表：负责自动发现与按序管理所有 <see cref="IDebugPanel"/> 实例。
    /// </summary>
    [NoJIT]
    internal sealed class DebugPanelRegistry
    {
        private readonly List<IDebugPanel> _panels = new();

        public IReadOnlyList<IDebugPanel> Panels => _panels;

        public void DiscoverAndRegisterBuiltIn()
        {
            var assembly = typeof(DebugPanelRegistry).Assembly;
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface)
                    continue;

                if (!typeof(IDebugPanel).IsAssignableFrom(type))
                    continue;

                var ctor = type.GetConstructor(Type.EmptyTypes);
                if (ctor is null)
                    continue;

                if (ctor.Invoke(null) is IDebugPanel panel)
                    Register(panel);
            }
        }

        public void Register(IDebugPanel panel)
        {
            ArgumentNullException.ThrowIfNull(panel);
            ArgumentException.ThrowIfNullOrWhiteSpace(panel.Id);

            IDebugPanel? existing = _panels.Find(p =>
                string.Equals(p.Id, panel.Id, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                throw new InvalidOperationException(
                    $"Duplicate debug panel Id '{panel.Id}' detected: " +
                    $"'{panel.GetType().FullName}' conflicts with already registered '{existing.GetType().FullName}'.");
            }

            _panels.Add(panel);
            _panels.Sort((a, b) =>
            {
                int orderCmp = a.Order.CompareTo(b.Order);
                return orderCmp != 0
                    ? orderCmp
                    : string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal);
            });
        }

        public void UnloadAll()
        {
            foreach (var panel in _panels)
            {
                try
                {
                    panel.OnUnload();
                }
                catch
                {
                    // Ignore cleanup errors during mod unload
                }
            }

            _panels.Clear();
        }
    }
}
#endif
