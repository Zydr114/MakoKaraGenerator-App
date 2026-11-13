using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendExportJobTests
{
    [Fact]
    public async Task 源材质失败报告公开真实owner阶段时刻预算且无脚本发生或内部异常文本()
    {
        await using Fixture f = await Fixture.Create(capabilities: new(true, true, true,
            SourceProfiles: [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.PackageLyricsScene]));
        string id = await f.Project("a"); Assert.True(f.Registry.TryGet("a", id, out var session));
        var package = MixedEffectPackageFixture.Contents();
        var project = session.Workspace.Project;
        session.Workspace.Replace(project with { Effects = new([package], []), LyricStyle = project.LyricStyle with
            { UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink") } } });
        ProjectSnapshot fixedSource = ProjectSnapshot.FromProject(session.Workspace.Project);
        var reference = Assert.Single(SourceMaterialCatalog.Collect(fixedSource.LyricStyle, fixedSource.Lyrics, fixedSource.LyricPresentation, fixedSource.PresetApplications));
        var source = new RenderSourceMaterialFailure(new(reference, PreparedMaterialSlot.UnsungFill), "RenderMaterials", 930_000,
            "ImageSurfaceBudgetExceeded", new(62, 4, 64, 1_000_000, 262_144, 536_870_912));
        f.Exporter.Failure = new(source.Code, "/private/source/path must stay internal", innerException: new IOException("private implementation details"))
            { SourceMaterialFailure = source };
        JobResponse job = await f.Start(id); (await f.Exporter.Next()).Release.TrySetResult();
        JobResponse terminal = await f.Terminal("a", job.JobId);
        Assert.Equal("failed", terminal.State); Assert.Equal(source.Code, terminal.FailureCode); Assert.NotNull(terminal.FailureReportRef); Assert.Null(terminal.ExportResult);
        byte[] bytes = f.Registry.ReadExportResult("a", job.JobId).Response!;
        string text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("private", text); Assert.DoesNotContain("implementation details", text);
        ExportFailureReportDto report = JsonSerializer.Deserialize<ExportFailureReportDto>(bytes, Json)!;
        BackendDiagnosticDto diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal("sourceMaterialFailure", diagnostic.OriginKind); Assert.Null(diagnostic.Occurrence); Assert.Null(diagnostic.Material);
        Assert.Equal(source, BackendSourceMaterialFailures.Decode(diagnostic.SourceMaterialFailure!, fixedSource));
        Assert.Equal(diagnostic, Assert.Single(f.Registry.ReadExportDiagnostics("a", job.JobId, null, 1).Response!.Diagnostics));
        Assert.Equal(BackendErrorCode.ExportArtifactNotFound, f.Registry.AcquireExportRead("a", job.JobId).Error!.Code);
        await f.Restart(); Assert.Equal(bytes, f.Registry.ReadExportResult("new-bootstrap", job.JobId, true).Response);
        Assert.Equal(BackendErrorCode.ExportArtifactNotFound, f.Registry.AcquireExportRead("new-bootstrap", job.JobId, true).Error!.Code);
    }

    [Theory]
    [InlineData("path")] [InlineData("time")]
    public void 源材质失败诊断拒绝伪造固定工程身份(string damage)
    {
        var package = MixedEffectPackageFixture.Contents();
        var project = Project.Create("source failure", Composition.Create(8, 4, Core.Time.FrameRate.Fps30, outputDurationUs: 1_000_000), Core.Lyrics.LyricsDocument.Create([])) with
        { Effects = new([package], []), LyricStyle = LyricStyle.Default with { UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink") } } };
        var snapshot = ProjectSnapshot.FromProject(project);
        var reference = Assert.Single(SourceMaterialCatalog.Collect(project));
        var dto = BackendSourceMaterialFailures.Encode(new(new(reference, PreparedMaterialSlot.UnsungFill), "RenderMaterials", 0, "ImageSurfaceBudgetExceeded"));
        dto = damage == "path" ? dto with { Owner = dto.Owner with { Path = "other-source.fill" } } : dto with { TimeUs = "1000000" };
        Assert.Equal("WorkerExportProtocolInvalid", Assert.Throws<WorkerProtocolException>(() => BackendSourceMaterialFailures.Decode(dto, snapshot)).Code);
    }
}
