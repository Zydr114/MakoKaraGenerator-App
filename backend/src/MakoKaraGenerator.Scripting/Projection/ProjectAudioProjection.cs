using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Projection;

/// <summary>工程已接受的分析转换成既有只读SDK查询，不生成或补齐特征。</summary>
public static class ProjectAudioProjection
{
    /// <summary>无源、未完成、失败和真实可用数据均保留原语义。</summary>
    public static AudioFeatures Create(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Create(project.AudioPath is not null, project.AudioMedia, project.AudioAnalysis);
    }
    /// <summary>与源工程使用相同固定输入，不构造丢失音频语义的临时Project。</summary>
    public static AudioFeatures Create(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Create(snapshot.AudioPath is not null, snapshot.AudioMedia, snapshot.AudioAnalysis);
    }
    private static AudioFeatures Create(bool hasAudio, MediaFileRevision? media, ProjectAudioAnalysis? analysis)
    {
        if (!hasAudio) return AudioFeatures.Unavailable(AudioAvailability.NoAudio, "AudioSourceMissing");
        if (analysis is null) return AudioFeatures.NotAnalyzed;
        if (analysis.Data is not { } data) return AudioFeatures.Unavailable(analysis.MissingStream ? AudioAvailability.NoAudio : AudioAvailability.AnalysisFailed, analysis.DiagnosticCode!);
        if (media is not null && media.Hash != data.SourceHash) throw new ArgumentException("固定分析与源音频身份不符。", nameof(analysis));
        return new(data.Revision, data.DurationUs, [.. data.Points.Select(point => new AudioFeaturePoint(point.TimeUs,
            new(point.BassLevel, point.Pulse, point.BeatPhase, point.BeatConfidence)))]);
    }
}
