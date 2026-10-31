using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>图预算的固定值；超过限制报告错误，不降质。</summary>
public sealed record GraphExecutionBudget
{
    /// <summary>R0 已接受的默认预算。</summary>
    public static GraphExecutionBudget Default { get; } = new();
    /// <summary>每图最大 pass。</summary>
    public int MaximumPasses { get; init; } = 32;
    /// <summary>输入及中间产物的最大同时活动表面数。</summary>
    public int MaximumSurfaces { get; init; } = 64;
    /// <summary>活动图像预估字节预算。</summary>
    public long MaximumBytes { get; init; } = 512L * 1024 * 1024;
    /// <summary>单张表面每边最大像素。</summary>
    public int MaximumPixelDimension { get; init; } = 8192;
    /// <summary>单输入／节点最大外扩与采样偏移。</summary>
    public double MaximumExpandPx { get; init; } = 256;
    /// <summary>最大逻辑 sigma。</summary>
    public double MaximumBlurSigmaPx { get; init; } = 64;
    /// <summary>shader 标量槽预算。</summary>
    public int MaximumUniformScalars { get; init; } = ShaderProgramDefinition.DefaultScalarBudget;
}
/// <summary>图结构／值／预算诊断，保留图及字段身份。</summary>
public sealed class ImageGraphException : InvalidOperationException
{
    /// <summary>创建具名错误。</summary>
    public ImageGraphException(string code, string graphId, string field, string message, Exception? innerException = null)
        : base($"{code}: {graphId} {field}: {message}", innerException) { Code = code; GraphId = graphId; Field = field; }
    /// <summary>错误代码。</summary>
    public string Code { get; }
    /// <summary>图身份。</summary>
    public string GraphId { get; }
    /// <summary>失败字段。</summary>
    public string Field { get; }
}
/// <summary>唯一结构校验后得到的不可变拓扑计划。</summary>
public sealed class CompiledImageGraphDefinition
{
    internal CompiledImageGraphDefinition(ImageGraphDefinition definition, ImmutableArray<ImageGraphNode> nodes,
        ImmutableDictionary<string, EffectParameterConstraint> parameters, ImmutableDictionary<string, ShaderProgramDefinition> programs,
        ImmutableDictionary<GraphReference, ShaderSurfaceKind> types, GraphExecutionBudget budget)
    { Definition = definition; Nodes = nodes; Parameters = parameters; Programs = programs; Types = types; Budget = budget; }
    /// <summary>原不可变定义。</summary>
    public ImageGraphDefinition Definition { get; }
    /// <summary>所有节点的确定性拓扑顺序。</summary>
    public ImmutableArray<ImageGraphNode> Nodes { get; }
    /// <summary>本图引用的 owning 包约束。</summary>
    public ImmutableDictionary<string, EffectParameterConstraint> Parameters { get; }
    /// <summary>本图使用的固定程序。</summary>
    public ImmutableDictionary<string, ShaderProgramDefinition> Programs { get; }
    /// <summary>所有边的真实端口类型。</summary>
    public ImmutableDictionary<GraphReference, ShaderSurfaceKind> Types { get; }
    /// <summary>本编译代次固定预算。</summary>
    public GraphExecutionBudget Budget { get; }
}
/// <summary>中立图的唯一静态类型／拓扑／参数／外扩校验。</summary>
public static class ImageGraphCompiler
{
    /// <summary>验证全部声明并生成拓扑；不运行作者或编译 native 源码。</summary>
    public static CompiledImageGraphDefinition Compile(ImageGraphDefinition graph, IEnumerable<EffectParameterConstraint> parameters,
        IEnumerable<KeyValuePair<string, ShaderProgramDefinition>> programs, GraphExecutionBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(parameters); ArgumentNullException.ThrowIfNull(programs);
        budget ??= GraphExecutionBudget.Default;
        [DoesNotReturn] void Fail(string code, string field, string message) => throw new ImageGraphException(code, graph.Id, field, message);
        if (string.IsNullOrWhiteSpace(graph.Id) || graph.Version != ImageGraphDefinition.CurrentVersion || !Enum.IsDefined(graph.Space)
            || !Enum.IsDefined(graph.AnchorMode) || !Enum.IsDefined(graph.Composite) || !Nonnegative(graph.MaximumExpandPx))
            Fail("InvalidGraphDefinition", "graph", "身份、版本、空间、模式或边界无效。");
        if (budget.MaximumPasses <= 0 || budget.MaximumSurfaces <= 0 || budget.MaximumBytes <= 0 || budget.MaximumPixelDimension <= 0
            || !Nonnegative(budget.MaximumExpandPx) || !Nonnegative(budget.MaximumBlurSigmaPx) || budget.MaximumUniformScalars <= 0)
            Fail("InvalidGraphBudget", "budget", "预算无效。");
        if (graph.Nodes.Length > budget.MaximumPasses) Fail("GraphPassBudgetExceeded", "nodes", $"{graph.Nodes.Length} > {budget.MaximumPasses}");
        var allParameters = Unique(parameters, value => value.Id, graph.Id, "parameters");
        var selected = ImmutableDictionary.CreateBuilder<string, EffectParameterConstraint>(StringComparer.Ordinal);
        foreach (string id in graph.ParameterIds)
        {
            if (string.IsNullOrWhiteSpace(id) || !allParameters.TryGetValue(id, out var constraint) || selected.ContainsKey(id))
                Fail("InvalidGraphParameter", "parameters." + id, "引用缺失或重复。");
            var parameter = allParameters[id];
            ValidateConstraint(parameter, graph.Id); selected.Add(id, parameter);
        }
        var parameterMap = selected.ToImmutable();
        var programMap = Unique(programs, value => value.Key, graph.Id, "programs").ToImmutableDictionary(value => value.Key, value => value.Value.Value, StringComparer.Ordinal);
        var inputs = Unique(graph.Inputs, value => value.Id, graph.Id, "inputs");
        var nodes = Unique(graph.Nodes, value => value.Id, graph.Id, "nodes");
        var types = ImmutableDictionary.CreateBuilder<GraphReference, ShaderSurfaceKind>();
        foreach (var input in graph.Inputs)
        {
            if (!Enum.IsDefined(input.Kind) || !Enum.IsDefined(input.Space) || input.Space != graph.Space || !Enum.IsDefined(input.BackgroundRequirement))
                Fail("InvalidGraphInput", "inputs." + input.Id, "输入类型／空间／背景声明无效。");
            IEnumerable<GraphInputSource> sources = [input.Source];
            if (input.ChoiceParameter is { } choiceId)
            {
                if (!parameterMap.TryGetValue(choiceId, out var constraint) || constraint is not EffectParameterConstraint.Choice choice
                    || input.Choices.Count != choice.Values.Length || choice.Values.Any(value => !input.Choices.ContainsKey(value)))
                    Fail("InvalidGraphInputChoice", "inputs." + input.Id, "来源选项须完整对应已声明 enum。");
                sources = input.Choices.Values;
            }
            else if (input.Choices.Count != 0) Fail("InvalidGraphInputChoice", "inputs." + input.Id, "未声明来源参数。");
            foreach (var source in sources)
            {
                if (!Enum.IsDefined(source) || SourceKind(source) != input.Kind || (source == GraphInputSource.ResourceImage && string.IsNullOrWhiteSpace(input.ResourceId)))
                    Fail("InvalidGraphInput", "inputs." + input.Id, "来源类型或资源身份无效。");
                if (input.BackgroundRequirement != GraphBackgroundRequirement.None && source is not (GraphInputSource.BackgroundMedia or GraphInputSource.BackdropBelow or GraphInputSource.BackgroundColor))
                    Fail("InvalidGraphBackgroundRequirement", "inputs." + input.Id, "背景依赖不能附在普通目标上。");
                if (source == GraphInputSource.BackgroundMedia && input.BackgroundRequirement == GraphBackgroundRequirement.None)
                    Fail("InvalidGraphBackgroundRequirement", "inputs." + input.Id, "媒体来源必须明确声明背景依赖，不能泄漏到透明输出。");
            }
            types.Add(GraphReference.Input(input.Id), input.Kind);
        }
        var marks = new Dictionary<string, int>(StringComparer.Ordinal); var ordered = ImmutableArray.CreateBuilder<ImageGraphNode>();
        var expansions = new Dictionary<GraphReference, double>();
        foreach (var input in graph.Inputs) expansions[GraphReference.Input(input.Id)] = 0;
        ShaderSurfaceKind Visit(GraphReference reference)
        {
            if (string.IsNullOrWhiteSpace(reference.Id)) { Fail("GraphDanglingReference", "reference", "引用身份为空。"); return default; }
            if (reference.IsInput)
            {
                if (!types.TryGetValue(reference, out var type)) Fail("GraphDanglingReference", "input:" + reference.Id, "输入不存在。");
                return types[reference];
            }
            if (!nodes.TryGetValue(reference.Id, out var node)) { Fail("GraphDanglingReference", "node:" + reference.Id, "节点不存在。"); return default; }
            if (marks.TryGetValue(reference.Id, out int mark))
            { if (mark == 1) Fail("GraphCycle", "nodes." + reference.Id, "同帧图含环。"); return types[reference]; }
            marks[reference.Id] = 1;
            foreach (var dependency in References(node)) _ = Visit(dependency);
            var limits = node.Bounds;
            if (limits == null || limits.Expand == null || !Nonnegative(limits.MaximumExpandPx) || !Nonnegative(limits.MaximumSampleOffsetPx) || !Enum.IsDefined(limits.Edge))
                Fail("InvalidGraphNodeBounds", "nodes." + node.Id, "边界声明缺失或无效。");
            if (limits!.MaximumExpandPx > budget.MaximumExpandPx || limits.MaximumSampleOffsetPx > budget.MaximumExpandPx)
                Fail("GraphExpansionBudgetExceeded", "nodes." + node.Id, "声明外扩／采样偏移超过预算。");
            double expand = ExpansionRange(limits.Expand, parameterMap, graph.Id).Maximum;
            if (expand > limits.MaximumExpandPx) Fail("GraphExpansionExceeded", "nodes." + node.Id, "参数最大外扩超过声明。");
            double cumulative = References(node).Select(value => expansions[value]).DefaultIfEmpty(0).Max() + expand;
            if (!double.IsFinite(cumulative) || cumulative > graph.MaximumExpandPx)
                Fail("GraphCumulativeExpansionExceeded", "nodes." + node.Id, "累计外扩超过图边界。");
            ShaderSurfaceKind output = node switch
            {
                ImageGraphNode.RuntimeShader shader => ValidateShader(shader, types, parameterMap, programMap, graph.Id, budget),
                ImageGraphNode.Blur blur => ValidateBlur(blur, types, parameterMap, graph.Id, budget),
                ImageGraphNode.Mask mask => RequireMask(mask, types, graph.Id),
                ImageGraphNode.Transform transform => RequireTransform(transform, types, graph.Id),
                ImageGraphNode.Blend blend => RequireBlend(blend, types, graph.Id),
                _ => throw new ImageGraphException("UnsupportedGraphOperation", graph.Id, "nodes." + node.Id, "操作不属于图 v1。"),
            };
            expansions[reference] = cumulative; types[reference] = output; marks[reference.Id] = 2; ordered.Add(node); return output;
        }
        foreach (string id in nodes.Keys.Order(StringComparer.Ordinal)) _ = Visit(GraphReference.Node(id));
        if (Visit(graph.Output) != ShaderSurfaceKind.Color) Fail("GraphOutputTypeMismatch", "output", "图像输出须为颜色表面。");
        if (graph.Composite == GraphCompositeMode.ReplaceCoveredBackdrop)
        { if (graph.CompositeCoverage is not { } coverage || Visit(coverage) != ShaderSurfaceKind.Coverage) Fail("GraphCompositeCoverageMissing", "composite.coverage", "替换合成须提供独立覆盖。"); }
        else if (graph.CompositeCoverage.HasValue) Fail("InvalidGraphComposite", "composite.coverage", "普通合成不读取覆盖度。");
        if (graph.BypassInput is { } bypass && (!bypass.IsInput || !inputs.TryGetValue(bypass.Id, out var bypassDefinition) || bypassDefinition.Source != GraphInputSource.TargetColor
            || !bypassDefinition.Required || bypassDefinition.BackgroundRequirement != GraphBackgroundRequirement.None
            || bypassDefinition.ChoiceParameter != null || Visit(bypass) != ShaderSurfaceKind.Color))
            Fail("InvalidGraphBypass", "bypassInput", "旁路须明确 required／none 的原 TargetColor 输入。");
        var usedPrograms = graph.Nodes.OfType<ImageGraphNode.RuntimeShader>().Select(node => node.ProgramId).ToHashSet(StringComparer.Ordinal);
        return new(graph, ordered.ToImmutable(), parameterMap, programMap.Where(pair => usedPrograms.Contains(pair.Key)).ToImmutableDictionary(StringComparer.Ordinal), types.ToImmutable(), budget);
    }

