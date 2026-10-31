using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using Xunit;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class FrameWriterContractTests
{
    private static readonly TokenFamilyTarget Target = new(new("token"), new("binding"));
    private static TokenGeometrySample Geometry(bool independent = true) => new()
    {
        BranchId = new("branch"), LineId = new("line"), WithRuby = Target, BaseOnly = new(Target.TokenId, Target.BindingId),
        BaseGeometry = new() { AdvanceBounds = new(10, 20, 50, 80), InkBounds = new(12, 22, 48, 77), Baseline = new(10, 70),
            BaseWorldMatrix = Affine2.Identity, DrawUnits = [new("unit")], CanTransformIndependently = independent,
            DiagnosticCode = independent ? null : "UnsupportedGlyphPartition" },
        FamilyGeometry = new() { AdvanceBounds = new(10, 20, 50, 80), Baseline = new(10, 70), BaseWorldMatrix = Affine2.Identity },
    };

    [Fact]
    public void 固定参数不跟随外部字典且整数时长不损失精度或转换类型()
    {
        const long duration = 9_007_199_254_740_993;
        Dictionary<string, ParameterValue> values = new() { ["peak"] = new ParameterValue.Number(1.35),
            ["duration"] = new ParameterValue.Duration(duration), ["scope"] = new ParameterValue.Choice("branch") };
        ParameterValues fixedValues = new(values); values["peak"] = new ParameterValue.Number(9);
        Assert.Equal(1.35, fixedValues.GetNumber("peak")); Assert.Equal(duration, fixedValues.GetDuration("duration"));
        Assert.Equal("branch", fixedValues.GetEnum("scope"));
        Assert.Contains("duration", Assert.Throws<InvalidOperationException>(() => fixedValues.GetNumber("duration")).Message, StringComparison.Ordinal);
        Assert.Contains("missing", Assert.Throws<KeyNotFoundException>(() => fixedValues.GetNumber("missing")).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParameterValue.Number(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParameterValue.Duration(-1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 非法比例不会夹值或发布输出(double factor)
    {
        FrameWriter writer = new([Geometry()], EffectCapabilities.Transforms);
        Assert.Equal("InvalidTransform", Assert.Throws<EffectOutputException>(() => writer.Transforms.ScaleTokenFamily(Target, factor, Pivot.BaseAdvanceCenter)).Code);
        Assert.Empty(writer.Complete().TokenTransforms);
    }

    [Fact]
    public void 不能写出绑定范围或未声明能力也不能复用已结束writer()
    {
        FrameWriter writer = new([Geometry()], EffectCapabilities.Transforms);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => writer.Transforms.ScaleTokenFamily(
            new(Target.TokenId, new("another-binding")), 1.2, Pivot.BaseAdvanceCenter)).Code);
        FrameWriter undeclared = new([Geometry()], EffectCapabilities.None);
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => undeclared.Transforms.ScaleTokenFamily(Target, 1.2, Pivot.BaseAdvanceCenter)).Code);
        writer.Transforms.ScaleTokenFamily(Target, 1.2, Pivot.BaseAdvanceCenter); var output = writer.Complete();
        Assert.Single(output.TokenTransforms);
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => writer.Transforms.ScaleTokenFamily(Target, 2, Pivot.BaseAdvanceCenter)).Code);
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => writer.Complete()).Code);
        Assert.Equal(1.2, output.TokenTransforms.Single().LocalMatrix.A);
        undeclared.Invalidate(); undeclared.Invalidate();
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => undeclared.Complete()).Code);
    }

    [Fact]
    public void 共享字簇只允许恒等请求且缩放枢轴使用基础Advance()
    {
        FrameWriter shared = new([Geometry(false)], EffectCapabilities.Transforms);
        shared.Transforms.ScaleTokenFamily(Target, 1, Pivot.BaseAdvanceCenter);
        Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<EffectOutputException>(() => shared.Transforms.ScaleTokenFamily(Target, 1.2, Pivot.BaseAdvanceCenter)).Code);
        FrameWriter writer = new([Geometry()], EffectCapabilities.Transforms);
        writer.Transforms.ScaleTokenFamily(Target, 2, Pivot.BaseAdvanceCenter);
        writer.Transforms.ScaleTokenFamily(Target, .75, Pivot.BaseAdvanceCenter);
        var local = Assert.Single(writer.Complete().TokenTransforms).LocalMatrix;
        Assert.Equal(1.5, local.A); Assert.Equal(new Point2(30, 50), local.Map(new(30, 50)));
    }

    [Fact]
    public async Task 初始化缺参数是具名实例失败而取消保持取消类型()
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/NeedsParameter.cs", """
            using MakoKaraGenerator.Scripting;
            public sealed class NeedsParameter : EffectBase
            {
                public override void Initialize(EffectInitializationContext context) => context.Parameters.GetNumber("required");
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics));
        PureFrameSourceFactory factory = new([new(new("instance"), result, "NeedsParameter", ParameterValues.Empty,
            new EffectBinding.Project(), EffectCapabilities.None)], "fixed", static () => new EmptyGeometry());
        var snapshot = new ProjectSnapshot { Name = "init", EffectInputsFingerprint = "fixed" };
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await factory.CreateAsync(snapshot));
        Assert.Equal("Initialize", error.Stage); Assert.Equal(new EffectInstanceId("instance"), error.InstanceId);
        Assert.Contains("required", error.Message, StringComparison.Ordinal);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await factory.CreateAsync(snapshot, cancellation.Token));
    }

    private sealed class EmptyGeometry : ILineGeometryProvider
    {
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => null;
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
            => new() { Definition = SceneDefinitionBuilder.Build(snapshot), TimeUs = timeUs };
    }
}
