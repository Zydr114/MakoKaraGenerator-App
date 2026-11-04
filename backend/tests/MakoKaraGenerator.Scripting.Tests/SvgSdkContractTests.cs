using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Particles.V2;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class SvgSdkContractTests
{
    private static readonly EffectInstanceId Owner = new("svg-owner");
    private static readonly ColorRgba White = new(1, 1, 1, 1);
    private static readonly ImmutableArray<string> Capabilities =
        ["visuals.dots", "visuals.segments", "visuals.trails", "visuals.sprites", "visuals.paths", "resources.svg.vector", "resources.svg.atlas"];
    private static readonly ImmutableArray<VisualOutputDeclaration> Declarations =
        [new(new("paths"), VisualPrimitiveKind.Paths, OverlayLayer.AboveText, VisualResourceKind.SvgVector),
         new(new("atlas"), VisualPrimitiveKind.Sprites, OverlayLayer.AboveText, VisualResourceKind.SvgAtlas),
         new(new("images"), VisualPrimitiveKind.Sprites, OverlayLayer.AboveText)];

    [Fact]
    public void 目录保留旧图片入口且SVG身份和档位结构化分离()
    {
        var resources = Resources();
        var vector = resources.GetVector("star-vector");
        var atlas = resources.GetSprite("star-atlas", "tier:large");
        Assert.Equal("star-vector", vector.Identity.ExportId);
        Assert.Equal("star-atlas", atlas.Identity.ExportId);
        Assert.Equal("tier:large", atlas.VariantId);
        Assert.Equal(Revision('a'), vector.Identity.Revision);
        Assert.True(resources.Contains(vector)); Assert.True(resources.Contains(atlas));
        Assert.True(resources.Contains(resources.GetImage("image.png")));
        Assert.NotEqual(atlas, resources.GetSprite("star-atlas", "small"));
        Assert.Throws<KeyNotFoundException>(() => resources.GetVector("star-atlas"));
        Assert.Throws<KeyNotFoundException>(() => resources.GetImage("star-vector"));
        Assert.Throws<KeyNotFoundException>(() => resources.GetSprite("star-atlas", "absent"));
        Assert.Throws<KeyNotFoundException>(() => ResourceCatalog.Empty.GetVector("star-vector"));
        Assert.False(ResourceCatalog.Empty.Contains(vector)); Assert.False(ResourceCatalog.Empty.Contains(atlas));
        var legacy = new ResourceCatalog("legacy", Revision('a'), [], [], ["image.png"]);
        Assert.True(legacy.Contains(legacy.GetImage("image.png")));
        Assert.Throws<KeyNotFoundException>(() => legacy.GetVector("star-vector"));
        Assert.Empty(typeof(VectorHandle).GetConstructors()); Assert.Empty(typeof(SpriteHandle).GetConstructors());
        Assert.Equal(["Identity"], typeof(VectorHandle).GetProperties().Select(property => property.Name));
        Assert.Equal(["Identity", "VariantId"], typeof(SpriteHandle).GetProperties().Select(property => property.Name));
        var structured = new ResourceCatalog("svg", Revision('a'), [], [], [], [],
            ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("one:two", ["three"]).Add("one", ["two:three"]));
        Assert.NotEqual(structured.GetSprite("one:two", "three"), structured.GetSprite("one", "two:three"));
    }

    [Fact]
    public void 目录拒绝默认集合空身份重复档位和跨SVG模式冲突()
    {
        ResourceCatalog Catalog(ImmutableArray<string> vectors, ImmutableDictionary<string, ImmutableArray<string>> sprites)
            => new("svg", Revision('a'), [], [], [], vectors, sprites);
        Assert.Throws<ArgumentException>(() => Catalog(default, Sprites()));
        Assert.Throws<ArgumentException>(() => Catalog([""], Sprites()));
        Assert.Throws<ArgumentException>(() => Catalog(["one", "one"], Sprites()));
        Assert.Throws<ArgumentException>(() => Catalog(["star-atlas"], Sprites()));
        Assert.Throws<ArgumentNullException>(() => Catalog([], null!));
        foreach (ImmutableArray<string> variants in new ImmutableArray<string>[] { default, [], [""], ["x", "x"] })
            Assert.Throws<ArgumentException>(() => Catalog([], ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("atlas", variants)));
        Assert.Throws<ArgumentException>(() => Catalog([], ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add(" ", ["small"])));
        Assert.Throws<ArgumentException>(() => new ResourceCatalog("svg", "sha256:short", [], [], [], [], Sprites()));
    }

    [Fact]
    public void owning检查完整包修订资源集合和精确档位而非句柄来源对象()
    {
        var resources = Resources();
        Assert.True(resources.Contains(Resources().GetVector("star-vector")));
        Assert.True(resources.Contains(Resources().GetSprite("star-atlas", "small")));
        foreach (var other in new[] { Resources(revision: 'b'), Resources(packageId: "other") })
        {
            Assert.False(resources.Contains(other.GetVector("star-vector")));
            Assert.False(resources.Contains(other.GetSprite("star-atlas", "small")));
        }
        var invented = new ResourceCatalog("svg", Revision('a'), [], [], [], ["invented"],
            Sprites().SetItem("star-atlas", ["invented"]));
        Assert.False(resources.Contains(invented.GetVector("invented")));
        Assert.False(resources.Contains(invented.GetSprite("star-atlas", "invented")));
        Assert.False(resources.Contains((VectorHandle)null!)); Assert.False(resources.Contains((SpriteHandle)null!));
    }

    [Fact]
    public void SVG值保留镜像非等比和斜切并拒绝默认奇异与四角溢出()
    {
        var resources = Resources();
        var affine = new Affine2(-2, 1, 3, 4, 10, 20);
        var path = new VectorVisual(resources.GetVector("star-vector"), affine, new(.2, .3, .4, .5));
        var atlas = new AtlasSprite(resources.GetSprite("star-atlas", "small"), affine, default);
        Assert.True(path.IsValid); Assert.True(atlas.IsValid);
        Assert.Equal(affine, path.UnitToWorld); Assert.Equal(affine, atlas.UnitToWorld);
        Assert.Equal(new Point2(11, 25), path.UnitToWorld.Map(new(1, 1)));
        Assert.False(default(VectorVisual).IsValid); Assert.False(default(AtlasSprite).IsValid);
        Assert.Throws<ArgumentException>(() => new VectorVisual(null!, Affine2.Identity, White));
        Assert.Throws<ArgumentException>(() => new AtlasSprite(null!, Affine2.Identity, White));
        foreach (var invalid in new[] { default(Affine2), new Affine2(1, 2, 2, 4, 0, 0),
            new Affine2(double.MaxValue, 0, 0, 1, double.MaxValue, 0) })
        {
            Assert.Throws<ArgumentException>(() => new VectorVisual(path.Vector, invalid, White));
            Assert.Throws<ArgumentException>(() => new AtlasSprite(atlas.Sprite, invalid, White));
        }
        Assert.Null(typeof(VectorVisual).GetProperty("Source")); Assert.Null(typeof(AtlasSprite).GetProperty("Source"));
        Assert.False(new PathBatch(Owner, new("paths"), OverlayLayer.AboveText, default).IsValid);
        Assert.False(new AtlasSpriteBatch(Owner, new("atlas"), OverlayLayer.AboveText, [default]).IsValid);
        Assert.True(new PathBatch(Owner, new("paths"), OverlayLayer.AboveText, []).IsValid);
    }

    [Fact]
    public void Prepare必须同时核验包与同名canvas的绘制和资源能力()
    {
        var prepared = new Data { VisualOutputs = Declarations };
        var outputs = Canvas();
        Validate(prepared, Capabilities, outputs);
        foreach (string missing in new[] { "visuals.paths", "resources.svg.vector", "visuals.sprites", "resources.svg.atlas" })
        {
            Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() =>
                Validate(prepared, [.. Capabilities.Where(capability => capability != missing)], outputs)).Code);
            var incomplete = outputs.Select(output => output with { Requires = [.. output.Requires.Where(capability => capability != missing)] }).ToImmutableArray();
            Assert.Equal("UndeclaredPackageOutput", Assert.Throws<EffectOutputException>(() => Validate(prepared, Capabilities, incomplete)).Code);
        }
        var split = ImmutableArray.Create(
            new CanvasOutputDeclaration(new("paths"), ["visuals.paths"]),
            new CanvasOutputDeclaration(new("other"), ["resources.svg.vector"]));
        Assert.Equal("UndeclaredPackageOutput", Assert.Throws<EffectOutputException>(() =>
            Validate(new Data { VisualOutputs = [Declarations[0]] }, Capabilities, split)).Code);
    }

    [Fact]
    public void Prepare二次核验不能以with绕过封闭资源类型()
    {
        foreach (var declaration in new[]
        {
            Declarations[0] with { Resource = VisualResourceKind.None },
            Declarations[0] with { Resource = VisualResourceKind.SvgAtlas },
            Declarations[1] with { Resource = VisualResourceKind.SvgVector },
            Declarations[1] with { Resource = (VisualResourceKind)999 },
            Declarations[1] with { Primitive = VisualPrimitiveKind.Dots },
        })
        {
            Assert.Equal("InvalidVisualDeclaration", Assert.Throws<EffectOutputException>(() =>
                Validate(new Data { VisualOutputs = [declaration] }, Capabilities, Canvas())).Code);
            Assert.Equal("InvalidVisualDeclaration", Assert.Throws<EffectOutputException>(() =>
                new VisualOutput(Owner, Capabilities, [declaration], Resources())).Code);
        }
    }

    [Fact]
    public void Writer复核两类能力精确目录归属与声明资源类型()
    {
        var resources = Resources(); var foreign = Resources(revision: 'b');
        var path = new VectorVisual(resources.GetVector("star-vector"), Affine2.Identity, White);
        var atlas = new AtlasSprite(resources.GetSprite("star-atlas", "small"), Affine2.Identity, White);
        foreach (string missing in new[] { "visuals.paths", "resources.svg.vector" })
        {
            var output = Output(resources, [.. Capabilities.Where(capability => capability != missing)]);
            Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => output.DrawPaths(new("paths"), [path], OverlayLayer.AboveText)).Code);
        }
        foreach (string missing in new[] { "visuals.sprites", "resources.svg.atlas" })
        {
            var output = Output(resources, [.. Capabilities.Where(capability => capability != missing)]);
            Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => output.DrawAtlasSprites(new("atlas"), [atlas], OverlayLayer.AboveText)).Code);
        }
        var writer = Output(resources);
        Assert.Equal("InvalidVectorResource", Assert.Throws<EffectOutputException>(() => writer.DrawPaths(new("paths"),
            [new(foreign.GetVector("star-vector"), Affine2.Identity, White)], OverlayLayer.AboveText)).Code);
        Assert.Equal("InvalidSpriteResource", Assert.Throws<EffectOutputException>(() => writer.DrawAtlasSprites(new("atlas"),
            [new(foreign.GetSprite("star-atlas", "small"), Affine2.Identity, White)], OverlayLayer.AboveText)).Code);
        Assert.Equal("UndeclaredPackageOutput", Assert.Throws<EffectOutputException>(() => writer.DrawAtlasSprites(new("images"), [atlas], OverlayLayer.AboveText)).Code);
        Assert.Equal("UndeclaredPackageOutput", Assert.Throws<EffectOutputException>(() => writer.DrawSprites(new("atlas"),
            [new(resources.GetImage("image.png"), Affine2.Identity, White)], OverlayLayer.AboveText)).Code);
        Assert.Equal("UndeclaredPackageOutput", Assert.Throws<EffectOutputException>(() => writer.DrawPaths(new("paths"), [path], OverlayLayer.BehindText)).Code);
        Assert.Equal("InvalidOverlay", Assert.Throws<EffectOutputException>(() => writer.DrawPaths(new("paths"), default, OverlayLayer.AboveText)).Code);
        Assert.Equal("InvalidOverlay", Assert.Throws<EffectOutputException>(() => writer.DrawAtlasSprites(new("atlas"), [default], OverlayLayer.AboveText)).Code);
        writer.DrawPaths(new("paths"), [path], OverlayLayer.AboveText);
        writer.DrawAtlasSprites(new("atlas"), [atlas], OverlayLayer.AboveText);
        Assert.Equal("DuplicateOverlayBatch", Assert.Throws<EffectOutputException>(() => writer.DrawPaths(new("paths"), [], OverlayLayer.AboveText)).Code);
        var batches = writer.Complete(); Assert.Equal(["atlas", "paths"], batches.Select(batch => batch.Id.Value));
        Assert.IsType<AtlasSpriteBatch>(batches[0]); Assert.IsType<PathBatch>(batches[1]);
        Assert.Equal("InvalidVisualOutputStage", Assert.Throws<EffectOutputException>(() => writer.DrawPaths(new("paths"), [], OverlayLayer.AboveText)).Code);
    }

    [Fact]
    public void 跨SVG类型同名输出拒绝且透明实例计入统一draw预算()
    {
        var resources = Resources();
        Assert.Equal("InvalidVisualDeclaration", Assert.Throws<EffectOutputException>(() => new VisualOutput(Owner, Capabilities,
            [Declarations[0], Declarations[1] with { Id = Declarations[0].Id }], resources)).Code);
        var output = new VisualOutput(Owner, Capabilities, Declarations, resources, new(1));
        output.DrawPaths(new("paths"), [new(resources.GetVector("star-vector"), Affine2.Identity, default)], OverlayLayer.AboveText);
        var failure = Assert.Throws<EffectOutputException>(() => output.DrawAtlasSprites(new("atlas"),
            [new(resources.GetSprite("star-atlas", "small"), Affine2.Identity, default)], OverlayLayer.AboveText));
        Assert.Equal("VisualOutputBudgetExceeded", failure.Code);
        Assert.Same(failure, Assert.Throws<EffectOutputException>(() => output.Complete()));
        var recovered = new VisualOutput(Owner, Capabilities, Declarations, resources, new(1));
        recovered.DrawPaths(new("paths"), [new(resources.GetVector("star-vector"), Affine2.Identity, default)], OverlayLayer.AboveText);
        Assert.Single(recovered.Complete());
    }

    [Fact]
    public void 统一字节账覆盖全部六类与拖尾真实节点并在发布批次前预留()
    {
        var resources = Resources();
        var point = new TrailPoint(default, 1, White);
        OverlayBatch[] batches =
        [new DotBatch(Owner, new("dots"), OverlayLayer.AboveText, [new(default, 1, White)]),
         new SegmentBatch(Owner, new("segments"), OverlayLayer.AboveText, [new(default, new(1, 1), 1, White)]),
         new TrailBatch(Owner, new("trails"), OverlayLayer.AboveText, [new([point, point, point]), new([point, point])]),
         new SpriteBatch(Owner, new("images"), OverlayLayer.AboveText, [new(resources.GetImage("image.png"), Affine2.Identity, White)]),
         new PathBatch(Owner, new("paths"), OverlayLayer.AboveText, [new(resources.GetVector("star-vector"), Affine2.Identity, default)]),
         new AtlasSpriteBatch(Owner, new("atlas"), OverlayLayer.AboveText, [new(resources.GetSprite("star-atlas", "small"), Affine2.Identity, default)])];
        Assert.Equal(new long[] { 320, 336, 704, 384, 368, 368 }, batches.Select(OverlayMemoryCost.Estimate));
        Assert.Throws<ArgumentException>(() => OverlayMemoryCost.Estimate(new PathBatch(Owner, new("bad"), OverlayLayer.AboveText, default)));
        List<long> bytes = []; bool reject = true;
        var output = new VisualOutput(Owner, Capabilities, Declarations, resources, budget: null, reserveBytes: value =>
        {
            if (reject) throw new EffectOutputException("TestSharedFrameMemoryExceeded", "本次共享帧预算不足。");
            bytes.Add(value);
        });
        var paths = Assert.IsType<PathBatch>(batches[4]).Paths;
        var failure = Assert.Throws<EffectOutputException>(() => output.DrawPaths(new("paths"), paths, OverlayLayer.AboveText));
        Assert.Equal("TestSharedFrameMemoryExceeded", failure.Code);
        reject = false;
        Assert.Same(failure, Assert.Throws<EffectOutputException>(() => output.DrawPaths(new("paths"), paths, OverlayLayer.AboveText)));
        Assert.Same(failure, Assert.Throws<EffectOutputException>(() => output.Complete()));
        Assert.Empty(bytes);
        var recovered = new VisualOutput(Owner, Capabilities, Declarations, resources, budget: null, reserveBytes: bytes.Add);
        recovered.DrawPaths(new("paths"), paths, OverlayLayer.AboveText);
        Assert.Equal([368L], bytes); Assert.Single(recovered.Complete());
        Assert.Empty(Output(resources).Complete());
    }

    [Fact]
    public void 大拖尾字节失败锁存整帧且下一writer合法输出可恢复()
    {
        var point = new TrailPoint(default, 1, default);
        var trails = Enumerable.Repeat(new Trail([point, point]), 349_526).ToImmutableArray();
        var output = new VisualOutput(Owner, Capabilities,
            [new(new("trail"), VisualPrimitiveKind.Trails, OverlayLayer.AboveText)], ResourceCatalog.Empty, new(int.MaxValue));
        var failure = Assert.Throws<EffectOutputException>(() => output.DrawTrails(new("trail"), trails, OverlayLayer.AboveText));
        Assert.Equal("VisualOutputMemoryExceeded", failure.Code);
        Assert.Same(failure, Assert.Throws<EffectOutputException>(() => output.DrawTrails(new("trail"), [], OverlayLayer.AboveText)));
        Assert.Same(failure, Assert.Throws<EffectOutputException>(() => output.Complete()));
        var recovered = new VisualOutput(Owner, Capabilities,
            [new(new("trail"), VisualPrimitiveKind.Trails, OverlayLayer.AboveText)], ResourceCatalog.Empty, new(int.MaxValue));
        recovered.DrawTrails(new("trail"), [], OverlayLayer.AboveText);
        Assert.Empty(Assert.IsType<TrailBatch>(Assert.Single(recovered.Complete())).Trails);
    }

    [Fact]
    public void 共享字节账只计每批一次且第二实例吞掉预算异常仍保留实际失败owner()
    {
        var resources = Resources(); var shared = new ParticleQueryBudget(byteLimit: 700);
        var firstOwner = new ParticleOwner(new("first-owner"), new("svg", Revision('a'), "first"), "compiled-first", 42, "inputs", "prepared");
        var secondOwner = firstOwner with { InstanceId = new("second-owner"), PackageIdentity = new("svg", Revision('a'), "second"), RuntimeHash = "compiled-second" };
        var path = new VectorVisual(resources.GetVector("star-vector"), Affine2.Identity, White);
        var first = new VisualOutput(firstOwner.InstanceId, Capabilities, Declarations, resources, budget: null,
            reserveBytes: bytes => shared.ReserveBytes(firstOwner, bytes, 123));
        var second = new VisualOutput(secondOwner.InstanceId, Capabilities, Declarations, resources, budget: null,
            reserveBytes: bytes => shared.ReserveBytes(secondOwner, bytes, 123));
        first.DrawPaths(new("paths"), [path], OverlayLayer.AboveText);
        var failure = Assert.Throws<EffectFailureException>(() => second.DrawPaths(new("paths"), [path], OverlayLayer.AboveText));
        Assert.Equal("ParticleFrameMemoryExceeded", failure.Code);
        Assert.Equal(secondOwner.InstanceId, failure.InstanceId); Assert.Equal(secondOwner.PackageIdentity, failure.PackageIdentity);
        Assert.Equal(secondOwner.RuntimeHash, failure.ArtifactHash); Assert.Equal(123, failure.TimeUs);
        Assert.Same(failure, Assert.Throws<EffectFailureException>(() => second.Complete()));
        Assert.Single(first.Complete());
        var fresh = new ParticleQueryBudget(byteLimit: 700);
        var recovered = new VisualOutput(secondOwner.InstanceId, Capabilities, Declarations, resources, budget: null,
            reserveBytes: bytes => fresh.ReserveBytes(secondOwner, bytes, 124));
        recovered.DrawPaths(new("paths"), [path], OverlayLayer.AboveText);
        Assert.Single(recovered.Complete());
    }

    private sealed record Data : PreparedEffect;
    private static string Revision(char marker) => "sha256:" + new string(marker, 64);
    private static ImmutableDictionary<string, ImmutableArray<string>> Sprites()
        => ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("star-atlas", ["small", "tier:large"]);
    private static ResourceCatalog Resources(char revision = 'a', string packageId = "svg")
        => new(packageId, Revision(revision), [], [], ["image.png"], ["star-vector"], Sprites());
    private static VisualOutput Output(ResourceCatalog resources, ImmutableArray<string> capabilities = default)
        => new(Owner, capabilities.IsDefault ? Capabilities : capabilities, Declarations, resources);
    private static ImmutableArray<CanvasOutputDeclaration> Canvas()
        => [new(new("paths"), ["visuals.paths", "resources.svg.vector"]),
            new(new("atlas"), ["visuals.sprites", "resources.svg.atlas"]), new(new("images"), ["visuals.sprites"])];
    private static void Validate(Data prepared, ImmutableArray<string> capabilities, ImmutableArray<CanvasOutputDeclaration> outputs)
    {
        ContentCatalog content = new([], [], [], []); SceneCatalog scene = new(content, [], [], [], []);
        DeclareContext source = new(content, scene, ParameterValues.Empty, AudioFeatures.NotAnalyzed, Resources(),
            42, TimelineIndex.Empty, new(1920, 1080), Owner);
        PreparedEffectDeclaration.Validate(prepared, source, capabilities, [], outputs);
    }
}
