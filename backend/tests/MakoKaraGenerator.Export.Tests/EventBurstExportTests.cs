using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Export.Tests;

public sealed partial class ContinuousParticleExportTests
{
    [Theory]
    [InlineData(false, "BurstAnchorUnavailable", 300_000, 9)]
    [InlineData(true, "LiveParticleBudgetExceeded", 400_000, 12)]
    public async Task Burst锚点缺失或全局存活预算中途失败保护原输出(bool budget, string code, long time, int rendered)
    {
        using TemporaryDirectory temp = new(); var project = Project();
        var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await Source(snapshot, fail: budget, burst: true, unavailable: !budget);
        using PreparedPipeline pipeline = new(source, snapshot);
        string output = temp.FilePath("existing.mp4"); byte[] original = [3,1,4]; await File.WriteAllBytesAsync(output, original);
        var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        var error = await Assert.ThrowsAsync<EffectFailureException>(() => exporter.ExportAsync(project,
            new() { OutputPath = output, IncludeAudio = false, Overwrite = true }));
        Assert.Equal(code, error.Code); Assert.Equal("cursor-effect", error.InstanceId.Value); Assert.Equal(time, error.TimeUs);
        Assert.Equal(rendered, pipeline.RenderedCount); Assert.Equal(original, await File.ReadAllBytesAsync(output));
        Assert.Equal([output], Directory.GetFiles(temp.Path));
    }
}
