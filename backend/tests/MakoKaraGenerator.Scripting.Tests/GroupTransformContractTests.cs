namespace MakoKaraGenerator.Scripting.Tests;

public sealed class GroupTransformContractTests
{
    private static readonly GroupTarget Target = new(new("group"));
    private static GroupGeometrySample Geometry() => new() { Target = Target, BaseWorldMatrix = Affine2.Identity,
        ReferenceBounds = new(0, 0, 100, 50), WorldPivot = new(50, 25), WorldInkCorners = [] };
    private static FrameWriter Writer(EffectCapabilities capabilities = EffectCapabilities.Transforms)
        => new([], capabilities, groupGeometry: [Geometry()]);

    [Fact]
    public void 组写入须有显式范围能力和有效阶段()
    {
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Writer(EffectCapabilities.Style)
            .Transforms.ApplyAffine(Target, Affine2.Identity)).Code);
        var writer = Writer();
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => writer.Transforms.ApplyAffine(new GroupTarget(new("old")), Affine2.Identity)).Code);
        writer.Transforms.ApplyAffine(Target, Affine2.Translation(10, 20));
        Assert.Equal(Target, Assert.Single(writer.Complete().GroupTransforms).Target);
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => writer.Transforms.ApplyAffine(Target, Affine2.Identity)).Code);
    }
    [Fact]
    public void 同组矩阵按调用右乘恒等与抵消省略输出()
    {
        var writer = Writer(); writer.Transforms.ApplyAffine(Target, Affine2.Translation(10, 20));
        writer.Transforms.ApplyAffine(Target, Affine2.ScaleAt(2, default));
        Assert.Equal(new Point2(12, 24), Assert.Single(writer.Complete().GroupTransforms).LocalMatrix.Map(new(1, 2)));
        var neutral = Writer(); neutral.Transforms.ApplyAffine(Target, Affine2.Identity);
        neutral.Transforms.ApplyAffine(Target, Affine2.Translation(10, 0)); neutral.Transforms.ApplyAffine(Target, Affine2.Translation(-10, 0));
        Assert.Empty(neutral.Complete().GroupTransforms);
    }
    [Fact]
    public void 不可逆和组合溢出不夹值失败writer不能复用()
    {
        var writer = Writer();
        Assert.Equal("InvalidTransform", Assert.Throws<EffectOutputException>(() => writer.Transforms.ApplyAffine(Target, default)).Code);
        var large = new Affine2(1e150, 0, 0, 1e150, 0, 0); writer.Transforms.ApplyAffine(Target, large);
        Assert.Equal("InvalidTransform", Assert.Throws<EffectOutputException>(() => writer.Transforms.ApplyAffine(Target, large)).Code);
        writer.Invalidate();
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => writer.Transforms.ApplyAffine(Target, Affine2.Identity)).Code);
    }
    [Fact]
    public void 组范围不接受重复空身份或未授权目标()
    {
        Assert.Throws<ArgumentException>(() => new FrameWriter([], EffectCapabilities.Transforms, groupGeometry: [Geometry(), Geometry()]));
        Assert.Throws<ArgumentException>(() => new FrameWriter([], EffectCapabilities.Transforms, groupGeometry: [Geometry() with { Target = new(default) }]));
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => new FrameWriter([], EffectCapabilities.Transforms)
            .Transforms.ApplyAffine(Target, Affine2.Identity)).Code);
    }
}
