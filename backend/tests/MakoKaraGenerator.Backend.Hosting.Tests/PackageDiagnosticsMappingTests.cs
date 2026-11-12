using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class PackageDiagnosticsMappingTests
{
    private const string Reason = "TransparentBackgroundDependency";
    private static (ProjectSnapshot Snapshot, ResourceCatalog Catalog) Fixture(bool source = false)
    {
        var p = MixedEffectPackageFixture.Contents(background: true);
        var material = new MaterialSpec.PackageMaterial(p.PackageId, p.Revision, "ink", EffectParameterOverrides.Parse("{\"amount\":0.12345678901234567}"));
        var project = Project.Create("diagnostics", Composition.Create(2, 2, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([])) with
        {
            Effects = new([p], [new() { Id = "instance", Name = "instance", PackageId = p.PackageId, Revision = p.Revision, ExportId = "controller",
                Binding = new(ProjectEffectBindingKind.Project), Subscription = new("project") }]),
            LyricStyle = source ? LyricStyle.Default with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material }, Stroke = null } : LyricStyle.Default,
        };
        return (ProjectSnapshot.FromProject(project), new(p.PackageId, p.Revision, ["ink"], ["tint"], []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 固定包材质诊断必须保留真实控制器身份而不接受空来源(bool hasIdentity)
    {
        var (snapshot, catalog) = Fixture();
        using RenderFrame frame = new(new byte[16], new() { PixelWidth = 2, PixelHeight = 2 }, PixelFormat.Bgra8888Premultiplied, 0)
        {
            ImageEffectDiagnostics = [new(new("instance"), new string('a', 64), catalog.GetGraph("tint"), "background", Reason)],
            MaterialDiagnostics = [new(new PreparedMaterialOwner.Script(new("instance"), new string('b', 64))
                { PackageIdentity = hasIdentity ? new(catalog.PackageId!, catalog.Revision!, "controller") : null }, catalog.GetMaterial("ink"), "background", Reason)],
        };
        var wire = PackageRenderDiagnosticsMapper.EncodeFrame(frame);
        var mapper = new PackageRenderDiagnosticsMapper(snapshot);
        if (!hasIdentity)
        {
            Assert.Equal("WorkerPackageDiagnosticsInvalid", Assert.Throws<WorkerProtocolException>(() => mapper.DecodeFrame(wire)).Code);
            return;
        }
        var restored = mapper.DecodeFrame(wire);
        Assert.Equal(frame.ImageEffectDiagnostics.ToArray(), restored.Image.ToArray());
        Assert.Equal(frame.MaterialDiagnostics.ToArray(), restored.Material.ToArray());
        Assert.True(catalog.Contains(restored.Image[0].Graph));
        Assert.True(catalog.Contains(restored.Material[0].Material));
    }

    [Theory]
    [InlineData("BackgroundMediaUnavailable", true)]
    [InlineData("GraphRequiredInputUnavailable", false)]
    public void 缺背景媒体原因可跨进程还原而未协商原因仍拒绝(string reason, bool accepted)
    {
        var (snapshot, catalog) = Fixture();
        using RenderFrame frame = new(new byte[16], new() { PixelWidth = 2, PixelHeight = 2 }, PixelFormat.Bgra8888Premultiplied, 0)
        {
            ImageEffectDiagnostics = [new(new("instance"), new string('a', 64), catalog.GetGraph("tint"), "background", reason)],
            MaterialDiagnostics = [new(new PreparedMaterialOwner.Script(new("instance"), new string('b', 64))
                { PackageIdentity = new(catalog.PackageId!, catalog.Revision!, "controller") }, catalog.GetMaterial("ink"), "background", reason)],
        };
        var wire = PackageRenderDiagnosticsMapper.EncodeFrame(frame);
        var mapper = new PackageRenderDiagnosticsMapper(snapshot);
        if (!accepted)
        {
            Assert.Equal("WorkerPackageDiagnosticsInvalid", Assert.Throws<WorkerProtocolException>(() => mapper.DecodeFrame(wire)).Code);
            return;
        }
        var restored = mapper.DecodeFrame(wire);
        Assert.Equal(frame.ImageEffectDiagnostics.ToArray(), restored.Image.ToArray());
        Assert.Equal(frame.MaterialDiagnostics.ToArray(), restored.Material.ToArray());
    }

    [Fact]
    public void 源材质保留原完整参数路径区域及slot()
    {
        var (snapshot, catalog) = Fixture(true);
        var reference = SourceMaterialCatalog.Collect(snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation)[0];
        var owner = new PreparedMaterialOwner.Source(reference, PreparedMaterialSlot.UnsungFill);
        using RenderFrame frame = new(new byte[16], new() { PixelWidth = 2, PixelHeight = 2 }, PixelFormat.Bgra8888Premultiplied, 0)
        { MaterialDiagnostics = [new(owner, catalog.GetMaterial("ink"), "background", Reason)] };
        var wire = PackageRenderDiagnosticsMapper.EncodeFrame(frame);
        var decoded = new PackageRenderDiagnosticsMapper(snapshot).DecodeFrame(wire);
        Assert.Equal(frame.MaterialDiagnostics.ToArray(), decoded.Material.ToArray());
        Assert.Equal(reference.Material.Parameters.Json, wire.Material[0].Owner.ParametersJson);
    }

    [Fact]
    public void 直接图实例使用完整包修订而非控制器DLL产物身份()
    {
        var (snapshot, catalog) = Fixture();
        snapshot = snapshot with { Effects = new(snapshot.Effects.Packages, [snapshot.Effects.Instances[0] with { ExportId = "tint" }]) };
        var resource = new WorkerPackageResourceDto(catalog.PackageId!, catalog.Revision!, "tint");
        var image = new WorkerPackageImageDiagnosticDto("instance", catalog.Revision!, resource, "background", Reason);
        var mapper = new PackageRenderDiagnosticsMapper(snapshot);
        Assert.Equal(catalog.Revision, mapper.DecodeFrame(new(1, [], [image], [])).Image[0].ArtifactHash);
        Assert.Throws<WorkerProtocolException>(() => mapper.DecodeFrame(new(1, [], [image with { ArtifactHash = new string('a', 64) }], [])));
    }

    [Fact]
    public void 无启用实例的源材质包编译诊断保留空实例导出及零位置()
    {
        var (snapshot, catalog) = Fixture(true);
        snapshot = snapshot with { Effects = new(snapshot.Effects.Packages, []) };
        var source = new RenderSourceDiagnostic(catalog.PackageId!, catalog.Revision!, null, null, "C", "error", true, null, 0, 0);
        var decoded = new PackageRenderDiagnosticsMapper(snapshot).DecodeSource(PackageRenderDiagnosticsMapper.EncodeSource([source]));
        Assert.Equal(source, Assert.Single(decoded));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("type")]
    [InlineData("input")]
    [InlineData("code")]
    [InlineData("instance")]
    [InlineData("controller")]
    public void 拒绝非固定图及材质身份或未知输入(string damage)
    {
        var (snapshot, catalog) = Fixture();
        var resource = new WorkerPackageResourceDto(catalog.PackageId!, catalog.Revision!, "tint");
        var image = new WorkerPackageImageDiagnosticDto("instance", new string('a', 64), resource, "background", Reason);
        var material = new WorkerPackageMaterialDiagnosticDto(new("script", "instance", new string('b', 64),
            new(catalog.PackageId!, catalog.Revision!, "controller"), null, null, null, null, null), resource with { ExportId = "ink" }, "background", Reason);
        if (damage == "controller") material = material with { Owner = material.Owner with { PackageIdentity = resource } };
        else image = damage switch
        {
            "revision" => image with { Graph = resource with { Revision = "sha256:" + new string('0', 64) } },
            "type" => image with { Graph = resource with { ExportId = "ink" } },
            "input" => image with { InputId = "original" },
            "code" => image with { Code = "Invented" },
            _ => image with { InstanceId = "missing" },
        };
        var mapper = new PackageRenderDiagnosticsMapper(snapshot);
        Assert.Equal("WorkerPackageDiagnosticsInvalid", Assert.Throws<WorkerProtocolException>(() => mapper.DecodeFrame(new(1, [], [image], [material]))).Code);
    }

    [Theory]
    [InlineData("parameters")]
    [InlineData("slot")]
    [InlineData("path")]
    [InlineData("region")]
    [InlineData("handle")]
    public void 源材质拒绝参数篡改与合法但错误slot(string damage)
    {
        var (snapshot, catalog) = Fixture(true);
        var reference = SourceMaterialCatalog.Collect(snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation)[0];
        var id = new WorkerPackageResourceDto(catalog.PackageId!, catalog.Revision!, "ink");
        var owner = new WorkerPackageMaterialOwnerDto("source", null, null, null, reference.Path, reference.Region,
            (int)PreparedMaterialSlot.UnsungFill, id, reference.Material.Parameters.Json);
        owner = damage switch
        {
            "parameters" => owner with { ParametersJson = "{\"amount\":0.2}" },
            "slot" => owner with { Slot = (int)PreparedMaterialSlot.SungFill },
            "path" => owner with { Path = "missing.unsung.fill" },
            "region" => owner with { Region = "stroke" },
            _ => owner,
        };
        var diagnostic = new WorkerPackageMaterialDiagnosticDto(owner, damage == "handle" ? id with { ExportId = "tint" } : id, "background", Reason);
        Assert.Throws<WorkerProtocolException>(() => new PackageRenderDiagnosticsMapper(snapshot).DecodeFrame(new(1, [], [], [diagnostic])));
    }

    [Fact]
    public void 源编译诊断保留全部顺序重复与真实false零位置()
    {
        var (snapshot, catalog) = Fixture();
        var warning = new RenderSourceDiagnostic(catalog.PackageId!, catalog.Revision!, "instance", "controller", "C1", "warning🎵", false, null, 0, 0);
        var error = warning with { Code = "C2", IsError = true, Path = "src/PackEffect.cs", Line = 2, Column = 1 };
        var wire = PackageRenderDiagnosticsMapper.EncodeSource([warning, error, warning]);
        var restored = new PackageRenderDiagnosticsMapper(snapshot).DecodeSource(wire);
        Assert.Equal(new[] { warning, error, warning }, restored.ToArray());
        var exception = new RenderSessionFailureException("WorkerPackageCompilationFailed", "compile", sourceDiagnostics: restored);
        Assert.Null(exception.DiagnosticSnapshot);
        Assert.Equal(restored, exception.SourceDiagnostics);
        Assert.Throws<WorkerProtocolException>(() => new PackageRenderDiagnosticsMapper(snapshot).DecodeFrame(wire));
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("export")]
    [InlineData("no-error")]
    [InlineData("null-with-enabled")]
    [InlineData("unused-package")]
    public void 源编译诊断拒绝伪造引用及非编译失败(string damage)
    {
        var (snapshot, catalog) = Fixture();
        var d = new WorkerPackageSourceDiagnosticDto(catalog.PackageId!, catalog.Revision!, "instance", "controller", "C", "error", true, null, 0, 0);
        d = damage switch
        {
            "instance" => d with { InstanceId = "missing" },
            "export" => d with { ExportId = "ink" },
            "no-error" => d with { IsError = false },
            _ => d with { InstanceId = null, ExportId = null },
        };
        if (damage == "unused-package") snapshot = snapshot with { Effects = new(snapshot.Effects.Packages, []) };
        Assert.Throws<WorkerProtocolException>(() => new PackageRenderDiagnosticsMapper(snapshot).DecodeSource(new(1, [d], [], [])));
    }
}
