using Xunit;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class StyleWriterContractTests
{
    private static readonly TokenFamilyTarget Target = new(new("token"), new("binding"));
    private static readonly MaterialValue White = MaterialValue.Solid(new(1, 1, 1, 1));
    private static readonly MaterialValue Red = MaterialValue.Solid(new(1, 0, 0, 1));
    private static readonly StyleView Basis = new(White, White, new(2, White), new(new(0, 0, 0, .5)), .5);
    private static readonly BaselineStyles Baseline = new([new(Target.TokenId, Basis)]);
    private static TokenGeometrySample Geometry(bool independent = true) => new()
    {
        BranchId = new("branch"), LineId = new("line"), WithRuby = Target, BaseOnly = new(Target.TokenId, Target.BindingId),
        BaseGeometry = new() { AdvanceBounds = new(10, 20, 50, 80), Baseline = new(10, 70), BaseWorldMatrix = Affine2.Identity,
            CanTransformIndependently = independent, DiagnosticCode = independent ? null : "UnsupportedGlyphPartition" },
        FamilyGeometry = new() { AdvanceBounds = new(10, 20, 50, 80), Baseline = new(10, 70), BaseWorldMatrix = Affine2.Identity },
    };
    [Fact]
    public void 写入需要声明能力真实装载目标且结束后不能复用()
    {
        FrameWriter undeclared = new([Geometry()], EffectCapabilities.Transforms, Baseline);
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => undeclared.Style.SetSungMaterial(Target, Red)).Code);
        FrameWriter writer = new([Geometry()], EffectCapabilities.Style, Baseline);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => writer.Style.SetSungMaterial(new TokenFamilyTarget(Target.TokenId, new("wrong")), Red)).Code);
        writer.Style.SetSungMaterial(Target, Red);
        Assert.Equal(Red, Assert.Single(writer.Complete().TokenStyles).Update.Apply(Basis).SungMaterial);
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => writer.Style.SetShadow(Target, null)).Code);
        Assert.Equal(White, Baseline.Get(Target).SungMaterial);
    }
    [Fact]
    public void 未写入与显式关闭分开且后项Set可以恢复静态基线()
    {
        FrameWriter writer = new([Geometry()], EffectCapabilities.Style, Baseline);
        writer.Style.SetSungMaterial(Target, Red); writer.Style.SetSungMaterial(Target, White);
        writer.Style.SetStroke(Target, null); writer.Style.SetShadow(Target, null);
        writer.Style.MultiplyOpacity(Target, 2); writer.Style.MultiplyOpacity(Target, .25);
        var result = Assert.Single(writer.Complete().TokenStyles).Update.Apply(Basis);
        Assert.Equal(White, result.SungMaterial); Assert.Equal(White, result.UnsungMaterial);
        Assert.Null(result.Stroke); Assert.Null(result.Shadow); Assert.Equal(.25, result.Opacity);
        Assert.Equal(Basis, new StyleUpdate().Apply(Basis));
        Assert.Equal(White, new StyleUpdate(sung: Red).Then(new(sung: White)).Apply(Basis).SungMaterial);
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 非法alpha系数报告而不夹值(double value) => Assert.Throws<ArgumentOutOfRangeException>(() => new StyleUpdate(opacityFactor: value));
    [Fact]
    public void 最终alpha越界与系数组合溢出均失败()
    {
        Assert.Equal("InvalidStyleOpacity", Assert.Throws<EffectOutputException>(() => new StyleUpdate(opacityFactor: 3).Apply(Basis)).Code);
        Assert.Equal("InvalidStyleOpacity", Assert.Throws<EffectOutputException>(() => new StyleUpdate(opacityFactor: 1e300).Then(new(opacityFactor: 1e300))).Code);
    }
    [Fact]
    public void 共享字簇允许无变化写入但拒绝独立变色()
    {
        FrameWriter writer = new([Geometry(false)], EffectCapabilities.Style, Baseline);
        writer.Style.SetSungMaterial(Target, White);
        Assert.Single(writer.Complete().TokenStyles);
        FrameWriter changed = new([Geometry(false)], EffectCapabilities.Style, Baseline);
        Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<EffectOutputException>(() => changed.Style.SetSungMaterial(Target, Red)).Code);
    }
    [Fact]
    public void 渐变保持色标原序硬边与完整参数且不猜基色()
    {
        GradientValue value = new([new(0, new(1, 0, 0, 1)), new(.5, new(1, 0, 0, 1)), new(.5, new(0, 1, 0, 1)), new(1, new(0, 0, 1, .5))],
            GradientKind.Radial, 31, GradientCoordinateSpace.Canvas, GradientColorSpace.Hsv, GradientHuePath.Longer, .3, .7, .4, .8);
        Assert.Equal(value, new GradientValue([.. value.Stops], value.Kind, value.AngleDegrees, value.CoordinateSpace, value.ColorSpace,
            value.HuePath, value.CenterX, value.CenterY, value.RadiusX, value.RadiusY));
        Assert.Equal(value, ((MaterialValue.GradientMaterial)MaterialValue.Gradient(value)).Value);
        var style = new StyleView(White, MaterialValue.Gradient(value), null, null, 1);
        Assert.Equal("BaselineMaterialNotSolid", Assert.Throws<EffectOutputException>(() => style.RequireSungSolidColor()).Code);
        Assert.Throws<ArgumentException>(() => new GradientValue([new(0, default), new(.8, default), new(.2, default), new(1, default)]));
        Assert.Throws<ArgumentException>(() => new GradientValue([new(.1, default), new(1, default)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GradientStop(double.NaN, default));
    }
    [Fact]
    public void 包材质仅可引用固定资源修订()
    {
        var handle = new ResourceCatalog("package", "sha256:" + new string('a', 64), ["glass"], [], []).GetMaterial("glass");
        FrameWriter writer = new([Geometry()], EffectCapabilities.Style, Baseline);
        Assert.Equal("InvalidMaterialResource", Assert.Throws<EffectOutputException>(() => writer.Style.SetSungMaterial(Target,
            MaterialValue.PackageExport(handle, ParameterValues.Empty))).Code);
    }
}