    /// <summary>操作的全部显式输入边。</summary>
    public static IEnumerable<GraphReference> References(ImageGraphNode node) => node switch
    {
        ImageGraphNode.RuntimeShader shader => shader.Children.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value),
        ImageGraphNode.Blur blur => [blur.Input], ImageGraphNode.Mask mask => [mask.Color, mask.Coverage],
        ImageGraphNode.Transform transform => [transform.Input], ImageGraphNode.Blend blend => [blend.Foreground, blend.Background],
        _ => [],
    };
    /// <summary>显式来源的固定表面编码。</summary>
    public static ShaderSurfaceKind SourceKind(GraphInputSource source) => source is GraphInputSource.GlyphCoverage or GraphInputSource.StrokeCoverage
        or GraphInputSource.SungCoverage or GraphInputSource.TargetRegionCoverage ? ShaderSurfaceKind.Coverage : ShaderSurfaceKind.Color;
    /// <summary>仅用于已校验图的数值区间，拒绝未知参数或负／非有限值。</summary>
    public static (double Minimum, double Maximum) NumberRange(GraphNumberBinding binding, IReadOnlyDictionary<string, EffectParameterConstraint> parameters, string graphId)
    {
        var range = binding switch
        {
            GraphNumberBinding.Literal literal => (literal.Value, literal.Value),
            GraphNumberBinding.Parameter parameter when parameters.TryGetValue(parameter.Id, out var constraint) && constraint is EffectParameterConstraint.Number number => (number.Minimum, number.Maximum),
            _ => throw new ImageGraphException("GraphNumberBindingMismatch", graphId, "number", "字段只接受常量或已声明 number。"),
        };
        if (!Nonnegative(range.Item1) || !Nonnegative(range.Item2) || range.Item2 < range.Item1)
            throw new ImageGraphException("InvalidGraphNumberRange", graphId, "number", "字段需要有限非负区间。");
        return range;
    }
    /// <summary>仅用于已校验图的外扩最大范围。</summary>
    public static (double Minimum, double Maximum) ExpansionRange(GraphExpansion expansion, IReadOnlyDictionary<string, EffectParameterConstraint> parameters, string graphId)
    {
        if (expansion is GraphExpansion.Literal literal) return NumberRange(new GraphNumberBinding.Literal(literal.Value), parameters, graphId);
        if (expansion is not GraphExpansion.CeilingMultiple multiple || !Nonnegative(multiple.Factor))
            throw new ImageGraphException("InvalidGraphExpansion", graphId, "bounds", "外扩表达式无效。");
        var range = NumberRange(new GraphNumberBinding.Parameter(multiple.ParameterId), parameters, graphId);
        double minimum = Math.Ceiling(range.Minimum * multiple.Factor), maximum = Math.Ceiling(range.Maximum * multiple.Factor);
        if (!Nonnegative(maximum)) throw new ImageGraphException("InvalidGraphExpansion", graphId, "bounds", "外扩计算溢出。");
        return (minimum, maximum);
    }
    private static ImmutableDictionary<string, T> Unique<T>(IEnumerable<T> values, Func<T, string> key, string graphId, string field)
    {
        var map = ImmutableDictionary.CreateBuilder<string, T>(StringComparer.Ordinal);
        foreach (T value in values)
        { if (value == null || string.IsNullOrWhiteSpace(key(value)) || !map.TryAdd(key(value), value)) throw new ImageGraphException("GraphDuplicateIdentity", graphId, field, "空／重复身份或 null 定义。"); }
        return map.ToImmutable();
    }
    private static bool Nonnegative(double value) => double.IsFinite(value) && value >= 0;
    private static bool Finite(SceneMatrix m) => double.IsFinite(m.A) && double.IsFinite(m.B) && double.IsFinite(m.C) && double.IsFinite(m.D) && double.IsFinite(m.Tx) && double.IsFinite(m.Ty);
    private static void ValidateConstraint(EffectParameterConstraint parameter, string graphId)
    {
        bool valid = parameter switch
        {
            EffectParameterConstraint.Number number => double.IsFinite(number.Minimum) && double.IsFinite(number.Maximum) && number.Maximum >= number.Minimum,
            EffectParameterConstraint.Whole integer => integer.Maximum >= integer.Minimum,
            EffectParameterConstraint.Duration duration => duration.Minimum >= 0 && duration.Maximum >= duration.Minimum,
            EffectParameterConstraint.Choice choice => choice.Values.Length > 0 && choice.Values.All(value => !string.IsNullOrWhiteSpace(value)) && choice.Values.Distinct(StringComparer.Ordinal).Count() == choice.Values.Length,
            EffectParameterConstraint.Toggle or EffectParameterConstraint.Color or EffectParameterConstraint.Text or EffectParameterConstraint.Curve or EffectParameterConstraint.Asset => true,
            _ => false,
        };
        if (!valid) throw new ImageGraphException("InvalidGraphParameter", graphId, "parameters." + parameter.Id, "约束无效。");
    }
    private static ShaderSurfaceKind ValidateShader(ImageGraphNode.RuntimeShader node, ImmutableDictionary<GraphReference, ShaderSurfaceKind>.Builder types,
        ImmutableDictionary<string, EffectParameterConstraint> parameters, ImmutableDictionary<string, ShaderProgramDefinition> programs, string graphId, GraphExecutionBudget budget)
    {
        if (!Enum.IsDefined(node.OutputKind) || !programs.TryGetValue(node.ProgramId, out var program)) throw new ImageGraphException("GraphShaderMissing", graphId, "nodes." + node.Id, "固定程序或输出类型无效。");
        if (program.ScalarCount > budget.MaximumUniformScalars) throw new ImageGraphException("GraphUniformBudgetExceeded", graphId, "nodes." + node.Id, "shader 标量预算超限。");
        if (node.Uniforms.Count != program.Uniforms.Length || node.Children.Count != program.Children.Length)
            throw new ImageGraphException("GraphShaderBindingMismatch", graphId, "nodes." + node.Id, "必须提供完整 uniform 和 child。 ");
        foreach (var uniform in program.Uniforms)
        {
            if (!node.Uniforms.TryGetValue(uniform.Name, out var binding)) throw new ImageGraphException("GraphShaderBindingMismatch", graphId, "nodes." + node.Id + "." + uniform.Name, "未绑定 uniform。");
            bool matches = binding switch
            {
                GraphUniformBinding.Literal literal => literal.Value != null && literal.Value.Kind == uniform.Kind && literal.Value.ArrayLength == uniform.ArrayLength,
                GraphUniformBinding.Host { Value: GraphHostValue.TimeSeconds } => uniform.Kind == ShaderUniformKind.Float && !uniform.ArrayLength.HasValue,
                GraphUniformBinding.Host { Value: GraphHostValue.CanvasSize } => uniform.Kind == ShaderUniformKind.Float2 && !uniform.ArrayLength.HasValue,
                GraphUniformBinding.Parameter parameter => !uniform.ArrayLength.HasValue && parameters.TryGetValue(parameter.Id, out var constraint) && (uniform.Kind switch
                {
                    ShaderUniformKind.Float => constraint is EffectParameterConstraint.Number,
                    ShaderUniformKind.Int => constraint is EffectParameterConstraint.Toggle || constraint is EffectParameterConstraint.Whole integer && integer.Minimum >= int.MinValue && integer.Maximum <= int.MaxValue,
                    ShaderUniformKind.Color => constraint is EffectParameterConstraint.Color,
                    _ => false,
                }),
                _ => false,
            };
            if (!matches) throw new ImageGraphException("GraphUniformBindingMismatch", graphId, "nodes." + node.Id + "." + uniform.Name, "值绑定与固定 uniform 不符。");
        }
        foreach (var child in program.Children)
            if (!node.Children.TryGetValue(child.Name, out var reference) || types[reference] != child.Kind)
                throw new ImageGraphException("GraphShaderChildTypeMismatch", graphId, "nodes." + node.Id + "." + child.Name, "child 表面编码不符。");
        return node.OutputKind;
    }
    private static ShaderSurfaceKind ValidateBlur(ImageGraphNode.Blur node, ImmutableDictionary<GraphReference, ShaderSurfaceKind>.Builder types,
        ImmutableDictionary<string, EffectParameterConstraint> parameters, string graphId, GraphExecutionBudget budget)
    {
        RequireType(node.Input, ShaderSurfaceKind.Color, types, graphId, node.Id);
        var sigma = NumberRange(node.SigmaPx, parameters, graphId);
        if (sigma.Maximum > budget.MaximumBlurSigmaPx) throw new ImageGraphException("GraphBlurBudgetExceeded", graphId, "nodes." + node.Id, "sigma 最大值超限。");
        double required = Math.Ceiling(3 * sigma.Maximum);
        bool bound = node.Bounds.Expand is GraphExpansion.Literal literal && literal.Value >= required
            || node.SigmaPx is GraphNumberBinding.Parameter parameter && node.Bounds.Expand is GraphExpansion.CeilingMultiple multiple && parameter.Id == multiple.ParameterId && multiple.Factor >= 3;
        if (!bound || node.Bounds.MaximumSampleOffsetPx < required) throw new ImageGraphException("GraphBlurBoundsInsufficient", graphId, "nodes." + node.Id, "必须声明完整 3σ 外扩及采样范围。");
        return ShaderSurfaceKind.Color;
    }
    private static ShaderSurfaceKind RequireMask(ImageGraphNode.Mask node, ImmutableDictionary<GraphReference, ShaderSurfaceKind>.Builder types, string graphId)
    { RequireType(node.Color, ShaderSurfaceKind.Color, types, graphId, node.Id); RequireType(node.Coverage, ShaderSurfaceKind.Coverage, types, graphId, node.Id); return ShaderSurfaceKind.Color; }
    private static ShaderSurfaceKind RequireTransform(ImageGraphNode.Transform node, ImmutableDictionary<GraphReference, ShaderSurfaceKind>.Builder types, string graphId)
    {
        if (!Finite(node.Matrix) || !node.Matrix.TryInverse(out var inverse) || !Finite(inverse) || !node.OutputBounds.IsValid)
            throw new ImageGraphException("InvalidGraphTransform", graphId, "nodes." + node.Id, "仿射和输出框须有限且可逆。");
        return types[node.Input];
    }
    private static ShaderSurfaceKind RequireBlend(ImageGraphNode.Blend node, ImmutableDictionary<GraphReference, ShaderSurfaceKind>.Builder types, string graphId)
    {
        RequireType(node.Foreground, ShaderSurfaceKind.Color, types, graphId, node.Id); RequireType(node.Background, ShaderSurfaceKind.Color, types, graphId, node.Id);
        if (!Enum.IsDefined(node.Mode)) throw new ImageGraphException("InvalidGraphBlend", graphId, "nodes." + node.Id, "混合模式无效。"); return ShaderSurfaceKind.Color;
    }
    private static void RequireType(GraphReference reference, ShaderSurfaceKind required, ImmutableDictionary<GraphReference, ShaderSurfaceKind>.Builder types, string graphId, string id)
    { if (types[reference] != required) throw new ImageGraphException("GraphPortTypeMismatch", graphId, "nodes." + id, "端口需要 " + required); }
}
