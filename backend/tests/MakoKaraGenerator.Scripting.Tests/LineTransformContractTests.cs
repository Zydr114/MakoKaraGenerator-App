namespace MakoKaraGenerator.Scripting.Tests;

public sealed class LineTransformContractTests
{
    private static readonly LineTarget Target = new(new("line"), new("binding"));
    private static LineGeometrySample Geometry() => new() { Target = Target, BranchId = new("branch"), ProgressPath = new(0, 50, 55, 40),
        BaseGeometry = new() { BaseWorldMatrix = Affine2.Identity, AdvanceBounds = new(0, 20, 50, 60), Baseline = new(0, 55),
            CanTransformIndependently = false, DiagnosticCode = "UnsupportedGlyphPartition" } };
    private static FrameWriter Writer(EffectCapabilities capability = EffectCapabilities.Transforms)
        => new([], capability, lineGeometry: [Geometry()]);
    [Fact]
    public void 整行变换需要真实行装载能力和有效阶段但不要求拆开共享字簇()
    {
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Writer(EffectCapabilities.Style)
            .Transforms.ApplyAffine(Target, Affine2.Identity)).Code);
        FrameWriter writer = Writer();
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => writer.Transforms.ApplyAffine(Target with { BindingId = new("old") }, Affine2.Identity)).Code);
        writer.Transforms.ApplyAffine(Target, Affine2.Translation(10, 20));
        Assert.Equal(Target, Assert.Single(writer.Complete().LineTransforms).Target);
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => writer.Transforms.ApplyAffine(Target, Affine2.Identity)).Code);
    }
    [Fact]
    public void 同行局部矩阵按顺序组合恒等和抵消均不制造输出()
    {
        FrameWriter writer = Writer(); writer.Transforms.ApplyAffine(Target, Affine2.Translation(10, 20));
        writer.Transforms.ApplyAffine(Target, Affine2.ScaleAt(2, default));
        Assert.Equal(new Point2(12, 24), Assert.Single(writer.Complete().LineTransforms).LocalMatrix.Map(new(1, 2)));
        FrameWriter neutral = Writer(); neutral.Transforms.ApplyAffine(Target, Affine2.Identity);
        neutral.Transforms.ApplyAffine(Target, Affine2.Translation(10, 0)); neutral.Transforms.ApplyAffine(Target, Affine2.Translation(-10, 0));
        Assert.Empty(neutral.Complete().LineTransforms);
    }
    [Fact]
    public void 零矩阵和组合溢出不夹值且结束失败writer不能复用()
    {
        FrameWriter writer = Writer();
        Assert.Equal("InvalidTransform", Assert.Throws<EffectOutputException>(() => writer.Transforms.ApplyAffine(Target, default)).Code);
        var large = new Affine2(1e150, 0, 0, 1e150, 0, 0); writer.Transforms.ApplyAffine(Target, large);
        Assert.Equal("InvalidTransform", Assert.Throws<EffectOutputException>(() => writer.Transforms.ApplyAffine(Target, large)).Code);
        writer.Invalidate();
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => writer.Transforms.ApplyAffine(Target, Affine2.Identity)).Code);
    }
    [Fact]
    public void 输出范围不默默去重或接受空装载身份()
    {
        Assert.Throws<ArgumentException>(() => new FrameWriter([], EffectCapabilities.Transforms, lineGeometry: [Geometry(), Geometry()]));
        Assert.Throws<ArgumentException>(() => new FrameWriter([], EffectCapabilities.Transforms, lineGeometry: [Geometry() with { Target = Target with { BindingId = default } }]));
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => new FrameWriter([], EffectCapabilities.Transforms)
            .Transforms.ApplyAffine(Target, Affine2.Identity)).Code);
    }
}
