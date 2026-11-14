using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class GroupOpacityContractTests
{
    private static readonly GroupTarget Target = new(new("group"));
    private static TextOutput Output(params string[] capabilities)
    {
        var frame = V2PackageTestHelper.CreateTextFrame();
        return new(in frame, [.. capabilities.Length == 0 ? [CapabilityNames.GroupOpacity] : capabilities], [Target]);
    }
    [Fact]
    public void 组透明度必须具备能力真实写范围及求值阶段()
    {
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Output(CapabilityNames.GroupAffine).MultiplyOpacity(new TextTarget.Group(Target), 1)).Code);
        var output = Output();
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => output.MultiplyOpacity(new TextTarget.Group(new(new("old"))), 1)).Code);
        output.MultiplyOpacity(new TextTarget.Group(Target), .5);
        var write = Assert.IsType<TextWrite.Opacity>(Assert.Single(output.Complete().Writes));
        Assert.Equal(new TextTarget.Group(Target), write.Target); Assert.Equal(.5, write.Factor);
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => output.MultiplyOpacity(new TextTarget.Group(Target), 1)).Code);
    }
    [Fact]
    public void 组因素记录有效范围并在零到一外立即拒绝()
    {
        var output = Output();
        output.MultiplyOpacity(new TextTarget.Group(Target), .5);
        output.MultiplyOpacity(new TextTarget.Group(Target), .25);
        var writes = output.Complete().Writes;
        Assert.Equal(2, writes.Length);
        Assert.Equal([.5, .25], writes.Cast<TextWrite.Opacity>().Select(w => w.Factor));
    }
    [Theory]
    [InlineData(-1)] [InlineData(2)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void 非法组因素不夹值(double factor) => Assert.Equal("InvalidStyleOpacity",
        Assert.Throws<EffectOutputException>(() => Output().MultiplyOpacity(new TextTarget.Group(Target), factor)).Code);
}
