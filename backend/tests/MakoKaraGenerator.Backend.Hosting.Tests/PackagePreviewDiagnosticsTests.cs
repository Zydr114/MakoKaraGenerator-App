using System.Collections.Immutable;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class PackagePreviewDiagnosticsTests
{
    private static readonly string Revision = "sha256:" + new string('a', 64);
    private static readonly string Artifact = new('b', 64);
    private static readonly ResourceCatalog Catalog = new("package", Revision, ["material"], ["graph"], []);
    private static readonly PreviewLimitsDto Limits = new(10, 10, 1, 2, 4096, 30, 128);
    private static PreviewSessionResponse Session() => new("preview", new("project", "project", "1"), "1", new(1, 1), PreviewAlphaMode.Opaque, "ready", Limits);
    private static RenderSourceDiagnostic Source(string message = "source warning", bool error = false) =>
        new("package", Revision, null, null, "Compile", message, error, null, 0, 0);
    private static ImmutableArray<RenderMaterialDiagnostic> Materials() =>
    [
        new(new PreparedMaterialOwner.Script(new("instance"), Artifact) { PackageIdentity = new("package", Revision, "controller") }, Catalog.GetMaterial("material"), "background", "TransparentBackgroundDependency"),
        new(new PreparedMaterialOwner.Source(new("lyrics.lines[0].style.unsung.fill", "fill", new MaterialSpec.PackageMaterial("package", Revision, "material", EffectParameterOverrides.Parse("{\"amount\":0.1234567890123456789}"))), PreparedMaterialSlot.UnsungFill), Catalog.GetMaterial("material"), "background", "TransparentBackgroundDependency"),
    ];
    [Fact]
    public void 完整来源投影保留零位置资源身份及两类材质owner()
    {
        var result = BackendPreviewDiagnosticsMapper.Project(null, [Source()], [new(new("instance"), Artifact, Catalog.GetGraph("graph"), "background", "TransparentBackgroundDependency")], Materials(), Limits, 32);
        Assert.Equal((IEnumerable<string>)["compileSource", "imageEffect", "material", "material"], result.Select(d => d.OriginKind));
        Assert.All(result, d => Assert.Null(d.Occurrence));
        Assert.Equal(new BackendSourceDiagnosticDto("package", Revision, null, null, "Compile", "source warning", false, null, 0, 0), result[0].Source);
        Assert.Equal(new BackendDiagnosticResourceDto("package", Revision, "graph"), result[1].ImageEffect!.Graph);
        Assert.Equal(new BackendDiagnosticResourceDto("package", Revision, "controller"), result[2].Material!.Owner.Script!.PackageIdentity);
        var owner = result[3].Material!.Owner.Source!;
        Assert.Equal("lyrics.lines[0].style.unsung.fill", owner.Path);
        Assert.Equal("fill", owner.Region); Assert.Equal(0, owner.Slot);
        Assert.Equal("{\"amount\":0.1234567890123456789}", owner.ParametersJson);
        Assert.Null(PreviewTransportValidation.ValidateDiagnosticsPage(Session(), new("preview", "1", "1", result, null, false)));
    }
    [Fact]
    public void 诊断union拒绝混合owner和伪造运行发生()
    {
        var d = BackendPreviewDiagnosticsMapper.Project(null, [Source()], [], [], Limits, 32)[0];
        var bad = d with { Occurrence = new(Guid.NewGuid(), "1", "fake", Artifact, "fake", "0", null, null) };
        Assert.NotNull(PreviewTransportValidation.ValidateDiagnosticsPage(Session(), new("preview", "1", "1", [bad], null, false)));
        bad = d with { OriginKind = "runtime" };
        Assert.NotNull(PreviewTransportValidation.ValidateDiagnosticsPage(Session(), new("preview", "1", "1", [bad], null, false)));
        var m = BackendPreviewDiagnosticsMapper.Project(null, [], [], Materials(), Limits, 32)[0];
        bad = m with { Material = m.Material! with { Owner = m.Material!.Owner with { Source = new("path", "fill", 0, new("package", Revision, "material"), "{}") } } };
        Assert.NotNull(PreviewTransportValidation.ValidateDiagnosticsPage(Session(), new("preview", "1", "1", [bad], null, false)));
    }
    [Fact]
    public void 完整公共字节预算使用真实camelCase省略null表达()
    {
        // Source message is duplicated in outer and payload; find the last legal wire length.
        int size = BackendPreviewDiagnosticsLimits.MaximumJsonBytes / 2 - 300;
        BackendDiagnosticDto[] accepted;
        while (true)
        {
            try { accepted = BackendPreviewDiagnosticsMapper.Project(null, [Source(new string('x', size))], [], [], Limits, 32); size++; }
            catch (RenderSessionFailureException) { size--; break; }
        }
        accepted = BackendPreviewDiagnosticsMapper.Project(null, [Source(new string('x', size))], [], [], Limits, 32);
        int bytes = JsonSerializer.SerializeToUtf8Bytes(accepted, BackendPreviewDiagnosticsMapper.PublicJsonOptions).Length;
        Assert.InRange(bytes, BackendPreviewDiagnosticsLimits.MaximumJsonBytes - 1, BackendPreviewDiagnosticsLimits.MaximumJsonBytes);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(accepted).Length > BackendPreviewDiagnosticsLimits.MaximumJsonBytes);
        Assert.Equal("PreviewDiagnosticsBudgetExceeded", Assert.Throws<RenderSessionFailureException>(() => BackendPreviewDiagnosticsMapper.Project(null, [Source(new string('x', size + 1))], [], [], Limits, 32)).Code);
    }
    [Fact]
    public void 单页发布满三十二页后明确拒绝且保留所有旧页()
    {
        PreviewDiagnosticsStore store = new(); var session = Session(); Assert.Null(store.Activate("client", session));
        for (int index = 0; index < 32; index++)
            Assert.Null(store.Publish("client", session, new("preview", "1", "1", [new("Code", "warning", "message", null)], null, false, "cursor-" + index)));
        Assert.Equal(BackendErrorCode.DiagnosticLimitExceeded, store.Publish("client", session, new("preview", "1", "1", [], null, false, "overflow"))!.Code);
        for (int index = 0; index < 32; index++)
            Assert.True(store.TryRead("client", "preview", "cursor-" + index, null, out _, out _));
        Assert.False(store.TryRead("client", "preview", "overflow", null, out _, out _));
    }

    [Theory]
    [InlineData(257, 32, 128)]
    [InlineData(3, 1, 2)]
    public void 条数和配置页数超限不截断(int count, int pages, int pageSize)
    {
        Assert.Equal("PreviewDiagnosticsBudgetExceeded", Assert.Throws<RenderSessionFailureException>(() =>
            BackendPreviewDiagnosticsMapper.Project(null, [.. Enumerable.Repeat(Source(), count)], [], [], Limits with { MaxDiagnosticsPageSize = pageSize }, pages)).Code);
    }
}
