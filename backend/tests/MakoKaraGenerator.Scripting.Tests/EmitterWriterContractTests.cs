namespace MakoKaraGenerator.Scripting.Tests;

public sealed class EmitterWriterContractTests
{
    private static CursorEmitterDescriptor Descriptor => new()
    { AnchorMode = CursorAnchorMode.AdvanceCenter, RatePerSecond = 60, LifetimeUs = 600_000, SizePx = 4,
        Color = new(1, 0, 0, 1), SeedStreamKey = "trail" };
    [Fact]
    public void 独立发射阶段同身份最后描述生效且结束后不能再写()
    {
        EmitterWriter writer = new(new("instance"), EffectCapabilities.Overlay);
        writer.Set(new("cursor"), Descriptor);
        writer.Set(new("cursor"), Descriptor with { RatePerSecond = 30 });
        Assert.Equal(30, Assert.Single(writer.Complete()).Descriptor.RatePerSecond);
        Assert.Equal("InvalidEmissionOutputStage", Assert.Throws<EffectOutputException>(() => writer.Set(new("old"), Descriptor)).Code);
        Assert.Equal("InvalidEmissionOutputStage", Assert.Throws<EffectOutputException>(() => writer.Complete()).Code);
    }
    [Fact]
    public void 发射阶段须授权且旧视觉输出接口已移除()
    {
        EmitterWriter absent = new(new("instance"), EffectCapabilities.None);
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => absent.Set(new("cursor"), Descriptor)).Code);
        EmitterWriter closed = new(new("instance"), EffectCapabilities.Overlay); closed.Invalidate();
        Assert.Equal("InvalidEmissionOutputStage", Assert.Throws<EffectOutputException>(() => closed.Set(new("cursor"), Descriptor)).Code);
        Assert.Null(typeof(VisualWriter).GetProperty("Emitters"));
        Assert.Null(typeof(EmissionFrameContext).GetProperty("Particles"));
    }
    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-1)]
    public void 非有限和负发射率拒绝而不夹值(double rate)
        => Assert.Equal("InvalidEmitter", Assert.Throws<EffectOutputException>(() => (Descriptor with { RatePerSecond = rate }).Validate()).Code);
    [Fact]
    public void 寿命尺寸枚举和未完成策略均明确失败()
    {
        foreach (var descriptor in new[] { Descriptor with { LifetimeUs = 0 }, Descriptor with { SizePx = 0 }, Descriptor with { TrailDurationUs = -1 },
            Descriptor with { SeedStreamKey = "" }, Descriptor with { AnchorMode = (CursorAnchorMode)100 }, Descriptor with { SpreadPx = double.NaN } })
            Assert.Equal("InvalidEmitter", Assert.Throws<EffectOutputException>(descriptor.Validate).Code);
        (Descriptor with { TransitionMode = TransitionMode.Bridge, BridgeDurationUs = 180000, BridgeGapLimitUs = 2000000 }).Validate();
        Assert.Equal("EmitterTargetCapturePending", Assert.Throws<EffectOutputException>((Descriptor with { ImageOnlyPolicy = ImageOnlyPolicy.IncludeInTargetCapture }).Validate).Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParticleSimulationBudget(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParticleSimulationBudget(trailSampleLimit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParticleSimulationBudget(emissionRecordLimit: 0));
        Assert.Equal(100_000, ParticleSimulationBudget.Default.EmissionRecordLimit);
    }
    [Fact]
    public void 年龄曲线纯求值且保留非法外观结果给宿主报告()
    {
        ParticleAgeCurve fade = new(1, 0, new CurveValue.Easing(EasingKind.Linear)); Assert.Equal(.75, fade.Evaluate(.25));
        Assert.Equal(1, ParticleAgeCurve.ConstantOne.Evaluate(.9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParticleAgeCurve(double.NaN, 0, new CurveValue.Easing(EasingKind.Linear)));
    }
}
