using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MakoKaraGenerator.Scripting.Compilation;

/// <summary>检查 SDK v2 支持的确定性 C# 子集；不构成安全沙箱或任意程序的终止性证明。</summary>
public static partial class ScriptV2PurityAnalyzer
{
    /// <summary>必须参与 v2 编译产物身份；规则或白名单变化时同步提升。</summary>
    public const string RuleVersion = ScriptCompilationIdentity.V2AnalysisRules;
    private const string SdkAssembly = "MakoKaraGenerator.Scripting.Abstractions";
    private const string V2Namespace = "MakoKaraGenerator.Scripting.V2";
    private static readonly DiagnosticDescriptor Capability = Error("MKV2001", "非固定输入能力", "SDK v2 禁止读取或调用非固定输入能力：{0}");
    private static readonly DiagnosticDescriptor StaticState = Error("MKV2002", "可变静态状态", "SDK v2 禁止可变静态状态：{0}");
    private static readonly DiagnosticDescriptor Mutation = Error("MKV2003", "外部状态写入", "SDK v2 只允许修改本次调用的局部变量和局部集合：{0}");
    private static readonly DiagnosticDescriptor PreparedType = Error("MKV2004", "准备结果不是递归只读", "准备结果必须递归只读：{0}");
    private static readonly DiagnosticDescriptor HandleEscape = Error("MKV2005", "阶段句柄逃逸", "帧、准备上下文和输出句柄只能在当前调用内使用：{0}");
    private static readonly DiagnosticDescriptor Unsupported = Error("MKV2006", "无法证明受支持的调用", "SDK v2 无法证明此调用属于支持的确定性子集：{0}");
    private static readonly DiagnosticDescriptor UnsupportedLanguage = Error("MKV2007", "不支持的执行能力", "SDK v2 禁止 async、unsafe、动态分派和外部代码入口：{0}");
    private static readonly DiagnosticDescriptor Allocation = new("MKV2008", "逐帧分配", "逐帧求值包含分配：{0}", "ScriptV2Performance", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, true);
    private static readonly DiagnosticDescriptor Linq = new("MKV2009", "逐帧 LINQ", "逐帧求值包含 LINQ；枚举和分配计入求值预算：{0}", "ScriptV2Performance", Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, true);

    /// <summary>分析真实 SDK v2 接口的源码实现及其可达 helper、getter、构造器和委托。</summary>
    /// <param name="compilation">使用实际分发参考集建立的编译。</param>
    /// <param name="entryTypes">包的入口元数据名称；空集合时检查全部 v2 源码入口。</param>
    /// <param name="cancellationToken">协作取消，不转换为作者诊断。</param>
    /// <returns>按源码位置和编号稳定排序的错误与性能提示；v1 源码不受此分析器约束。</returns>
    public static ImmutableArray<Diagnostic> Analyze(CSharpCompilation compilation, IEnumerable<string>? entryTypes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        return new Analysis(compilation, entryTypes, cancellationToken).Run();
    }

    private static DiagnosticDescriptor Error(string id, string title, string message)
        => new(id, title, message, "ScriptV2Purity", Microsoft.CodeAnalysis.DiagnosticSeverity.Error, true);

    private sealed partial class Analysis
    {
        private readonly CSharpCompilation _compilation;
        private readonly CancellationToken _token;
        private readonly HashSet<string> _entries;
        private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModels = [];
        private readonly List<Diagnostic> _diagnostics = [];
        private readonly HashSet<string> _diagnosticKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _visitedMethods = new(StringComparer.Ordinal);
        private readonly HashSet<ITypeSymbol> _checkedPrepared = new(SymbolEqualityComparer.Default);
        private readonly HashSet<ITypeSymbol> _checkedStatics = new(SymbolEqualityComparer.Default);
        private readonly HashSet<ITypeSymbol> _checkedInitializers = new(SymbolEqualityComparer.Default);
        private readonly INamedTypeSymbol? _effectInterface;
        private readonly INamedTypeSymbol? _visualInterface;
        private readonly INamedTypeSymbol? _preparedBase;

