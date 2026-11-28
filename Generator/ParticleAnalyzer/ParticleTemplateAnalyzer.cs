#nullable enable

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Apotheosis.Generator.ParticleAnalyzer
{
    /// <summary>编译期粒子模板布局校验分析器：静态拦截姿态组件互斥、缺失姿态及缺失核心组件。</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ParticleTemplateAnalyzer : DiagnosticAnalyzer
    {
        private const string Category = "Apotheosis.Particles";

        public static readonly DiagnosticDescriptor ConflictingPoseRule = new("APOT001", "粒子模板姿态组件互斥",
            "粒子模板不能同时包含 ParticlePose2D 与 ParticlePose3D", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor MissingPoseRule = new("APOT002", "粒子模板缺少姿态组件",
            "粒子模板必须包含 ParticlePose2D 或 ParticlePose3D 之一", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor MissingCoreRule = new("APOT003", "粒子模板缺少核心组件",
            "粒子模板缺少必需的核心组件：{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(ConflictingPoseRule, MissingPoseRule, MissingCoreRule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext startContext)
        {
            Compilation compilation = startContext.Compilation;

            INamedTypeSymbol? templateType = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Fragments.ParticleTemplate");
            INamedTypeSymbol? fragmentType = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Fragments.ParticleFragment");
            INamedTypeSymbol? lifeType = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Components.ParticleLife");
            INamedTypeSymbol? spatialType = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Components.ParticleSpatial");
            INamedTypeSymbol? pose2DType = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Components.ParticlePose2D");
            INamedTypeSymbol? pose3DType = compilation.GetTypeByMetadataName("Apotheosis.Runtime.Particles.Components.ParticlePose3D");

            if (templateType is null || fragmentType is null || lifeType is null || spatialType is null || pose2DType is null || pose3DType is null)
                return;

            var resolver = new LayoutResolver(compilation, templateType, fragmentType);

            startContext.RegisterOperationAction(opContext =>
            {
                var invocation = (IInvocationOperation)opContext.Operation;
                IMethodSymbol method = invocation.TargetMethod;
                if (method.Name != "Bake" || !SymbolEqualityComparer.Default.Equals(method.ContainingType, templateType))
                    return;

                // 跳过 ParticleTemplate 内部自身实现（如静态 Bake 内部转发至实例 Bake）
                if (SymbolEqualityComparer.Default.Equals(opContext.ContainingSymbol.ContainingType, templateType))
                    return;

                var components = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                CancellationToken ct = opContext.CancellationToken;
                bool resolved;

                if (method.IsStatic && invocation.Arguments.Length == 2)
                {
                    // 按形参序号（Ordinal == 1）查找 layout 实参，兼容命名参数乱序调用
                    IArgumentOperation? layoutArg = FindArgumentByOrdinal(invocation.Arguments, 1);
                    if (layoutArg is null)
                        return;
                    resolved = resolver.TryCollectFragment(layoutArg.Value, components, ct);
                }
                else if (!method.IsStatic && invocation.Instance is not null)
                {
                    resolved = resolver.TryCollectTemplate(invocation.Instance, components, ct);
                }
                else
                {
                    return;
                }

                // 遇到运行时动态分支或开放泛型参数等无法静态推导的写法，保守放行避免误报
                if (!resolved)
                    return;

                Location location = invocation.Syntax.GetLocation();
                bool hasLife = components.Contains(lifeType);
                bool hasSpatial = components.Contains(spatialType);
                bool hasPose2D = components.Contains(pose2DType);
                bool hasPose3D = components.Contains(pose3DType);

                if (hasPose2D && hasPose3D)
                    opContext.ReportDiagnostic(Diagnostic.Create(ConflictingPoseRule, location));
                else if (!hasPose2D && !hasPose3D)
                    opContext.ReportDiagnostic(Diagnostic.Create(MissingPoseRule, location));

                if (!hasLife || !hasSpatial)
                {
                    string missing = (!hasLife && !hasSpatial)
                        ? "ParticleLife, ParticleSpatial"
                        : (!hasLife ? "ParticleLife" : "ParticleSpatial");
                    opContext.ReportDiagnostic(Diagnostic.Create(MissingCoreRule, location, missing));
                }
            }, OperationKind.Invocation);
        }

        private static IArgumentOperation? FindArgumentByOrdinal(ImmutableArray<IArgumentOperation> arguments, int ordinal)
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                if (arguments[i].Parameter?.Ordinal == ordinal)
                    return arguments[i];
            }

            return null;
        }

        private sealed class LayoutResolver
        {
            private readonly Compilation _compilation;
            private readonly INamedTypeSymbol _templateType;
            private readonly INamedTypeSymbol _fragmentType;
            private readonly ConcurrentDictionary<ISymbol, ImmutableArray<ITypeSymbol>?> _symbolCache = new(SymbolEqualityComparer.Default);

            public LayoutResolver(Compilation compilation, INamedTypeSymbol templateType, INamedTypeSymbol fragmentType)
            {
                _compilation = compilation;
                _templateType = templateType;
                _fragmentType = fragmentType;
            }

            public bool TryCollectTemplate(IOperation? op, HashSet<ITypeSymbol> components, CancellationToken ct)
            {
                op = UnwrapConversion(op);
                if (op is not IInvocationOperation inv)
                    return false;

                IMethodSymbol method = inv.TargetMethod;
                if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, _templateType))
                    return false;

                if (method.IsStatic && method.Name == "Create")
                    return true;

                if (inv.Instance is null || !TryCollectTemplate(inv.Instance, components, ct))
                    return false;

                switch (method.Name)
                {
                    case "Add" when inv.Arguments.Length == 1:
                    {
                        // 必须先在独立集合中求值入参 Fragment，防止入参内的 Without<T>() 误删模板先前已添加的组件
                        var fragSet = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                        if (!TryCollectFragment(inv.Arguments[0].Value, fragSet, ct))
                            return false;
                        components.UnionWith(fragSet);
                        return true;
                    }

                    case "With" when method.TypeArguments.Length == 1:
                        if (!IsConcreteType(method.TypeArguments[0]))
                            return false;
                        components.Add(method.TypeArguments[0]);
                        return true;

                    case "Without" when method.TypeArguments.Length == 1:
                        if (!IsConcreteType(method.TypeArguments[0]))
                            return false;
                        components.Remove(method.TypeArguments[0]);
                        return true;

                    default:
                        // 嵌套 .Bake().Bake() 或未知方法一律保守放行，避免重复报错或掩盖运行时 EnsureMutable 异常
                        return false;
                }
            }

            public bool TryCollectFragment(IOperation? op, HashSet<ITypeSymbol> components, CancellationToken ct)
                => TryCollectFragmentCore(op, components, new HashSet<ISymbol>(SymbolEqualityComparer.Default), ct);

            private bool TryCollectFragmentCore(IOperation? op, HashSet<ITypeSymbol> components, HashSet<ISymbol> visiting, CancellationToken ct)
            {
                op = UnwrapConversion(op);
                if (op is null)
                    return false;

                switch (op)
                {
                    case IDefaultValueOperation:
                        return true;

                    case IObjectCreationOperation creation when SymbolEqualityComparer.Default.Equals(creation.Type, _fragmentType) && creation.Arguments.Length == 0:
                        return true;

                    case IBinaryOperation binary when binary.OperatorMethod is not null
                        && SymbolEqualityComparer.Default.Equals(binary.OperatorMethod.ContainingType, _fragmentType)
                        && (binary.OperatorKind == BinaryOperatorKind.Or || binary.OperatorKind == BinaryOperatorKind.Add):
                    {
                        var leftSet = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                        var rightSet = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                        if (!TryCollectFragmentCore(binary.LeftOperand, leftSet, visiting, ct) || !TryCollectFragmentCore(binary.RightOperand, rightSet, visiting, ct))
                            return false;

                        components.UnionWith(leftSet);
                        components.UnionWith(rightSet);
                        return true;
                    }

                    case IInvocationOperation inv when SymbolEqualityComparer.Default.Equals(inv.TargetMethod.ContainingType, _fragmentType):
                    {
                        IMethodSymbol method = inv.TargetMethod;
                        if (method.IsStatic && method.Name == "From")
                        {
                            for (int i = 0; i < method.TypeArguments.Length; i++)
                            {
                                if (!IsConcreteType(method.TypeArguments[i]))
                                    return false;
                                components.Add(method.TypeArguments[i]);
                            }

                            return true;
                        }

                        if (inv.Instance is null || !TryCollectFragmentCore(inv.Instance, components, visiting, ct))
                            return false;

                        switch (method.Name)
                        {
                            case "With" when method.TypeArguments.Length == 1:
                                if (!IsConcreteType(method.TypeArguments[0]))
                                    return false;
                                components.Add(method.TypeArguments[0]);
                                return true;

                            case "Without" when method.TypeArguments.Length == 1:
                                if (!IsConcreteType(method.TypeArguments[0]))
                                    return false;
                                components.Remove(method.TypeArguments[0]);
                                return true;

                            case "Combine" when inv.Arguments.Length == 1:
                            {
                                var otherSet = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                                if (!TryCollectFragmentCore(inv.Arguments[0].Value, otherSet, visiting, ct))
                                    return false;
                                components.UnionWith(otherSet);
                                return true;
                            }

                            default:
                                return false;
                        }
                    }

                    case IFieldReferenceOperation fieldRef when fieldRef.Field.IsStatic && fieldRef.Field.IsReadOnly
                        && SymbolEqualityComparer.Default.Equals(fieldRef.Field.Type, _fragmentType):
                        // 若所属类型定义了显式静态构造函数，static readonly 字段可能在其中被动态重赋值，保守放行
                        if (HasExplicitStaticConstructor(fieldRef.Field.ContainingType))
                            return false;
                        return TryResolveReadOnlyMember(fieldRef.Field, components, visiting, ct);

                    case IPropertyReferenceOperation propRef when propRef.Property.IsStatic && propRef.Property.IsReadOnly
                        && !propRef.Property.IsVirtual && !propRef.Property.IsOverride && !propRef.Property.IsAbstract
                        && SymbolEqualityComparer.Default.Equals(propRef.Property.Type, _fragmentType):
                        return TryResolveReadOnlyMember(propRef.Property, components, visiting, ct);

                    default:
                        return false;
                }
            }

            private bool TryResolveReadOnlyMember(ISymbol member, HashSet<ITypeSymbol> components, HashSet<ISymbol> visiting, CancellationToken ct)
            {
                if (_symbolCache.TryGetValue(member, out ImmutableArray<ITypeSymbol>? cached))
                {
                    if (!cached.HasValue)
                        return false;

                    foreach (ITypeSymbol t in cached.Value)
                        components.Add(t);
                    return true;
                }

                // 防止静态字段/属性互相引用造成无限递归
                if (!visiting.Add(member))
                    return false;

                try
                {
                    if (member.DeclaringSyntaxReferences.Length == 0)
                    {
                        _symbolCache[member] = null;
                        return false;
                    }

                    SyntaxReference syntaxRef = member.DeclaringSyntaxReferences[0];
                    // 跨项目引用的语法树不属于当前 Compilation，直接 GetSemanticModel 会抛 ArgumentException (AD0001)
                    if (!_compilation.ContainsSyntaxTree(syntaxRef.SyntaxTree))
                    {
                        _symbolCache[member] = null;
                        return false;
                    }

                    SyntaxNode syntax = syntaxRef.GetSyntax(ct);
                    ExpressionSyntax? initExpr = ExtractInitializerExpression(syntax, member.ContainingType);
                    if (initExpr is null)
                    {
                        _symbolCache[member] = null;
                        return false;
                    }

                    SemanticModel model = _compilation.GetSemanticModel(syntaxRef.SyntaxTree);
                    IOperation? initOp = model.GetOperation(initExpr, ct);
                    var resolvedSet = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                    if (!TryCollectFragmentCore(initOp, resolvedSet, visiting, ct))
                    {
                        _symbolCache[member] = null;
                        return false;
                    }

                    ImmutableArray<ITypeSymbol> array = resolvedSet.ToImmutableArray();
                    _symbolCache[member] = array;
                    foreach (ITypeSymbol t in array)
                        components.Add(t);
                    return true;
                }
                finally
                {
                    visiting.Remove(member);
                }
            }

            private static ExpressionSyntax? ExtractInitializerExpression(SyntaxNode syntax, INamedTypeSymbol? containingType)
            {
                switch (syntax)
                {
                    case VariableDeclaratorSyntax v:
                        return v.Initializer?.Value;

                    case PropertyDeclarationSyntax p:
                        if (p.ExpressionBody is not null)
                            return p.ExpressionBody.Expression;
                        if (p.Initializer is not null)
                        {
                            // 带初始化器的只读自动属性可在显式静态构造函数中被重赋值，存在时保守放行防误报
                            return HasExplicitStaticConstructor(containingType) ? null : p.Initializer.Value;
                        }
                        if (p.AccessorList is not null)
                        {
                            foreach (AccessorDeclarationSyntax acc in p.AccessorList.Accessors)
                            {
                                if (acc.IsKind(SyntaxKind.GetAccessorDeclaration) && acc.ExpressionBody is not null)
                                    return acc.ExpressionBody.Expression;
                            }
                        }
                        return null;

                    default:
                        return null;
                }
            }

            private static bool IsConcreteType(ITypeSymbol type)
                => type is not ITypeParameterSymbol && type is not IErrorTypeSymbol && type.TypeKind != TypeKind.Error;

            private static bool HasExplicitStaticConstructor(INamedTypeSymbol? containingType)
            {
                if (containingType is null)
                    return false;

                ImmutableArray<IMethodSymbol> ctors = containingType.StaticConstructors;
                for (int i = 0; i < ctors.Length; i++)
                {
                    if (!ctors[i].IsImplicitlyDeclared)
                        return true;
                }

                return false;
            }

            private static IOperation? UnwrapConversion(IOperation? op)
            {
                while (op is IConversionOperation conv)
                    op = conv.Operand;
                return op;
            }
        }
    }
}
