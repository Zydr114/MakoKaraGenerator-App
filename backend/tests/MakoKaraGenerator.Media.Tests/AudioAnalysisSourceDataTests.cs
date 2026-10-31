using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Media.Tests;

public sealed class AudioAnalysisSourceDataTests
{
    [Fact]
    public async Task 真正短尾分析映射与文件闭包保留算法修订及每个hop值()
    {
        using TemporaryDirectory temp = new(); string audio = temp.FilePath("source.wav");
        WavWriter.Write(audio, Enumerable.Range(0, 48624).Select(i => (float)(.5 * Math.Sin(2 * Math.PI * 80 * i / 48000d))).ToArray(), 48000);
        var result = await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(audio); var data = result.ToSourceData()!;
        Assert.Equal(result.Features.Revision, data.Data!.Revision); Assert.Equal(result.SourceHash, data.Data.SourceHash); Assert.Equal(result.PcmHash, data.Data.PcmHash); Assert.Equal(result.SampleCount, data.Data.SampleCount);
        var points = result.Features.GetRange(new(0, result.Features.DurationUs!.Value)).Points;
        Assert.Equal(points.Select(p => p.TimeUs), data.Data.Points.Select(p => p.TimeUs)); Assert.Equal(points.Select(p => p.Values.BassLevel), data.Data.Points.Select(p => p.BassLevel));
        Assert.Equal(points.Select(p => p.Values.Pulse), data.Data.Points.Select(p => p.Pulse)); Assert.Equal(points.Select(p => p.Values.BeatPhase), data.Data.Points.Select(p => p.BeatPhase));
        string saved = temp.FilePath("project.mkgproj"); ProjectStore.Save(Project.Create("真实分析", audioPath: audio) with { AudioAnalysis = data }, saved);
        var restored = ProjectStore.Load(saved).Project!; Assert.Equal(data, restored.AudioAnalysis); Assert.Equal("source.wav", restored.AudioMedia!.OriginalName);
        var again = await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(restored.AudioPath); Assert.Equal(data, again.ToSourceData());
    }
    [Fact]
    public async Task 真正无源与解码失败不会生成可用修订或静音()
    {
        Assert.Null((await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(null)).ToSourceData());
        using TemporaryDirectory temp = new(); var result = await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(temp.FilePath("missing.wav"));
        Assert.Equal(AudioAvailability.AnalysisFailed, result.Features.Availability); var data = result.ToSourceData()!;
        Assert.Null(data.Data); Assert.Equal(result.DiagnosticCode, data.DiagnosticCode); Assert.Equal(result.DiagnosticMessage, data.DiagnosticMessage); Assert.False(data.MissingStream);
    }
}
