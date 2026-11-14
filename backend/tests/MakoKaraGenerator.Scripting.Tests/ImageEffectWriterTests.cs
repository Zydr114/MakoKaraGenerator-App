using MakoKaraGenerator.Scripting.V2;
using Xunit;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class ImageEffectWriterTests
{
    private static readonly TokenFamilyTarget Token = new(new("token"), new("binding"));
    private static readonly LineTarget Line = new(new("line"), new("binding"));
    private static readonly TextMarkTarget Mark = new(new("mark"), new("cue"), new("binding"));
    private static ResourceCatalog Catalog(char revision = 'a') => new("package", "sha256:" + new string(revision, 64), [], ["first", "last"], ["asset"]);
    private static TextOutput Output(ResourceCatalog catalog, bool independent = true, bool includeLine = true, params string[] capabilities)
    {
        var frame = V2PackageTestHelper.CreateTextFrame(independent: independent, resources: catalog, includeLine: includeLine);
        return new(in frame, [.. capabilities.Length == 0 ? [CapabilityNames.ImageGraph] : capabilities]);
    }
    private static GraphBindingValue Binding(GraphHandle graph, ParameterValues? parameters = null)
        => new(graph, parameters ?? ParameterValues.Empty, GraphUniformValues.Empty, GraphTransformValues.Empty);
    [Fact]
    public void 同实例不同目标各自记录图绑定及参数并在完成后锁定阶段()
    {
        var catalog = Catalog(); var output = Output(catalog);
        var first = new ParameterValues([new("old", new ParameterValue.Number(10))]);
        var last = new ParameterValues([new("strength", new ParameterValue.Number(2))]);
        output.BindGraph(new TextTarget.Family(Token), Binding(catalog.GetGraph("first"), first));
        output.BindGraph(new TextTarget.Line(Line), Binding(catalog.GetGraph("first"), first));
        output.BindGraph(new TextTarget.Mark(Mark), Binding(catalog.GetGraph("first"), first));
        output.BindGraph(new TextTarget.Family(Token), Binding(catalog.GetGraph("last"), last));
        var writes = output.Complete().Writes.Cast<TextWrite.GraphBinding>().ToArray(); Assert.Equal(4, writes.Length);
        var lastToken = writes[^1];
        Assert.Equal("last", lastToken.Value.Graph.Identity.ExportId); Assert.Same(last, lastToken.Value.Parameters);
        Assert.Throws<KeyNotFoundException>(() => lastToken.Value.Parameters.GetNumber("old"));
        Assert.Equal(2, writes.Count(w => w.Target is TextTarget.Line or TextTarget.Mark && w.Value.Graph.Identity.ExportId == "first"));
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => output.BindGraph(new TextTarget.Family(Token), Binding(catalog.GetGraph("last"), last))).Code);
    }
    [Fact]
    public void 能力真实装载共享字簇和固定资源修订严格验证()
    {
        var catalog = Catalog(); var graph = catalog.GetGraph("first");
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Output(catalog, capabilities: [CapabilityNames.StyleFill])
            .BindGraph(new TextTarget.Family(Token), Binding(graph))).Code);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => Output(catalog).BindGraph(new TextTarget.Family(Token with { BindingId = new("old") }), Binding(graph))).Code);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => Output(catalog).BindGraph(new TextTarget.Mark(Mark with { CueId = new("old") }), Binding(graph))).Code);
        Assert.Equal("InvalidGraphResource", Assert.Throws<EffectOutputException>(() => Output(catalog).BindGraph(new TextTarget.Line(Line), Binding(Catalog('b').GetGraph("first")))).Code);
        Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<EffectOutputException>(() => Output(catalog, independent: false).BindGraph(new TextTarget.Family(Token), Binding(graph))).Code);
        var badAsset = new ParameterValues([new("asset", new ParameterValue.Asset(Catalog('b').GetImage("asset")))]);
        Assert.Equal("InvalidParameterResource", Assert.Throws<EffectOutputException>(() => Output(catalog).BindGraph(new TextTarget.Line(Line), Binding(graph, badAsset))).Code);
        Assert.Empty(Output(catalog).Complete().Writes);
    }
    [Fact]
    public void 无行几何不能写行图且invalidate失效()
    {
        var catalog = Catalog(); var output = Output(catalog, includeLine: false);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => output.BindGraph(new TextTarget.Line(Line), Binding(catalog.GetGraph("first")))).Code);
        output.Invalidate();
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => output.BindGraph(new TextTarget.Family(Token), Binding(catalog.GetGraph("first")))).Code);
    }
}
