using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class LineTransformContractTests
{
    private static readonly LineTarget Target = new(new("line"), new("binding"));
    private static TextOutput Output(EffectBinding? binding = null, bool includeLine = true, params string[] capabilities)
    {
        var frame = V2PackageTestHelper.CreateTextFrame(independent: false, binding: binding, includeLine: includeLine);
        return new(in frame, [.. capabilities.Length == 0 ? [CapabilityNames.LineAffine] : capabilities]);
    }
    [Fact]
    public void 整行变换需要真实行装载能力和有效阶段但不要求拆开共享字簇()
    {
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Output(capabilities: [CapabilityNames.StyleFill])
            .AppendTransform(new TextTarget.Line(Target), Affine2.Identity)).Code);
        TextOutput output = Output();
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => output.AppendTransform(new TextTarget.Line(Target with { BindingId = new("old") }), Affine2.Identity)).Code);
        output.AppendTransform(new TextTarget.Line(Target), Affine2.Translation(10, 20));
        var write = Assert.IsType<TextWrite.Transform>(Assert.Single(output.Complete().Writes));
        Assert.Equal(new TextTarget.Line(Target), write.Target); Assert.Equal(Affine2.Translation(10, 20), write.LocalMatrix);
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => output.AppendTransform(new TextTarget.Line(Target), Affine2.Identity)).Code);
    }
    [Fact]
    public void 同行局部矩阵按顺序记录并在内部校验组合()
    {
        TextOutput output = Output();
        output.AppendTransform(new TextTarget.Line(Target), Affine2.Translation(10, 20));
        output.AppendTransform(new TextTarget.Line(Target), Affine2.ScaleAt(2, default));
        var writes = output.Complete().Writes;
        Assert.Equal(2, writes.Length);
        var combined = ((TextWrite.Transform)writes[1]).LocalMatrix.Compose(((TextWrite.Transform)writes[0]).LocalMatrix);
        Assert.Equal(new Point2(22, 44), combined.Map(new(1, 2)));
    }
    [Fact]
    public void 零矩阵和组合溢出不夹值且失效后不能复用()
    {
        TextOutput output = Output();
        Assert.Equal("InvalidTransform", Assert.Throws<EffectOutputException>(() => output.AppendTransform(new TextTarget.Line(Target), default)).Code);
        var large = new Affine2(1e20, 0, 0, 1e20, 0, 0); output.AppendTransform(new TextTarget.Line(Target), large);
        Assert.Equal("InvalidTransform", Assert.Throws<EffectOutputException>(() => output.AppendTransform(new TextTarget.Line(Target), large)).Code);
        output.Invalidate();
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => output.AppendTransform(new TextTarget.Line(Target), Affine2.Identity)).Code);
    }
    [Fact]
    public void 元素范围不获得行变换写权且无行几何不能写行()
    {
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => Output(includeLine: false)
            .AppendTransform(new TextTarget.Line(Target), Affine2.Identity)).Code);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => Output(binding: new EffectBinding.Tokens([new("token")]))
            .AppendTransform(new TextTarget.Line(Target), Affine2.Identity)).Code);
    }
}
