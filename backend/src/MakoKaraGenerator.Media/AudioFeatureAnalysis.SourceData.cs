using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Media;

/// <summary>真实离线结果到中立工程源数据的唯一桥接，不再次运行算法。</summary>
public static class AudioFeatureAnalysisSourceData
{
    /// <summary>固定完整真实hop；无源返回null，失败保留原原因。</summary>
    public static ProjectAudioAnalysis? ToSourceData(this AudioFeatureAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.Features.Availability == AudioAvailability.NoAudio)
            return analysis.DiagnosticCode == "AudioSourceMissing" ? null : ProjectAudioAnalysis.NoStream(analysis.DiagnosticCode!, analysis.DiagnosticMessage!);
        if (analysis.Features.Availability != AudioAvailability.Available)
            return ProjectAudioAnalysis.Failed(analysis.DiagnosticCode ?? "AudioAnalysisFailed", analysis.DiagnosticMessage ?? "音频分析未完成。");
        var features = analysis.Features;
        var data = new AudioAnalysisRevision(analysis.SourceHash!, analysis.PcmHash!, analysis.SampleCount!.Value,
            features.GetRange(new(0, features.DurationUs!.Value)).Points.Select(point => new AudioAnalysisPoint(point.TimeUs,
                point.Values.BassLevel, point.Values.Pulse, point.Values.BeatPhase, point.Values.BeatConfidence)));
        if (data.Revision != features.Revision || data.DurationUs != features.DurationUs) throw new InvalidOperationException("工程桥接与真实分析身份不符。");
        return ProjectAudioAnalysis.Available(data);
    }
}
