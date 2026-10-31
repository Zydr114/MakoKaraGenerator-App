namespace MakoKaraGenerator.Scripting.Tests;

public sealed class GroupOpacityContractTests
{
    private static readonly GroupTarget Target = new(new("group"));
    private static FrameWriter Writer(EffectCapabilities capabilities = EffectCapabilities.Style) => new([], capabilities,
        groupGeometry: [new() { Target = Target, BaseWorldMatrix = Affine2.Identity, ReferenceBounds = new(0, 0, 100, 50), WorldPivot = new(50, 25), WorldInkCorners = [] }]);
    [Fact]
    public void 组透明度必须具备能力真实写范围及求值阶段()
    {
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Writer(EffectCapabilities.Transforms).Style.MultiplyOpacity(Target, 1)).Code);
        var writer = Writer();
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => writer.Style.MultiplyOpacity(new GroupTarget(new("old")), 1)).Code);
        writer.Style.MultiplyOpacity(Target, .5);
        Assert.Equal(new GroupOpacity(Target, .5), Assert.Single(writer.Complete().GroupOpacities));
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => writer.Style.MultiplyOpacity(Target, 1)).Code);
    }
    [Fact]
    public void 同组因素按调用相乘允许中间大于一但不允许溢出()
    {
        var writer = Writer(); writer.Style.MultiplyOpacity(Target, 2); writer.Style.MultiplyOpacity(Target, .25);
        Assert.Equal(.5, Assert.Single(writer.Complete().GroupOpacities).Factor);
        var neutral = Writer(); neutral.Style.MultiplyOpacity(Target, 2); neutral.Style.MultiplyOpacity(Target, .5);
        Assert.Empty(neutral.Complete().GroupOpacities);
        var large = Writer(); large.Style.MultiplyOpacity(Target, double.MaxValue);
        Assert.Equal("InvalidStyleOpacity", Assert.Throws<EffectOutputException>(() => large.Style.MultiplyOpacity(Target, 2)).Code);
    }
    [Theory]
    [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void 非法组因素不夹值(double factor) => Assert.Equal("InvalidStyleOpacity",
        Assert.Throws<EffectOutputException>(() => Writer().Style.MultiplyOpacity(Target, factor)).Code);
}