        internal Analysis(CSharpCompilation compilation, IEnumerable<string>? entries, CancellationToken token)
        {
            _compilation = compilation; _token = token;
            _entries = new(entries ?? [], StringComparer.Ordinal);
            IAssemblySymbol? sdk = compilation.References.Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>()
                .FirstOrDefault(static assembly => assembly.Name == SdkAssembly);
            _effectInterface = sdk?.GetTypeByMetadataName(V2Namespace + ".IPureKaraokeEffect");
            _visualInterface = sdk?.GetTypeByMetadataName(V2Namespace + ".IPureVisualEffect");
            _preparedBase = sdk?.GetTypeByMetadataName(V2Namespace + ".PreparedEffect");
        }

        private SemanticModel GetSemanticModel(SyntaxTree tree)
        {
            if (_semanticModels.TryGetValue(tree, out SemanticModel? model)) return model;
            model = _compilation.GetSemanticModel(tree);
            _semanticModels.Add(tree, model);
            return model;
        }

        internal ImmutableArray<Diagnostic> Run()
        {
            if (_effectInterface is null) return [];
            bool foundEntry = false;
            foreach (SyntaxTree tree in _compilation.SyntaxTrees)
            {
                _token.ThrowIfCancellationRequested();
                SemanticModel model = GetSemanticModel(tree);
                foreach (BaseTypeDeclarationSyntax declaration in tree.GetRoot(_token).DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(declaration, _token) is not INamedTypeSymbol type) continue;
                    if (IsPrepared(type)) CheckPrepared(type, declaration.GetLocation());
                    if (_entries.Count != 0 && !_entries.Contains(MetadataName(type))) continue;
                    if (!type.AllInterfaces.Any(value => Same(value, _effectInterface) || Same(value, _visualInterface))) continue;
                    foundEntry = true;
                    CheckStaticState(type);
                    foreach (INamedTypeSymbol contract in type.AllInterfaces.Where(value => Same(value, _effectInterface) || Same(value, _visualInterface)))
                    foreach (IMethodSymbol member in contract.GetMembers().OfType<IMethodSymbol>())
                    {
                        if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol method)
                            CheckMethod(method, member.Name != "Declare", [], declaration.GetLocation());
                    }
                    // An implicit entry constructor still executes its source base
                    // constructors. None of that author code may escape analysis.
                    for (INamedTypeSymbol? current = type; current is not null && current.Locations.Any(static location => location.IsInSource);
                        current = current.BaseType)
                        foreach (IMethodSymbol constructor in current.InstanceConstructors)
                            CheckMethod(constructor, false, [], declaration.GetLocation());
                }
            }
            if (foundEntry) CheckPackageInitializers();
            return [.. _diagnostics.OrderBy(static value => value.Location.SourceTree?.FilePath, StringComparer.Ordinal)
                .ThenBy(static value => value.Location.SourceSpan.Start).ThenBy(static value => value.Id, StringComparer.Ordinal)];
        }

        private void CheckPackageInitializers()
        {
            var disposable = _compilation.GetTypeByMetadataName("System.IDisposable");
            foreach (SyntaxTree tree in _compilation.SyntaxTrees)
            {
                SemanticModel model = GetSemanticModel(tree);
                foreach (BaseTypeDeclarationSyntax declaration in tree.GetRoot(_token).DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(declaration, _token) is not INamedTypeSymbol type) continue;
                    CheckStaticState(type);
                    foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>())
                    {
                        if (method.MethodKind == MethodKind.Destructor)
                            Report(UnsupportedLanguage, SourceLocation(method), "作者析构器依赖非固定 GC 时机 " + method.ToDisplayString());
                        if (method.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString()
                            is "System.Runtime.CompilerServices.ModuleInitializerAttribute" or "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute"))
                            Report(UnsupportedLanguage, SourceLocation(method), "模块或原生初始化入口 " + method.ToDisplayString());
                    }
                    // Module disposal and C# using both invoke Dispose implicitly.
                    // Check the actual interface implementation, including explicit
                    // and inherited implementations, while allowing pure cleanup.
                    if (disposable is not null && type.AllInterfaces.Any(contract => Same(contract, disposable)))
                        foreach (IMethodSymbol member in disposable.GetMembers().OfType<IMethodSymbol>())
                            if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation)
                                CheckMethod(implementation, false, [], SourceLocation(type));
                }
            }
        }

        private void Report(DiagnosticDescriptor descriptor, Location location, string detail)
        {
            string key = descriptor.Id + "|" + location.SourceTree?.FilePath + "|" + location.SourceSpan + "|" + detail;
            if (_diagnosticKeys.Add(key)) _diagnostics.Add(Diagnostic.Create(descriptor, location, detail));
        }

        private void CheckStaticState(INamedTypeSymbol type)
        {
            if (!_checkedStatics.Add(type)) return;
            foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (!field.IsStatic || field.IsConst || field.AssociatedSymbol is not null) continue;
                if (!field.IsReadOnly || !Readonly(field.Type, new(SymbolEqualityComparer.Default), out _))
                    Report(StaticState, SourceLocation(field), field.ToDisplayString());
                foreach (SyntaxReference reference in field.DeclaringSyntaxReferences)
                {
                    SyntaxNode syntax = reference.GetSyntax(_token);
                    if (syntax is VariableDeclaratorSyntax { Initializer.Value: { } initializer })
                        CheckExpression(initializer, false);
                }
            }
            foreach (IPropertySymbol property in type.GetMembers().OfType<IPropertySymbol>().Where(static value => value.IsStatic))
            {
                if (property.SetMethod is not null || !Readonly(property.Type, new(SymbolEqualityComparer.Default), out _))
                    Report(StaticState, SourceLocation(property), property.ToDisplayString());
                foreach (SyntaxReference reference in property.DeclaringSyntaxReferences)
                    if (reference.GetSyntax(_token) is PropertyDeclarationSyntax { Initializer.Value: { } initializer }) CheckExpression(initializer, false);
            }
            foreach (IMethodSymbol constructor in type.StaticConstructors.Where(static value => !value.IsImplicitlyDeclared))
                CheckMethod(constructor, false, [], SourceLocation(constructor));
            CheckInitializers(type);
        }

        private void CheckInitializers(INamedTypeSymbol type)
        {
            if (!_checkedInitializers.Add(type)) return;
            foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>().Where(static value => !value.IsStatic && !value.IsConst))
            {
                if (IsStageHandle(field.Type)) Report(HandleEscape, SourceLocation(field), field.ToDisplayString());
                foreach (SyntaxReference reference in field.DeclaringSyntaxReferences)
                    if (reference.GetSyntax(_token) is VariableDeclaratorSyntax { Initializer.Value: { } initializer }) CheckExpression(initializer, false);
            }
            foreach (IPropertySymbol property in type.GetMembers().OfType<IPropertySymbol>().Where(static value => !value.IsStatic))
                foreach (SyntaxReference reference in property.DeclaringSyntaxReferences)
                    if (reference.GetSyntax(_token) is PropertyDeclarationSyntax { Initializer.Value: { } initializer }) CheckExpression(initializer, false);
        }

        private void CheckExpression(ExpressionSyntax expression, bool evaluating)
        {
            SemanticModel model = GetSemanticModel(expression.SyntaxTree);
            new Walker(this, null, evaluating, []).Visit(model.GetOperation(expression, _token));
        }

        private void CheckPrepared(ITypeSymbol type, Location location)
        {
            if (!_checkedPrepared.Add(type)) return;
            if (!Readonly(type, new(SymbolEqualityComparer.Default), out string reason))
                Report(PreparedType, location, type.ToDisplayString() + " / " + reason);
        }

        private bool Readonly(ITypeSymbol type, HashSet<ITypeSymbol> visiting, out string reason)
        {
            reason = type.ToDisplayString();
            if (type.TypeKind is TypeKind.Error or TypeKind.Dynamic or TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.TypeParameter
                || type is IArrayTypeSymbol || IsStageHandle(type) || type.TypeKind == TypeKind.Delegate
                || type.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr) return false;
            if (type.SpecialType is not (SpecialType.None or SpecialType.System_Object or SpecialType.System_Void)
                || type.TypeKind == TypeKind.Enum) return true;
            if (type is not INamedTypeSymbol named) return false;
            if (Same(named, _preparedBase)) return true;
            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                return Readonly(named.TypeArguments[0], visiting, out reason);
            string ns = named.ContainingNamespace.ToDisplayString();
            if (named.IsTupleType || ns == "System.Collections.Immutable" && named.TypeKind != TypeKind.Interface && !named.Name.Contains("Builder", StringComparison.Ordinal)
                || ns == "System.Collections.Frozen")
            {
                foreach (ITypeSymbol argument in named.TypeArguments)
                    if (!Readonly(argument, visiting, out reason)) return false;
                return true;
            }
            if (IsSdkValue(named) || IsBclValue(named)) return true;
            if (!named.Locations.Any(static location => location.IsInSource) || named.TypeKind == TypeKind.Interface
                || named.IsRefLikeType || named.IsReferenceType && !named.IsSealed) return false;
            if (!visiting.Add(named)) return true;
            if (named.BaseType is { SpecialType: not SpecialType.System_Object } parent && !Same(parent, _preparedBase)
                && !Readonly(parent, visiting, out reason)) return false;
            foreach (IFieldSymbol field in named.GetMembers().OfType<IFieldSymbol>().Where(static value => !value.IsStatic && value.AssociatedSymbol is null))
            {
                if (!field.IsReadOnly) { reason = field.ToDisplayString() + " 可写字段"; return false; }
                if (!Readonly(field.Type, visiting, out reason)) return false;
            }
            foreach (IPropertySymbol property in named.GetMembers().OfType<IPropertySymbol>().Where(static value => !value.IsStatic && !value.IsImplicitlyDeclared))
            {
                if (property.SetMethod is { IsInitOnly: false }) { reason = property.ToDisplayString() + " 可写属性"; return false; }
                if (property.ReturnsByRef || property.ReturnsByRefReadonly || !Readonly(property.Type, visiting, out reason)) return false;
            }
            visiting.Remove(named);
            return true;
        }

        private bool IsPrepared(ITypeSymbol? type)
        {
            for (INamedTypeSymbol? current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
                if (Same(current, _preparedBase)) return true;
            return false;
        }

        private static bool IsStageHandle(ITypeSymbol? type)
            => type is not null && IsTrustedSdk(type) && type.ContainingNamespace.ToDisplayString() == V2Namespace
                && type.Name is "DeclareContext" or "TextFrame" or "TextOutput" or "VisualFrame" or "VisualOutput" or "ParticleFrame"
                    or "SourceIndexes" or "SourcePresentationIndex" or "SourceLayoutIndex" or "SourceCursorIndex";

        private static bool IsTrustedSdk(ITypeSymbol type) => type.ContainingAssembly?.Name == SdkAssembly;

        private static bool IsSdkValue(ITypeSymbol type)
        {
            if (!IsTrustedSdk(type) || IsStageHandle(type)) return false;
            if (type.ContainingNamespace.ToDisplayString() == V2Namespace)
            {
                string valueName = type.ContainingType?.Name ?? type.Name;
                return valueName is "PreparedEffect" or "TimelineIndex" or "TimelineIndexBudget" or "TimelineScope" or "TimelineSelector"
                    or "SourcePresentationLoad" or "SourcePresentationSample" or "SourceCursorSegment" or "TextTarget" or "TextWrite"
                    or "TextOutputSnapshot" or "TextLineLoad" or "TextClusterTarget" or "TextClusterGeometrySample"
                    or "BaseTokenTransform" or "BaseTokenStyle" or "TextClusterTransform"
                    or "RateSegment" or "RateTrack" or "ParticleSpec" or "BridgePolicy" or "CursorEmissionPlan" or "BurstEventRow"
                    or "EventBurstPlan" or "ParticleSchedule" or "GeometrySamplingPolicy" or "VisualOutputDeclaration" or "ParticleLimits"
                    or "ParticleHead" or "ParticleOrigin" or "ParticleIdentity" or "BirthWindow" or "ParticleBirth" or "ParticleView"
                    or "ParticleBirthWindow" || type.TypeKind == TypeKind.Enum;
            }
            // These are fixed projections or value identities. Existing lifecycle writers/queries are deliberately absent.
            string name = type.ContainingType?.Name ?? type.Name;
            return name is "ContentCatalog" or "SceneCatalog" or "ParameterValues" or "ParameterValue" or "AudioFeatures"
                or "ResourceCatalog" or "DeterministicRandom" or "CurveValue" or "Curves" or "ColorRgba" or "Vector2"
                or "Point2" or "Rect2" or "Affine2" or "TimeRange" or "MaterialValue" or "MaterialUniforms" or "UniformValue"
                or "GradientValue" or "GradientStopValue" or "SourceBranch" or "SourceLine" or "SourceToken" or "SourceRuby"
                or "SourceRubySegment" or "SourceTimingStatus" or "FrameLine" or "FrameToken" or "FrameMark" or "FrameGroup"
                or "EffectBinding" or "FramePresentations" or "StyleView" or "BaselineStyles" or "TokenFamilyTarget"
                or "BaseTokenTarget" or "TextMarkTarget" or "LineTarget" or "GroupTarget" or "NodeTarget" or "ImageHandle" or "MaterialHandle"
                or "GraphHandle" or "ResourceIdentity" or "FontHandle" or "SpriteHandle" or "VectorHandle" or "TimelineEvent" or "TimelineEventKind"
                or "StrokeValue" or "ShadowValue" or "GlowValue" or "GradientStop" or "GroupStyleView" or "PerformanceSample"
                or "GeometrySample" or "RubyGeometrySample" or "TokenGeometrySample" or "LineGeometrySample" or "TextMarkGeometrySample"
                or "GroupGeometrySample" or "SourceFilter" or "BaseSceneGeometry" or "AudioFeatureValues" or "AudioFeaturePoint" or "AudioFeatureSample" or "AudioFeatureRange"
                or "TextProgressPath" or "PlaybackCursor" or "CursorGeometry" or "PlaybackFrame" or "CursorIdentity" or "FinalSceneGeometry"
                or "GridWarp" or "TextGeometryMapping" or "GraphBindingValue" or "GraphTransformValue" or "GraphTransformValues" or "GraphUniformValues"
                or "SceneElementIdentity" or "EventSourceOrder" or "LeadInEventTarget"
                or "ParticleDescription" or "ParticleMotion" or "ParticleAgeCurve" or "ParticleSource" or "BurstAnchor" or "ParticleTrailSample"
                or "Dot" or "Segment" or "Trail" or "TrailPoint" or "Sprite" or "VectorVisual" or "AtlasSprite"
                || IsTimelineEventValue(type)
                || type.Name.EndsWith("Id", StringComparison.Ordinal) || type.TypeKind == TypeKind.Enum;
        }

        private static bool IsTimelineEventValue(ITypeSymbol type)
        {
            for (INamedTypeSymbol? current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
                if (current.Name == "TimelineEvent" && IsTrustedSdk(current)) return true;
            return false;
        }

        private static bool IsBclValue(ITypeSymbol type)
            => type.ContainingNamespace.ToDisplayString() == "System.Numerics" && type.Name is ("Vector2" or "Vector3" or "Vector4" or "Matrix3x2" or "Matrix4x4" or "Quaternion" or "Complex")
                || type.ContainingNamespace.ToDisplayString() == "System" && type.Name is ("DateTime" or "DateTimeOffset" or "TimeSpan" or "Guid" or "Index" or "Range");

        private static string MetadataName(INamedTypeSymbol type)
            => type.ContainingType is { } parent ? MetadataName(parent) + "+" + type.MetadataName
                : (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") + type.MetadataName;
        private static bool Same(ISymbol? first, ISymbol? second) => first is not null && second is not null && SymbolEqualityComparer.Default.Equals(first, second);
        private static Location SourceLocation(ISymbol symbol) => symbol.Locations.FirstOrDefault(static value => value.IsInSource) ?? Location.None;
    }
}
