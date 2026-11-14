using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class GroupTransformContractTests
{
    private static readonly GroupTarget Target = new(new("group"));
    private static TextOutput Output(params string[] capabilities)
    {
        var frame = V2PackageTestHelper.CreateTextFrame();
        return new(in frame, [.. capabilities.Length == 0 ? [CapabilityNames.GroupAffine] : capabilities], [Target]);
    }

    [Fact]
    public void 组写入须有显式范围能力和有效阶段()
    {
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Output(CapabilityNames.StyleFill)
            .AppendTransform(new TextTarget.Group(Target), Affine2.Identity)).Code);
        var output = Output();
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => output.AppendTransform(new TextTarget.Group(new(new("old"))), Affine2.Identity)).Code);
        output.AppendTransform(new TextTarget.Group(Target), Affine2.Translation(10, 20));
        var write = Assert.IsType<TextWrite.Transform>(Assert.Single(output.Complete().Writes));
        Assert.Equal(new TextTarget.Group(Target), write.Target); Assert.Equal(Affine2.Translation(10, 20), write.LocalMatrix);
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => output.AppendTransform(new TextTarget.Group(Target), Affine2.Identity)).Code);
    }
    [Fact]
    public void 同组矩阵按调用顺序记录并在内部校验溢出()
    {
        var output = Output();
        output.AppendTransform(new TextTarget.Group(Target), Affine2.Translation(10, 20));
        output.AppendTransform(new TextTarget.Group(Target), Affine2.ScaleAt(2, default));
        var writes = output.Complete().Writes;
        Assert.Equal(2, writes.Length);
        var combined = ((TextWrite.Transform)writes[1]).LocalMatrix.Compose(((TextWrite.Transform)writes[0]).LocalMatrix);
        Assert.Equal(new Point2(22, 44), combined.Map(new(1, 2)));
    }
    [Fact]
    public void 不可逆和组合溢出不夹值失败后不能复用()
    {
        var output = Output();
        Assert.Equal("InvalidTransform", Assert.Throws<EffectOutputException>(() => output.AppendTransform(new TextTarget.Group(Target), default)).Code);
        var large = new Affine2(1e20, 0, 0, 1e20, 0, 0); output.AppendTransform(new TextTarget.Group(Target), large);
        Assert.Equal("InvalidTransform", Assert.Throws<EffectOutputException>(() => output.AppendTransform(new TextTarget.Group(Target), large)).Code);
        output.Invalidate();
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => output.AppendTransform(new TextTarget.Group(Target), Affine2.Identity)).Code);
    }
    [Fact]
    public void 组范围不接受空身份或未授权目标()
    {
        var frame = V2PackageTestHelper.CreateTextFrame();
        Assert.Throws<ArgumentException>(() => new TextOutput(in frame, [CapabilityNames.GroupAffine], [new(default)]));
        Assert.Throws<ArgumentException>(() => new TextOutput(in frame, [CapabilityNames.GroupAffine], [new(new("missing"))]));
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => new TextOutput(in frame, [CapabilityNames.GroupAffine], [])
            .AppendTransform(new TextTarget.Group(Target), Affine2.Identity)).Code);
    }
}
