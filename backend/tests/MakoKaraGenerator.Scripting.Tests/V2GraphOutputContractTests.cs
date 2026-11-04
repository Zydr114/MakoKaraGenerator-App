using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class V2GraphOutputContractTests
{
    private static readonly ResourceCatalog Resources = new("graphs", "sha256:" + new string('a', 64), [], ["first", "last"], []);
    private static TextFrame Frame(bool independent = true)
    {
        ContentCatalog content = new([], [], [], []); SceneCatalog scene = new(content, [], [], [], []);
        var family = new TokenFamilyTarget(new("token"), new("binding")); var basic = new BaseTokenTarget(family.TokenId, family.BindingId);
        var line = new FrameLine(new("line"), new("branch"), family.BindingId, new("node"), new("group"), new("program"), null, null, DisplayPhase.Performing, 1);
        var geometry = new GeometrySample { AdvanceBounds = new(0, 0, 10, 10), Baseline = new(0, 8), BaseWorldMatrix = Affine2.Identity,
            CanTransformIndependently = independent, DiagnosticCode = independent ? null : "UnsupportedGlyphPartition" };
        FrameContext legacy = new() { TimeUs = 1000, Project = new() { Content = content, Scene = scene, CanvasSize = new(16, 16) },
            Parameters = ParameterValues.Empty, Resources = Resources, Random = new(42, new("test")), Binding = new EffectBinding.Project(),
            Presentations = new() { LinesInBinding = [line], TokensInBinding = [new() { TokenId = family.TokenId, BranchId = line.BranchId,
                LineId = line.LineId, BindingId = family.BindingId, SourceText = "x", WithRuby = family, BaseOnly = basic,
                Performance = new(null, null, null, null, false, false, ProgressSource.Missing) }] },
            Geometry = [new() { BranchId = line.BranchId, LineId = line.LineId, WithRuby = family, BaseOnly = basic, BaseGeometry = geometry, FamilyGeometry = geometry }],
            LineGeometry = [new() { BranchId = line.BranchId, Target = line.Target, BaseGeometry = geometry, ProgressPath = new(0, 10, 8, 5) }] };
        var source = new DeclareContext(content, scene, ParameterValues.Empty, AudioFeatures.NotAnalyzed, Resources, 42, TimelineIndex.Empty, new(16, 16), new("test"));
        return new(source, legacy);
    }
    private static GraphBindingValue Value(string graph = "first", UniformValue? uniform = null)
        => new(Resources.GetGraph(graph), ParameterValues.Empty, uniform is null ? GraphUniformValues.Empty : new([new(("shader", "amount"), uniform)]), GraphTransformValues.Empty);

    [Fact]
    public void 写入保留完整序列且未知最终shader节点留宿主身份阶段校验()
    {
        var frame = Frame(); var target = frame.Geometry[0].WithRuby;
        var output = new TextOutput(frame, ["image.graph"]);
        output.BindGraph(target, Value(uniform: UniformValue.Float(.5)));
        output.BindGraph(frame.LineLoads[0].Target, Value());
        output.BindGraph(target, Value("last"));
        var writes = output.Complete().Writes.OfType<TextWrite.GraphBinding>().ToArray();
        Assert.Equal(3, writes.Length); Assert.Equal(new TextTarget.Family(target), writes[0].Target);
        Assert.Single(writes[0].Value.Uniforms.Values); Assert.Empty(writes[2].Value.Uniforms.Values);
        Assert.Equal(Resources.GetGraph("last"), writes[2].Value.Graph);
        output.Invalidate(); Assert.Equal(3, writes.Length);
    }

    [Theory]
    [InlineData("native", "ShaderUniformNativeRange")]
    [InlineData("target", "TargetOutsideBinding")]
    [InlineData("base", "PropertyTargetUnsupported")]
    [InlineData("cap", "UndeclaredCapability")]
    [InlineData("foreign", "InvalidGraphResource")]
    public void 非法图写即使被作者捕获也锁存到全部后续writer入口与完成(string kind, string code)
    {
        var frame = Frame(); var target = frame.Geometry[0].WithRuby;
        var output = new TextOutput(frame, kind == "cap" ? ["token.affine"] : ["image.graph", "token.affine"]);
        var foreign = new ResourceCatalog("graphs", "sha256:" + new string('b', 64), [], ["first"], []);
        GraphBindingValue value = kind == "foreign" ? new(foreign.GetGraph("first"), ParameterValues.Empty, GraphUniformValues.Empty, GraphTransformValues.Empty)
            : Value(uniform: kind == "native" ? UniformValue.Float(1e100) : null);
        TextTarget selected = kind == "target" ? new TextTarget.Family(new(new("foreign"), target.BindingId))
            : kind == "base" ? new TextTarget.Base(frame.Geometry[0].BaseOnly) : new TextTarget.Family(target);
        var original = Assert.Throws<EffectOutputException>(() => output.BindGraph(selected, value)); Assert.Equal(code, original.Code);
        Assert.Same(original, Assert.Throws<EffectOutputException>(() => output.BindGraph(target, Value())));
        Assert.Same(original, Assert.Throws<EffectOutputException>(() => output.AppendTransform(target, Affine2.Identity)));
        Assert.Same(original, Assert.Throws<EffectOutputException>(() => output.SetStroke(target, null)));
        Assert.Same(original, Assert.Throws<EffectOutputException>(() => output.SetWarp(target, null)));
        Assert.Same(original, Assert.Throws<EffectOutputException>(() => output.Complete()));
    }

    [Fact]
    public void 被覆盖的全部写入与数组标量均计费且超限锁存()
    {
        var frame = Frame(); var target = frame.Geometry[0].WithRuby; var output = new TextOutput(frame, ["image.graph"]);
        for (int i = 0; i < GraphBindingBudget.MaximumWrites; i++) output.BindGraph(target, Value());
        Assert.Equal(GraphBindingBudget.MaximumWrites, output.Complete().Writes.Length);
        var excess = new TextOutput(frame, ["image.graph"]);
        var array = UniformValue.Array(Enumerable.Repeat(UniformValue.Float(1), GraphBindingBudget.MaximumScalars));
        excess.BindGraph(target, Value(uniform: array));
        var error = Assert.Throws<EffectOutputException>(() => excess.BindGraph(target, Value(uniform: UniformValue.Float(1))));
        Assert.Equal("GraphOutputBudgetExceeded", error.Code); Assert.Same(error, Assert.Throws<EffectOutputException>(() => excess.Complete()));
        var writes = new TextOutput(frame, ["image.graph"]);
        for (int i = 0; i < GraphBindingBudget.MaximumWrites; i++) writes.BindGraph(target, Value());
        Assert.Equal("GraphOutputBudgetExceeded", Assert.Throws<EffectOutputException>(() => writes.BindGraph(target, Value())).Code);
        Assert.Throws<EffectOutputException>(() => writes.Complete());
        var bytes = new TextOutput(frame, ["image.graph"]);
        var textParameters = new ParameterValues([new("text", new ParameterValue.Text(new string('x', (int)(GraphBindingBudget.MaximumBytes / 2))))]);
        var oversized = new GraphBindingValue(Resources.GetGraph("first"), textParameters, GraphUniformValues.Empty, GraphTransformValues.Empty);
        Assert.Equal("GraphOutputBudgetExceeded", Assert.Throws<EffectOutputException>(() => bytes.BindGraph(target, oversized)).Code);
        Assert.Throws<EffectOutputException>(() => bytes.Complete());
    }

    [Fact]
    public void 共享字形不能独立绑定图且取消原样传播不可被catch后完成()
    {
        var shared = Frame(false); var output = new TextOutput(shared, ["image.graph"]);
        Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<EffectOutputException>(() => output.BindGraph(shared.Geometry[0].WithRuby, Value())).Code);
        using var cancel = new CancellationTokenSource(); var frame = Frame(); var canceled = new TextOutput(frame, ["image.graph"]) { GraphCancellation = cancel.Token };
        cancel.Cancel(); Assert.Throws<OperationCanceledException>(() => canceled.BindGraph(frame.Geometry[0].WithRuby, Value()));
        Assert.Throws<OperationCanceledException>(() => canceled.Complete());
    }
}
