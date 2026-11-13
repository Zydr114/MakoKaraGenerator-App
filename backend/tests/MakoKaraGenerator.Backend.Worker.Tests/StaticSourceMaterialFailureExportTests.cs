using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed partial class StaticAnalysisExportTaskTests
{
    [Fact]
    public async Task 源材质表面预算失败保真实来源数值与主因且不伪造脚本发生()
    {
        using TemporaryDirectory directory = new();
        var snapshot = PackageSnapshot(); var package = snapshot.Effects.Packages[0];
        snapshot = ProjectSnapshot.FromProject(StaticExportTask.ToProject(snapshot) with { LyricStyle = snapshot.LyricStyle with
            { UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink") } } });
        var reference = Assert.Single(SourceMaterialCatalog.Collect(snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation, snapshot.PresetApplications));
        var owner = new PreparedMaterialOwner.Source(reference, PreparedMaterialSlot.UnsungFill);
        ImageSurfaceBudgetException budget = ActualBudgetFailure();
        var original = new SourceMaterialFailureException(owner, "RenderMaterials", 0, budget.Code, budget);
        byte[] source = PackageProjectSnapshotCodec.Encode(snapshot); Guid id = Guid.NewGuid();
        string candidate = directory.FilePath("failed.mp4");
        byte[] payload = WorkerExportContract.EncodeInput(id, Metadata(candidate, PackageProjectSnapshotCodec.Profile), source);
        WorkerMessage request = new(WorkerExportContract.RequestKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload, taskId: id.ToString("N"), sourceSha256: WorkerProtocol.ComputeSha256(source),
            exportFormat: "opaqueMp4", exportIncludeAudio: false);
        var factory = new FailingSourceFactory(original);
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => StaticExportTask.ExecuteAsync(request, true,
            _ => Task.CompletedTask, factory, CancellationToken.None));
        Assert.Equal("ImageSurfaceBudgetExceeded", failure.Code); Assert.Same(original, failure.InnerException);
        Assert.Null(failure.DiagnosticSnapshot); Assert.True(factory.Disposed); Assert.False(File.Exists(candidate));
        RenderSourceMaterialFailure actual = Assert.IsType<RenderSourceMaterialFailure>(failure.SourceMaterialFailure);
        Assert.Equal(owner, actual.Owner); Assert.Equal("RenderMaterials", actual.Stage); Assert.Equal(0, actual.TimeUs);
        Assert.Equal(new RenderImageSurfaceBudget(1, 1, 1, 256, 256, 512), actual.Budget);
        BackendSourceMaterialFailureDto encoded = BackendSourceMaterialFailures.Encode(actual);
        WorkerMessage message = new("error", request.RequestId, request.InputSha256, error: "固定静态导出未能完成。",
            errorCode: failure.Code, taskId: request.TaskId, sourceSha256: request.SourceSha256,
            exportFormat: request.ExportFormat, exportIncludeAudio: request.ExportIncludeAudio, exportSourceMaterialFailure: encoded);
        using MemoryStream wire = new(); await WorkerProtocol.WriteAsync(wire, message); wire.Position = 0;
        WorkerMessage restored = (await WorkerProtocol.ReadAsync(wire))!;
        Assert.Null(restored.ExportDiagnostics); Assert.Equal(actual, BackendSourceMaterialFailures.Decode(restored.ExportSourceMaterialFailure!, snapshot));
    }

    private static ImageSurfaceBudgetException ActualBudgetFailure()
    {
        using var bitmap = new SKBitmap(8, 8); using var first = SKImage.FromBitmap(bitmap); using var second = SKImage.FromBitmap(bitmap);
        var descriptor = new GraphSurfaceDescriptor(new(0, 0, 8, 8), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1);
        using var a = SkiaGraphSurface.Borrow(first, descriptor, ShaderSurfaceKind.Color);
        using var b = SkiaGraphSurface.Borrow(second, descriptor, ShaderSurfaceKind.Color);
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 1, MaximumBytes = 512 });
        return Assert.Throws<ImageSurfaceBudgetException>(() => budget.HoldFixedImages([a, b]));
    }

    private sealed class FailingSourceFactory(Exception failure) : IRenderFrameSessionFactory
    {
        internal bool Disposed;
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IRenderFrameSession>(new Session(this, failure));
        private sealed class Session(FailingSourceFactory factory, Exception failure) : IRenderFrameSession
        {
            public ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default) => throw failure;
            public ValueTask DisposeAsync() { factory.Disposed = true; return ValueTask.CompletedTask; }
        }
    }
}
