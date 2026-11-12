using System.Collections.Immutable;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class BackendFallbackDiagnosticCollectorTests
{
    private static readonly string Revision = "sha256:" + new string('a', 64);
    private static readonly ResourceCatalog Catalog = new("package", Revision, ["ink"],
        [.. Enumerable.Range(0, 200).Select(index => "graph-" + index)], []);

    private static RenderImageEffectDiagnostic Image(int graph, string code = BackendFallbackDiagnostics.TransparentBackgroundDependency,
        string input = "background") => new(new EffectInstanceId("instance"), Revision, Catalog.GetGraph("graph-" + graph), input, code);

    [Fact]
    public void 按公共投影去重保留首次顺序并只数含兜底的帧()
    {
        BackendFallbackDiagnosticCollector collector = new();
        RenderMaterialDiagnostic material = new(new EffectInstanceId("instance"), Revision, Catalog.GetMaterial("ink"), "background",
            BackendFallbackDiagnostics.BackgroundMediaUnavailable);
        collector.Add([Image(0), Image(1)], []);
        collector.Add([Image(0)], []);
        collector.Add([], []);
        collector.Add(default, default);
        collector.Add([Image(1)], [material]);

        Assert.Equal(3, collector.FrameCount);
        Assert.False(collector.Truncated);
        BackendDiagnosticDto[] items = collector.ToArray();
        Assert.Equal([BackendFallbackDiagnostics.Map(Image(0)), BackendFallbackDiagnostics.Map(Image(1)), BackendFallbackDiagnostics.Map(material)], items);
        Assert.Equal("透明输出没有背景，图效果已旁路为原样。", items[0].Message);
        Assert.Equal("背景媒体不可用（未设置、文件丢失或该时刻无帧），材质已退回包内兜底外观。", items[2].Message);
        Assert.Equal(("script", "instance"), (items[2].Material!.Owner.Kind, items[2].Material!.Owner.Script!.InstanceId));
        Assert.True(BackendFallbackDiagnosticsContract.IsValid(items));
    }

    [Fact]
    public void 超过条数上限只记截断且结果仍合法()
    {
        BackendFallbackDiagnosticCollector collector = new();
        foreach (int index in Enumerable.Range(0, BackendFallbackDiagnosticsContract.MaximumItems + 2))
            collector.Add([Image(index)], []);
        collector.Add([Image(BackendFallbackDiagnosticsContract.MaximumItems + 1)], []);

        Assert.True(collector.Truncated);
        Assert.Equal(BackendFallbackDiagnosticsContract.MaximumItems + 3, collector.FrameCount);
        BackendDiagnosticDto[] items = collector.ToArray();
        Assert.Equal(BackendFallbackDiagnosticsContract.MaximumItems, items.Length);
        Assert.Equal(BackendFallbackDiagnostics.Map(Image(0)), items[0]);
        Assert.True(BackendFallbackDiagnosticsContract.IsValid(items));
        Assert.False(BackendFallbackDiagnosticsContract.IsValid([.. items, BackendFallbackDiagnostics.Map(Image(150))]));
    }

    [Fact]
    public void 超过字节上限只记截断且结果仍合法()
    {
        BackendFallbackDiagnosticCollector collector = new();
        string input = new('x', 2048);
        foreach (int index in Enumerable.Range(0, 40))
            collector.Add([Image(index, input: input)], []);

        Assert.True(collector.Truncated);
        BackendDiagnosticDto[] items = collector.ToArray();
        Assert.InRange(items.Length, 1, 39);
        Assert.True(BackendFallbackDiagnosticsContract.IsValid(items));
        Assert.False(BackendFallbackDiagnosticsContract.IsValid([.. items, BackendFallbackDiagnostics.Map(Image(39, input: input))]));
    }

    [Fact]
    public void 合同拒绝非兜底来源重复与错误级别()
    {
        BackendDiagnosticDto item = BackendFallbackDiagnostics.Map(Image(0));
        Assert.True(BackendFallbackDiagnosticsContract.IsValid([]));
        Assert.False(BackendFallbackDiagnosticsContract.IsValid(null));
        Assert.False(BackendFallbackDiagnosticsContract.IsValid([item, item]));
        Assert.False(BackendFallbackDiagnosticsContract.IsValid([item with { Severity = "error" }]));
        Assert.False(BackendFallbackDiagnosticsContract.IsValid([item with { OriginKind = "runtime" }]));
        Assert.False(BackendFallbackDiagnosticsContract.IsValid([item with { Message = " " }]));
        Assert.False(BackendFallbackDiagnosticsContract.IsValid([item with { Code = "Other" }]));
        Assert.False(BackendFallbackDiagnosticsContract.IsValid([null!]));
    }
}
