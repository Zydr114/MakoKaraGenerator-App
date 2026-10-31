using MakoKaraGenerator.Scripting;
using Xunit;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class ImageEffectWriterTests
{
    private static readonly TokenFamilyTarget Token = new(new("token"), new("binding"));
    private static readonly LineTarget Line = new(new("line"), new("binding"));
    private static readonly TextMarkTarget Mark = new(new("mark"), new("cue"), new("binding"));
    private static ResourceCatalog Catalog(char revision = 'a') => new("package", "sha256:" + new string(revision, 64), [], ["first", "last"], ["asset"]);
    private static TokenGeometrySample Geometry(bool independent = true) => new() { BranchId = new("branch"), LineId = Line.LineId,
        WithRuby = Token, BaseOnly = new(Token.TokenId, Token.BindingId), BaseGeometry = new() { AdvanceBounds = new(0, 0, 20, 20),
            Baseline = new(0, 15), BaseWorldMatrix = Affine2.Identity, CanTransformIndependently = independent },
        FamilyGeometry = new() { AdvanceBounds = new(0, 0, 20, 20), Baseline = new(0, 15), BaseWorldMatrix = Affine2.Identity } };
    private static FrameWriter Writer(ResourceCatalog catalog, EffectCapabilities capabilities = EffectCapabilities.ImageEffects, bool independent = true)
        => new([Geometry(independent)], capabilities, resources: catalog,
            markGeometry: [new(Mark, Geometry().BaseGeometry)],
            lineGeometry: [new() { Target = Line, BranchId = new("branch"), BaseGeometry = Geometry().BaseGeometry, ProgressPath = new(0, 20, 15, 10) }]);
    [Fact]
    public void 同实例同目标仅最后图和完整参数不同目标各自保留()
    {
        var catalog = Catalog(); var writer = Writer(catalog);
        var first = new ParameterValues([new("old", new ParameterValue.Number(10))]);
        var last = new ParameterValues([new("strength", new ParameterValue.Number(2))]);
        writer.ImageEffects.Bind(Token, catalog.GetGraph("first"), first);
        writer.ImageEffects.Bind(Line, catalog.GetGraph("first"), first);
        writer.ImageEffects.Bind(Mark, catalog.GetGraph("first"), first);
        writer.ImageEffects.Bind(Token, catalog.GetGraph("last"), last);
        var output = writer.Complete(); Assert.Equal(3, output.ImageEffects.Length);
        var binding = Assert.Single(output.ImageEffects, b => b.Target == new ImageEffectTarget.Token(Token));
        Assert.Equal("last", binding.Graph.Identity.ExportId); Assert.Same(last, binding.Parameters);
        Assert.Throws<KeyNotFoundException>(() => binding.Parameters.GetNumber("old"));
        Assert.Equal(2, output.ImageEffects.Count(b => b.Graph.Identity.ExportId == "first"));
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => writer.ImageEffects.Bind(Token, catalog.GetGraph("last"), last)).Code);
    }
    [Fact]
    public void 能力真实装载共享字簇和固定资源修订严格验证()
    {
        var catalog = Catalog(); var graph = catalog.GetGraph("first");
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Writer(catalog, EffectCapabilities.Style).ImageEffects.Bind(Token, graph, ParameterValues.Empty)).Code);
        var writer = Writer(catalog);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => writer.ImageEffects.Bind(Token with { BindingId = new("old") }, graph, ParameterValues.Empty)).Code);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => writer.ImageEffects.Bind(Mark with { CueId = new("old") }, graph, ParameterValues.Empty)).Code);
        Assert.Equal("InvalidGraphResource", Assert.Throws<EffectOutputException>(() => writer.ImageEffects.Bind(Line, Catalog('b').GetGraph("first"), ParameterValues.Empty)).Code);
        Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<EffectOutputException>(() => Writer(catalog, independent: false).ImageEffects.Bind(Token, graph, ParameterValues.Empty)).Code);
        var badAsset = new ParameterValues([new("asset", new ParameterValue.Asset(Catalog('b').GetImage("asset")))]);
        Assert.Equal("InvalidParameterResource", Assert.Throws<EffectOutputException>(() => writer.ImageEffects.Bind(Line, graph, badAsset)).Code);
        Assert.Empty(writer.Complete().ImageEffects);
    }
    [Fact]
    public void 元素范围不因可读取整行而获得行图写权且invalidate失效()
    {
        var catalog = Catalog(); var writer = new FrameWriter([Geometry()], EffectCapabilities.ImageEffects, resources: catalog);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => writer.ImageEffects.Bind(Line, catalog.GetGraph("first"), ParameterValues.Empty)).Code);
        writer.Invalidate();
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => writer.ImageEffects.Bind(Token, catalog.GetGraph("first"), ParameterValues.Empty)).Code);
    }
}
