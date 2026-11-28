#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Apotheosis.Generator.ParticleAnalyzer
{
    /// <summary>编译期模块化粒子发射器校验分析器：基于模块声明特性静态校验基础核心模块、姿态互斥、前置依赖、重复提供与渲染模块。</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ParticleEmitterAnalyzer : DiagnosticAnalyzer
    {
        private const string Category = "Apotheosis.Particles";

        public static readonly DiagnosticDescriptor MissingCoreModuleRule = new("APOT101", "粒子发射器缺少核心模块",
            "粒子发射器缺少必需的基础初始化模块（缺失组件：{0}）", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor ConflictingPoseModuleRule = new("APOT102", "粒子发射器姿态模块互斥",
            "粒子发射器不能同时挂载提供 ParticlePose2D 与 ParticlePose3D 的模块", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor MissingPoseModuleRule = new("APOT103", "粒子发射器缺少姿态模块",
            "粒子发射器必须挂载提供 ParticlePose2D 或 ParticlePose3D 之一的初始化模块", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor MissingDependencyRule = new("APOT104", "发射器模块缺少前置组件依赖",
            "模块 '{0}' 依赖组件 {1}，但当前发射器未挂载提供该组件的模块", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DuplicateProviderRule = new("APOT105", "发射器模块重复提供组件",
            "组件 '{0}' 被多个初始化模块重复提供，可能导致初始化数据被相互覆盖", Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor MissingRenderModuleRule = new("APOT106", "粒子发射器未挂载渲染模块",
            "粒子发射器未挂载任何提供 ParticleRenderBase 的渲染模块，生成的粒子将不会渲染显示", Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
            MissingCoreModuleRule, ConflictingPoseModuleRule, MissingPoseModuleRule,
            MissingDependencyRule, DuplicateProviderRule, MissingRenderModuleRule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext startContext)
        {
            Compilation compilation = startContext.Compilation;

            INamedTypeSymbol? emitterType = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Emitters.ParticleEmitter");
            INamedTypeSymbol? customModuleType = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Emitters.Modules.CustomSpawnModule");
            INamedTypeSymbol? providesAttr = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Emitters.Modules.ProvidesComponentsAttribute");
            INamedTypeSymbol? requiresAttr = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Emitters.Modules.RequiresComponentsAttribute");
            INamedTypeSymbol? requiresAnyAttr = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Emitters.Modules.RequiresAnyComponentAttribute");

            INamedTypeSymbol? lifeType = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Components.ParticleLife");
            INamedTypeSymbol? spatialType = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Components.ParticleSpatial");
            INamedTypeSymbol? pose2DType = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Components.ParticlePose2D");
            INamedTypeSymbol? pose3DType = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Components.ParticlePose3D");
            INamedTypeSymbol? renderBaseType = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Components.ParticleRenderBase");

            if (emitterType is null || providesAttr is null || requiresAttr is null || requiresAnyAttr is null
                || lifeType is null || spatialType is null || pose2DType is null || pose3DType is null || renderBaseType is null)
            {
                return;
            }

            startContext.RegisterOperationAction(opContext =>
            {
                var invocation = (IInvocationOperation)opContext.Operation;
                IMethodSymbol method = invocation.TargetMethod;
                if (method.Name != "Bake" || method.IsStatic || !SymbolEqualityComparer.Default.Equals(method.ContainingType, emitterType))
                    return;

                if (SymbolEqualityComparer.Default.Equals(opContext.ContainingSymbol.ContainingType, emitterType))
                    return;

                var modules = new List<CollectedModule>();
                if (!TryCollectChain(invocation.Instance, emitterType, customModuleType, providesAttr, modules))
                    return;

                var provided = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                var duplicateReported = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                var allRequirements = new List<RequiredAllEntry>();
                var anyRequirements = new List<RequiredAnyEntry>();

                for (int i = 0; i < modules.Count; i++)
                {
                    CollectedModule entry = modules[i];
                    if (entry.IsEmptyCustomModule)
                        continue;

                    CollectModuleMetadata(entry.ModuleType, entry.Location, providesAttr, requiresAttr, requiresAnyAttr,
                        provided, duplicateReported, allRequirements, anyRequirements, opContext);
                }

                Location bakeLoc = invocation.Syntax.GetLocation();
                bool hasLife = provided.Contains(lifeType);
                bool hasSpatial = provided.Contains(spatialType);
                bool hasPose2D = provided.Contains(pose2DType);
                bool hasPose3D = provided.Contains(pose3DType);
                bool hasRender = provided.Contains(renderBaseType);

                if (!hasLife || !hasSpatial)
                {
                    string missing = (!hasLife && !hasSpatial)
                        ? "ParticleLife, ParticleSpatial"
                        : (!hasLife ? "ParticleLife" : "ParticleSpatial");
                    opContext.ReportDiagnostic(Diagnostic.Create(MissingCoreModuleRule, bakeLoc, missing));
                }

                if (hasPose2D && hasPose3D)
                    opContext.ReportDiagnostic(Diagnostic.Create(ConflictingPoseModuleRule, bakeLoc));
                else if (!hasPose2D && !hasPose3D)
                    opContext.ReportDiagnostic(Diagnostic.Create(MissingPoseModuleRule, bakeLoc));

                if (!hasRender)
                    opContext.ReportDiagnostic(Diagnostic.Create(MissingRenderModuleRule, bakeLoc));

                for (int i = 0; i < allRequirements.Count; i++)
                {
                    RequiredAllEntry req = allRequirements[i];
                    if (!provided.Contains(req.RequiredComponent))
                        opContext.ReportDiagnostic(Diagnostic.Create(MissingDependencyRule, req.Location, req.ModuleName, req.RequiredComponent.Name));
                }

                for (int i = 0; i < anyRequirements.Count; i++)
                {
                    RequiredAnyEntry req = anyRequirements[i];
                    bool satisfied = false;
                    for (int c = 0; c < req.Candidates.Length; c++)
                    {
                        if (provided.Contains(req.Candidates[c]))
                        {
                            satisfied = true;
                            break;
                        }
                    }

                    if (!satisfied)
                    {
                        string names = JoinTypeNames(req.Candidates);
                        opContext.ReportDiagnostic(Diagnostic.Create(MissingDependencyRule, req.Location, req.ModuleName, names));
                    }
                }
            }, OperationKind.Invocation);
        }

        private static bool TryCollectChain(IOperation? op, INamedTypeSymbol emitterType, INamedTypeSymbol? customModuleType,
            INamedTypeSymbol providesAttr, List<CollectedModule> modules)
        {
            op = UnwrapConversion(op);
            if (op is not IInvocationOperation inv)
                return false;

            IMethodSymbol method = inv.TargetMethod;
            if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, emitterType))
                return false;

            if (method.IsStatic && method.Name == "Create")
                return true;

            if (inv.Instance is null || !TryCollectChain(inv.Instance, emitterType, customModuleType, providesAttr, modules))
                return false;

            switch (method.Name)
            {
                case "WithSchedule":
                case "WithDuration":
                case "WithStartDelay":
                case "WithLimits":
                case "WithRate":
                case "WithBurst":
                case "AddSpawnRule":
                    return true;

                case "AddModule" when inv.Arguments.Length == 1:
                {
                    IOperation? modOp = UnwrapConversion(inv.Arguments[0].Value);
                    if (modOp is null || modOp.Type is not INamedTypeSymbol modType)
                        return false;

                    // 接口、抽象类、错误类型、泛型模块或非 sealed 基类变量无法静态确定运行期精确类型，保守放行避免假阳性
                    if (modType.IsAbstract || modType.TypeKind == TypeKind.Interface || modType.TypeKind == TypeKind.Error
                        || modType.IsGenericType || (modOp is not IObjectCreationOperation && !modType.IsSealed))
                    {
                        return false;
                    }

                    Location argLocation = inv.Arguments[0].Syntax.GetLocation();

                    // CustomSpawnModule 仅在未传额外组件时可静态精确判定；传入非默认 extraComponents 时保守放行
                    if (customModuleType is not null && SymbolEqualityComparer.Default.Equals(modType, customModuleType))
                    {
                        if (!IsEmptyCustomSpawnModuleCreation(modOp))
                            return false;

                        modules.Add(new CollectedModule(modType, argLocation, isEmptyCustomModule: true));
                        return true;
                    }

                    // 若外部自定义模块未贴 [ProvidesComponents] 特性，无法确定它提供的组件，保守放行避免误报
                    if (!HasProvidesAttribute(modType, providesAttr))
                        return false;

                    modules.Add(new CollectedModule(modType, argLocation, isEmptyCustomModule: false));
                    return true;
                }

                default:
                    return false;
            }
        }

        private static bool IsEmptyCustomSpawnModuleCreation(IOperation modOp)
        {
            if (modOp is not IObjectCreationOperation creation)
                return false;

            for (int i = 0; i < creation.Arguments.Length; i++)
            {
                IArgumentOperation arg = creation.Arguments[i];
                if (arg.Parameter?.Name != "extraComponents")
                    continue;

                if (arg.ArgumentKind == ArgumentKind.DefaultValue)
                    return true;

                IOperation? val = UnwrapConversion(arg.Value);
                return val is IDefaultValueOperation
                    || (val is IObjectCreationOperation emptyFrag && emptyFrag.Arguments.Length == 0);
            }

            return true;
        }

        private static bool HasProvidesAttribute(INamedTypeSymbol moduleType, INamedTypeSymbol providesAttr)
        {
            for (INamedTypeSymbol? cur = moduleType; cur is not null; cur = cur.BaseType)
            {
                ImmutableArray<AttributeData> attrs = cur.GetAttributes();
                for (int i = 0; i < attrs.Length; i++)
                {
                    if (SymbolEqualityComparer.Default.Equals(attrs[i].AttributeClass, providesAttr))
                        return true;
                }
            }

            return false;
        }

        private static void CollectModuleMetadata(INamedTypeSymbol moduleType, Location location,
            INamedTypeSymbol providesAttr, INamedTypeSymbol requiresAttr, INamedTypeSymbol requiresAnyAttr,
            HashSet<ITypeSymbol> provided, HashSet<ITypeSymbol> duplicateReported,
            List<RequiredAllEntry> allRequirements, List<RequiredAnyEntry> anyRequirements, OperationAnalysisContext opContext)
        {
            string moduleName = moduleType.Name;
            // 先在单模块局部集合去重，防止派生类重申基类特性时对单个模块误报 APOT105 或重复 APOT104
            var moduleProvided = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            var moduleRequiredAll = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

            for (INamedTypeSymbol? cur = moduleType; cur is not null; cur = cur.BaseType)
            {
                ImmutableArray<AttributeData> attrs = cur.GetAttributes();
                for (int i = 0; i < attrs.Length; i++)
                {
                    AttributeData attr = attrs[i];
                    if (attr.ConstructorArguments.Length != 1)
                        continue;

                    TypedConstant arg = attr.ConstructorArguments[0];
                    if (arg.Kind != TypedConstantKind.Array || arg.Values.IsDefaultOrEmpty)
                        continue;

                    if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, providesAttr))
                    {
                        foreach (TypedConstant item in arg.Values)
                        {
                            if (item.Value is ITypeSymbol compType && IsConcreteType(compType))
                                moduleProvided.Add(compType);
                        }
                    }
                    else if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, requiresAttr))
                    {
                        foreach (TypedConstant item in arg.Values)
                        {
                            if (item.Value is ITypeSymbol compType && IsConcreteType(compType) && moduleRequiredAll.Add(compType))
                                allRequirements.Add(new RequiredAllEntry(moduleName, compType, location));
                        }
                    }
                    else if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, requiresAnyAttr))
                    {
                        var candidates = ImmutableArray.CreateBuilder<ITypeSymbol>(arg.Values.Length);
                        foreach (TypedConstant item in arg.Values)
                        {
                            if (item.Value is ITypeSymbol compType && IsConcreteType(compType))
                                candidates.Add(compType);
                        }

                        if (candidates.Count > 0)
                            anyRequirements.Add(new RequiredAnyEntry(moduleName, candidates.ToImmutable(), location));
                    }
                }
            }

            foreach (ITypeSymbol compType in moduleProvided)
            {
                if (!provided.Add(compType) && duplicateReported.Add(compType))
                    opContext.ReportDiagnostic(Diagnostic.Create(DuplicateProviderRule, location, compType.Name));
            }
        }

        private static string JoinTypeNames(ImmutableArray<ITypeSymbol> types)
        {
            if (types.Length == 1)
                return types[0].Name;

            var names = new string[types.Length];
            for (int i = 0; i < types.Length; i++)
                names[i] = types[i].Name;
            return string.Join(" 或 ", names);
        }

        private static bool IsConcreteType(ITypeSymbol type)
            => type is not ITypeParameterSymbol && type is not IErrorTypeSymbol && type.TypeKind != TypeKind.Error;

        private static IOperation? UnwrapConversion(IOperation? op)
        {
            while (op is IConversionOperation conv)
                op = conv.Operand;
            return op;
        }

        private readonly struct CollectedModule
        {
            public readonly INamedTypeSymbol ModuleType;
            public readonly Location Location;
            public readonly bool IsEmptyCustomModule;

            public CollectedModule(INamedTypeSymbol moduleType, Location location, bool isEmptyCustomModule)
            {
                ModuleType = moduleType;
                Location = location;
                IsEmptyCustomModule = isEmptyCustomModule;
            }
        }

        private readonly struct RequiredAllEntry
        {
            public readonly string ModuleName;
            public readonly ITypeSymbol RequiredComponent;
            public readonly Location Location;

            public RequiredAllEntry(string moduleName, ITypeSymbol requiredComponent, Location location)
            {
                ModuleName = moduleName;
                RequiredComponent = requiredComponent;
                Location = location;
            }
        }

        private readonly struct RequiredAnyEntry
        {
            public readonly string ModuleName;
            public readonly ImmutableArray<ITypeSymbol> Candidates;
            public readonly Location Location;

            public RequiredAnyEntry(string moduleName, ImmutableArray<ITypeSymbol> candidates, Location location)
            {
                ModuleName = moduleName;
                Candidates = candidates;
                Location = location;
            }
        }
    }
}
