using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class V2MaterialUniformOutputContractTests
{
    private static readonly ResourceCatalog Resources = new("materials", "sha256:" + new string('a', 64), ["a", "b"], [], []);
    private static TextFrame Frame(bool independent = true)
    {
        ContentCatalog content = new([], [], [], []); SceneCatalog scene = new(content, [], [], [], []);
        var family = new TokenFamilyTarget(new("token"), new("binding")); var basic = new BaseTokenTarget(family.TokenId, family.BindingId);
        var line = new FrameLine(new("line"), new("branch"), family.BindingId, new("node"), new("group"), new("program"), null, null, DisplayPhase.Performing, 1);
        var geometry = new GeometrySample { AdvanceBounds = new(0, 0, 10, 10), Baseline = new(0, 8), BaseWorldMatrix = Affine2.Identity,
            CanTransformIndependently = independent, DiagnosticCode = independent ? null : "UnsupportedGlyphPartition" };
        TextFrameHostInputs legacy = new(1000, BaselineStyles.Empty,
            new() { LinesInBinding = [line], TokensInBinding = [new() { TokenId = family.TokenId, BranchId = line.BranchId,
                LineId = line.LineId, BindingId = family.BindingId, SourceText = "x", WithRuby = family, BaseOnly = basic,
                Performance = new(null, null, null, null, false, false, ProgressSource.Missing) }] },
            [new() { BranchId = line.BranchId, LineId = line.LineId, WithRuby = family, BaseOnly = basic, BaseGeometry = geometry, FamilyGeometry = geometry }],
            [new() { BranchId = line.BranchId, Target = line.Target, BaseGeometry = geometry, ProgressPath = new(0, 10, 8, 5) }],
            [], [], new EffectBinding.Project(), OutputMode.Opaque);
        var source = new DeclareContext(content, scene, ParameterValues.Empty, AudioFeatures.NotAnalyzed, Resources, 42, TimelineIndex.Empty, new(16, 16), new("test"));
        return new(source, legacy);
    }

    [Fact]
    public void 未绑定shader身份的uniform先写保留原始序列且替换不吞intrinsic错误()
    {
        var frame = Frame(); var target = frame.Geometry[0].WithRuby; var output = new TextOutput(frame, ["material.uniform", "style.fill"]);
        output.SetMaterialUniform(target, MaterialSlot.SungFill, Resources.GetMaterial("a"), "unknown-until-final", "amount", UniformValue.Float(.5));
        output.SetFill(target, TextState.Sung, MaterialValue.PackageExport(Resources.GetMaterial("b"), ParameterValues.Empty));
        Assert.Collection(output.Complete().Writes, write => Assert.IsType<TextWrite.MaterialUniform>(write), write => Assert.IsType<TextWrite.Fill>(write));
        var failed = new TextOutput(frame, ["material.uniform", "style.fill", "token.affine"]);
        var failure = Assert.Throws<EffectOutputException>(() => failed.SetMaterialUniform(target, MaterialSlot.SungFill, Resources.GetMaterial("a"), "shader", "amount", UniformValue.Float(1e100)));
        Assert.Equal("ShaderUniformNativeRange", failure.Code);
        Assert.Same(failure, Assert.Throws<EffectOutputException>(() => failed.SetFill(target, TextState.Sung, MaterialValue.Solid(new(1, 1, 1, 1)))));
        Assert.Same(failure, Assert.Throws<EffectOutputException>(() => failed.AppendTransform(target, Affine2.Identity)));
        Assert.Same(failure, Assert.Throws<EffectOutputException>(() => failed.Complete()));
    }

    [Theory]
    [InlineData("slot", "InvalidMaterialSlot")]
    [InlineData("name", "InvalidMaterialUniformMetadata")]
    [InlineData("cap", "UndeclaredCapability")]
    [InlineData("foreign", "InvalidMaterialResource")]
    [InlineData("shared", "UnsupportedGlyphPartition")]
    public void uniform写入目标资源能力及部位错误锁存全部文字writer(string kind, string code)
    {
        var frame = Frame(kind != "shared"); var target = frame.Geometry[0].WithRuby;
        var output = new TextOutput(frame, kind == "cap" ? ["style.fill"] : ["material.uniform", "style.fill"]);
        var foreign = new ResourceCatalog("materials", "sha256:" + new string('b', 64), ["a"], [], []);
        var error = Assert.Throws<EffectOutputException>(() => output.SetMaterialUniform(new TextTarget.Family(target),
            kind == "slot" ? (MaterialSlot)99 : MaterialSlot.SungFill, (kind == "foreign" ? foreign : Resources).GetMaterial("a"), "shader", kind == "name" ? " " : "amount", UniformValue.Float(.5)));
        Assert.Equal(code, error.Code); Assert.Same(error, Assert.Throws<EffectOutputException>(() => output.SetStrokes(target, TextState.Both)));
        Assert.Same(error, Assert.Throws<EffectOutputException>(() => output.SetWarp(target, null))); Assert.Same(error, Assert.Throws<EffectOutputException>(() => output.Complete()));
    }

    [Fact]
    public void BaseUniform保留真实Base身份并与Family作用域独立()
    {
        var frame = Frame(); var output = new TextOutput(frame, ["material.uniform"]);
        output.SetMaterialUniform(new TextTarget.Base(frame.Geometry[0].BaseOnly), MaterialSlot.SungFill,
            Resources.GetMaterial("a"), "shader", "amount", UniformValue.Float(.5));
        output.SetMaterialUniform(new TextTarget.Family(frame.Geometry[0].WithRuby), MaterialSlot.SungFill,
            Resources.GetMaterial("b"), "shader", "amount", UniformValue.Float(.75));
        var writes = output.Complete().Writes;
        Assert.IsType<TextTarget.Base>(Assert.IsType<TextWrite.MaterialUniform>(writes[0]).Target);
        Assert.IsType<TextTarget.Family>(Assert.IsType<TextWrite.MaterialUniform>(writes[1]).Target);
    }

    [Fact]
    public void Fill与uniform实际写入共用累计预算且完整参数及数组计费()
    {
        var frame = Frame(); var target = frame.Geometry[0].WithRuby; var output = new TextOutput(frame, ["material.uniform", "style.fill"]);
        for (int i = 0; i < 128; i++)
        {
            output.SetFill(target, TextState.Sung, MaterialValue.PackageExport(Resources.GetMaterial("a"), ParameterValues.Empty));
            output.SetMaterialUniform(target, MaterialSlot.SungFill, Resources.GetMaterial("a"), "shader", "amount", UniformValue.Float(.5));
        }
        Assert.Equal("GraphOutputBudgetExceeded", Assert.Throws<EffectOutputException>(() => output.SetFill(target, TextState.Sung, MaterialValue.Solid(new(1, 1, 1, 1)))).Code);
        Assert.Throws<EffectOutputException>(() => output.Complete());
        var data = new TextOutput(frame, ["style.fill"]);
        var parameters = new ParameterValues([new("text", new ParameterValue.Text(new string('x', 4 * 1024 * 1024)))]);
        Assert.Equal("GraphOutputBudgetExceeded", Assert.Throws<EffectOutputException>(() => data.SetFill(target, TextState.Sung, MaterialValue.PackageExport(Resources.GetMaterial("a"), parameters))).Code);
        Assert.Throws<EffectOutputException>(() => data.Complete());
        var array = new TextOutput(frame, ["material.uniform"]);
        array.SetMaterialUniform(target, MaterialSlot.SungFill, Resources.GetMaterial("a"), "shader", "amount", UniformValue.Array(Enumerable.Repeat(UniformValue.Float(.5), GraphBindingBudget.MaximumScalars)));
        Assert.Equal("GraphOutputBudgetExceeded", Assert.Throws<EffectOutputException>(() => array.SetMaterialUniform(target, MaterialSlot.SungFill, Resources.GetMaterial("a"), "shader", "amount", UniformValue.Float(.5))).Code);
        Assert.Throws<EffectOutputException>(() => array.Complete());
        using var cancellation = new CancellationTokenSource(); var canceled = new TextOutput(frame, ["material.uniform"]) { GraphCancellation = cancellation.Token };
        cancellation.Cancel(); Assert.Throws<OperationCanceledException>(() => canceled.SetMaterialUniform(target, MaterialSlot.SungFill, Resources.GetMaterial("a"), "shader", "amount", UniformValue.Float(.5)));
        Assert.Throws<OperationCanceledException>(() => canceled.Complete());
    }
}
