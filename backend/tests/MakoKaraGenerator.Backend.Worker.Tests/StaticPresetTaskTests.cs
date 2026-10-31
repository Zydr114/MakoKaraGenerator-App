using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed partial class StaticAnalysisExportTaskTests
{
    [Theory]
    [InlineData(null)] [InlineData("available")] [InlineData("failed")] [InlineData("noStream")]
    public async Task Worker正式验证双模式消费原固定应用和可选分析不查库或读取声明音频(string? analysis)
    {
        var source = PresetSource(analysis);
        byte[] bytes = StaticProjectSnapshotCodec.Encode(source, StaticProjectSnapshotCodec.PresetsProfile);
        Guid id = Guid.NewGuid(); byte[] payload = WorkerValidationContract.EncodeInput(id, 3, bytes);
        WorkerMessage request = new(WorkerValidationContract.RequestKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload, sourceSha256: WorkerProtocol.ComputeSha256(bytes),
            taskId: id.ToString("N"), validationFramesPerOutput: 3);
        var factory = new CapturingFactory();
        var result = await StaticValidationTask.ExecuteAsync(request, _ => Task.CompletedTask, factory, CancellationToken.None);
        var report = WorkerValidationContract.DecodeReport(result.Payload, 3);
        Assert.True(report.Succeeded); Assert.Equal(3, report.OpaqueFrames); Assert.Equal(3, report.TransparentFrames);
        Assert.Equal(2, factory.Created); Assert.Equal(2, factory.Disposed); Assert.Equal(6, factory.Frames);
        Assert.Equal(source.PresetApplications, factory.Snapshot!.PresetApplications);
        Assert.Equal(source.AudioAnalysis, factory.Snapshot.AudioAnalysis); Assert.Equal(source.AudioPath, factory.Snapshot.AudioPath);
        Assert.Equal(request.SourceSha256, result.SourceSha256);
    }

    [Theory]
    [InlineData(null)] [InlineData("available")] [InlineData("failed")] [InlineData("noStream")]
    public async Task Worker真实Exporter重编码同一应用记录及源SHA并保可选分析(string? analysis)
    {
        using TemporaryDirectory directory = new();
        var source = PresetSource(analysis); var factory = new CapturingFactory();
        var request = Request(source, directory.FilePath("candidate.mp4"), StaticProjectSnapshotCodec.PresetsProfile);
        var result = await StaticExportTask.ExecuteAsync(request, true, _ => Task.CompletedTask, factory, CancellationToken.None);
        var report = WorkerExportContract.DecodeReport(result.Payload, new("opaqueMp4", false, 8, 4, 30, 1, 30, 1_000_000));
        Assert.True(report.StrictlyVerified); Assert.Equal(30, factory.Frames);
        Assert.Equal(1, factory.Created); Assert.Equal(1, factory.Disposed);
        Assert.Equal(source.PresetApplications, factory.Snapshot!.PresetApplications);
        Assert.Equal(source.AudioAnalysis, factory.Snapshot.AudioAnalysis);
        Assert.Equal(request.SourceSha256, result.SourceSha256);
    }

    private static ProjectSnapshot PresetSource(string? analysis)
    {
        var source = Snapshot("/never-read/declared-source.wav", Wave(), analysis ?? "failed");
        if (analysis is null) source = source with { AudioAnalysis = null };
        var template = StyleTemplate.Create("fixed", "固定模板", StyleTemplateKind.Line,
            source.LyricStyle with { Opacity = .4f }, [LyricStyleProperty.Opacity]);
        return source with { PresetApplications = new([new PresetApplication("stable-application", new(ProjectEffectBindingKind.Project), template)]) };
    }
}
