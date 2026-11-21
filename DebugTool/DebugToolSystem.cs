#if DEBUG
#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Apotheosis.DebugTool.Core;
using Terraria;
using Terraria.ModLoader;

namespace Apotheosis
{
    public partial class Apotheosis
    {
        partial void ConfigureDebugTool()
        {
            PreJITFilter = new DebugTool.DebugToolPreJitFilter();
        }
    }
}

namespace Apotheosis.DebugTool
{
    /// <summary>
    /// 跳过 <c>Apotheosis.DebugTool.*</c> 命名空间下所有类型（含编译器生成的闭包/委托缓存嵌套类）的加载期 Pre-JIT，
    /// 确保在 <see cref="DebugToolSystem.Load"/> 注册 <c>Hexa.NET.ImGui</c> 解析器之后再按需 JIT。
    /// </summary>
    internal sealed class DebugToolPreJitFilter : PreJITFilter
    {
        public override bool ShouldJIT(MemberInfo member)
        {
            Type? type = member as Type ?? member.DeclaringType;
            while (type is not null)
            {
                if (type.Namespace is not null &&
                    type.Namespace.StartsWith("Apotheosis.DebugTool", StringComparison.Ordinal))
                {
                    return false;
                }

                type = type.DeclaringType;
            }

            return base.ShouldJIT(member);
        }
    }

    /// <summary>
    /// 仅在 <c>Development</c> 模式下生效的 ImGui 调试工具唯一 <see cref="ModSystem"/> 入口。
    /// <para>
    /// 本类字段与方法签名中完全不出现任何 ImGui 类型，先向当前 tModLoader <see cref="AssemblyLoadContext"/>
    /// 注册 <c>Hexa.NET.ImGui</c> / <c>HexaGen.Runtime</c> 的动态解析器，再以非内联方式启动 <see cref="DebugToolRuntime"/>，
    /// 从而在单项目架构下避免加载期 <c>ReflectionTypeLoadException</c> 与 <c>JITException</c>。
    /// </para>
    /// </summary>
    internal sealed class DebugToolSystem : ModSystem
    {
        private AssemblyLoadContext? _modAlc;
        private string? _binDir;
        private bool _runtimeActive;

        public override void Load()
        {
            if (Main.dedServ)
                return;

            Assembly modAssembly = typeof(DebugToolSystem).Assembly;
            _binDir = ResolveDevBinDirectory(modAssembly);

            if (string.IsNullOrEmpty(_binDir) || !Directory.Exists(_binDir))
            {
                Mod.Logger.Warn($"[Apotheosis.DebugTool] Dev bin directory not found ('{_binDir}'). Debug panel disabled.");
                return;
            }

            _modAlc = AssemblyLoadContext.GetLoadContext(modAssembly);
            if (_modAlc is not null)
                _modAlc.Resolving += OnResolveManagedAssembly;

            try
            {
                StartRuntimeNoInline(_binDir);
                _runtimeActive = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                CleanupResolver();
                throw;
            }
            catch (Exception ex)
            {
                Mod.Logger.Error("[Apotheosis.DebugTool] Failed to start DebugTool runtime.", ex);
                CleanupResolver();
            }
        }

        public override void PostUpdateInput()
        {
            if (!_runtimeActive)
                return;

            ForwardPostUpdateInputNoInline();
        }

        public override void OnWorldUnload()
        {
            if (!_runtimeActive)
                return;

            ForwardOnWorldUnloadNoInline();
        }

        public override void Unload()
        {
            if (_runtimeActive)
            {
                try
                {
                    StopRuntimeNoInline();
                }
                catch (Exception ex)
                {
                    Mod?.Logger.Warn($"[Apotheosis.DebugTool] Unload warning: {ex.Message}");
                }

                _runtimeActive = false;
            }

            CleanupResolver();
        }

        [NoJIT]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void StartRuntimeNoInline(string binDir)
        {
            DebugToolRuntime.Initialize(binDir, Mod.Logger);
        }

        [NoJIT]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ForwardPostUpdateInputNoInline()
        {
            DebugToolRuntime.OnPostUpdateInput();
        }

        [NoJIT]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ForwardOnWorldUnloadNoInline()
        {
            DebugToolRuntime.OnWorldUnload();
        }

        [NoJIT]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StopRuntimeNoInline()
        {
            DebugToolRuntime.Shutdown();
        }

        private Assembly? OnResolveManagedAssembly(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            if (string.IsNullOrEmpty(_binDir) || string.IsNullOrEmpty(assemblyName.Name))
                return null;

            if (!string.Equals(assemblyName.Name, "Hexa.NET.ImGui", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(assemblyName.Name, "HexaGen.Runtime", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string candidatePath = Path.Combine(_binDir, assemblyName.Name + ".dll");
            return File.Exists(candidatePath) ? context.LoadFromAssemblyPath(candidatePath) : null;
        }

        private void CleanupResolver()
        {
            if (_modAlc is not null)
            {
                _modAlc.Resolving -= OnResolveManagedAssembly;
                _modAlc = null;
            }

            _binDir = null;
        }

        private static string? ResolveDevBinDirectory(Assembly modAssembly)
        {
            string? fromMetadata = modAssembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => string.Equals(a.Key, "ApotheosisDebugToolBinDir", StringComparison.Ordinal))
                ?.Value;

            if (!string.IsNullOrEmpty(fromMetadata) && Directory.Exists(fromMetadata))
                return fromMetadata;

            if (!string.IsNullOrEmpty(modAssembly.Location))
                return Path.GetDirectoryName(modAssembly.Location);

            return null;
        }
    }
}
#endif
