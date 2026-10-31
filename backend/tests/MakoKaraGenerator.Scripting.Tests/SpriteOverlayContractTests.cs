namespace MakoKaraGenerator.Scripting.Tests;

public sealed class SpriteOverlayContractTests
{
    private static ImageHandle Image => new ResourceCatalog("sprites", "sha256:" + new string('a', 64), [], [], ["spark.png"]).GetImage("spark.png");
    [Fact]
    public void 精灵要求真实句柄非空图片内裁片及可逆有限世界几何()
    {
        Assert.False(default(Sprite).IsValid);
        Assert.Throws<ArgumentException>(() => new Sprite(null!, Affine2.Identity, default));
        Assert.Throws<ArgumentException>(() => new Sprite(Image, default, default));
        Assert.Throws<ArgumentException>(() => new Sprite(Image, Affine2.Identity, default, new(0, 0, 0, 1)));
        Assert.Throws<ArgumentException>(() => new Sprite(Image, Affine2.Identity, default, new(-.1, 0, 1, 1)));
        Assert.Throws<ArgumentException>(() => new Sprite(Image, new(double.MaxValue, 0, 0, 1, double.MaxValue, 0), default));
        var mirror = new Sprite(Image, new(-20, 0, 0, 10, 30, 40), new(1, 1, 1, .5), new(.5, 0, 1, 1));
        Assert.True(mirror.IsValid); Assert.Equal(new Point2(10, 50), mirror.UnitToWorld.Map(new(1, 1)));
    }
    [Fact]
    public void 精灵沿用混合名字预算能力与过期门禁透明项同样计费()
    {
        VisualWriter writer = new(new("fx"), EffectCapabilities.Overlay, new(1));
        writer.Overlay.DrawSprites(new("image"), [new(Image, Affine2.Identity, default)], OverlayLayer.AboveText);
        Assert.Equal("DuplicateOverlayBatch", Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawDots(new("image"), [], OverlayLayer.AboveText)).Code);
        Assert.Equal("VisualOutputBudgetExceeded", Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawSprites(new("more"), [new(Image, Affine2.Identity, default)], OverlayLayer.AboveText)).Code);
        Assert.IsType<SpriteBatch>(Assert.Single(writer.CompleteOverlay()));
        Assert.Equal("InvalidVisualOutputStage", Assert.Throws<EffectOutputException>(() => writer.Overlay.DrawSprites(new("late"), [], OverlayLayer.AboveText)).Code);
        VisualWriter invalid = new(new("fx"), EffectCapabilities.Overlay);
        Assert.Equal("InvalidOverlay", Assert.Throws<EffectOutputException>(() => invalid.Overlay.DrawSprites(new("bad"), [default], OverlayLayer.AboveText)).Code);
        Assert.Throws<EffectOutputException>(() => invalid.Overlay.DrawSprites(new("bad"), default, OverlayLayer.AboveText));
        VisualWriter none = new(new("fx"), EffectCapabilities.None);
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => none.Overlay.DrawSprites(new("bad"), [], OverlayLayer.AboveText)).Code);
    }
}
