using System.Collections.Immutable;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class SpriteOverlayContractTests
{
    private static readonly ResourceCatalog Catalog = new("sprites", "sha256:" + new string('a', 64), [], [], ["spark.png"]);
    private static ImageHandle Image => Catalog.GetImage("spark.png");
    private static ImmutableArray<VisualOutputDeclaration> Declarations =>
    [
        new(new("image"), VisualPrimitiveKind.Sprites, OverlayLayer.AboveText),
        new(new("more"), VisualPrimitiveKind.Sprites, OverlayLayer.AboveText),
        new(new("bad"), VisualPrimitiveKind.Sprites, OverlayLayer.AboveText),
        new(new("late"), VisualPrimitiveKind.Sprites, OverlayLayer.AboveText),
    ];
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
        VisualOutput writer = new(new("fx"), [CapabilityNames.VisualsSprites, CapabilityNames.VisualsDots], Declarations, Catalog, new(1));
        writer.DrawSprites(new("image"), [new(Image, Affine2.Identity, default)], OverlayLayer.AboveText);
        Assert.Equal("UndeclaredPackageOutput", Assert.Throws<EffectOutputException>(() => writer.DrawDots(new("image"), [], OverlayLayer.AboveText)).Code);
        Assert.Equal("VisualOutputBudgetExceeded", Assert.Throws<EffectOutputException>(() => writer.DrawSprites(new("more"), [new(Image, Affine2.Identity, default)], OverlayLayer.AboveText)).Code);
        VisualOutput completed = new(new("fx"), [CapabilityNames.VisualsSprites], Declarations, Catalog, new(1));
        completed.DrawSprites(new("image"), [new(Image, Affine2.Identity, default)], OverlayLayer.AboveText);
        Assert.IsType<SpriteBatch>(Assert.Single(completed.Complete()));
        Assert.Equal("InvalidVisualOutputStage", Assert.Throws<EffectOutputException>(() => completed.DrawSprites(new("late"), [], OverlayLayer.AboveText)).Code);
        VisualOutput invalid = new(new("fx"), [CapabilityNames.VisualsSprites], Declarations, Catalog);
        Assert.Equal("InvalidOverlay", Assert.Throws<EffectOutputException>(() => invalid.DrawSprites(new("bad"), [default], OverlayLayer.AboveText)).Code);
        Assert.Throws<EffectOutputException>(() => invalid.DrawSprites(new("bad"), default, OverlayLayer.AboveText));
        VisualOutput none = new(new("fx"), [], Declarations, Catalog);
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => none.DrawSprites(new("bad"), [], OverlayLayer.AboveText)).Code);
    }
}
