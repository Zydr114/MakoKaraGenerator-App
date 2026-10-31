using System.Text.Json;
using MakoKaraGenerator.Core.Projects;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>完整分析文件的唯一规范编码；不执行算法、FFmpeg或作者代码。</summary>
public static class AudioAnalysisJson
{
    /// <summary>编码全部元数据与真实hop，数值保留double精度。</summary>
    public static byte[] Encode(AudioAnalysisRevision analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis); using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 1); writer.WriteString("algorithm", AudioAnalysisProfile.AlgorithmVersion);
            writer.WriteNumber("sampleRate", AudioAnalysisProfile.SampleRate); writer.WriteNumber("hopSamples", AudioAnalysisProfile.HopSamples); writer.WriteNumber("originUs", 0);
            writer.WriteString("sourceHash", analysis.SourceHash); writer.WriteString("pcmHash", analysis.PcmHash); writer.WriteNumber("sampleCount", analysis.SampleCount);
            writer.WriteNumber("durationUs", analysis.DurationUs); writer.WriteString("revision", analysis.Revision); writer.WriteStartArray("points");
            foreach (var point in analysis.Points)
            {
                writer.WriteStartObject(); writer.WriteNumber("timeUs", point.TimeUs); writer.WriteNumber("bassLevel", point.BassLevel); writer.WriteNumber("pulse", point.Pulse);
                if (point.BeatPhase is { } phase) writer.WriteNumber("beatPhase", phase); else writer.WriteNull("beatPhase");
                writer.WriteNumber("beatConfidence", point.BeatConfidence); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return stream.ToArray();
    }
    /// <summary>严格解析当前算法与全部真实网格；坏值不修正为静音。</summary>
    public static AudioAnalysisRevision Read(ReadOnlyMemory<byte> bytes)
    {
        using var document = ReadJson(bytes, "audioAnalysis"); var root = document.RootElement;
        Keys(root, "audioAnalysis", ["version", "algorithm", "sampleRate", "hopSamples", "originUs", "sourceHash", "pcmHash", "sampleCount", "durationUs", "revision", "points"], []);
        if (Whole(root.GetProperty("version"), "version") != 1 || RequiredText(root, "algorithm", "analysis") != AudioAnalysisProfile.AlgorithmVersion
            || Whole(root.GetProperty("sampleRate"), "sampleRate") != AudioAnalysisProfile.SampleRate || Whole(root.GetProperty("hopSamples"), "hopSamples") != AudioAnalysisProfile.HopSamples
            || Whole(root.GetProperty("originUs"), "originUs") != 0) throw Range("audioAnalysis.profile");
        List<AudioAnalysisPoint> points = [];
        foreach (var point in Array(root.GetProperty("points"), "points"))
        {
            Keys(point, "points", ["timeUs", "bassLevel", "pulse", "beatPhase", "beatConfidence"], []);
            points.Add(new(Whole(point.GetProperty("timeUs"), "timeUs"), Number(point.GetProperty("bassLevel"), "bassLevel"), Number(point.GetProperty("pulse"), "pulse"),
                point.GetProperty("beatPhase").ValueKind == JsonValueKind.Null ? null : Number(point.GetProperty("beatPhase"), "beatPhase"), Number(point.GetProperty("beatConfidence"), "beatConfidence")));
        }
        var result = new AudioAnalysisRevision(RequiredText(root, "sourceHash", "analysis"), RequiredText(root, "pcmHash", "analysis"), Whole(root.GetProperty("sampleCount"), "sampleCount"), points);
        if (result.DurationUs != Whole(root.GetProperty("durationUs"), "durationUs") || result.Revision != RequiredText(root, "revision", "analysis")) throw Range("audioAnalysis.revision");
        if (!Encode(result).AsSpan().SequenceEqual(bytes.Span)) throw Error("AudioAnalysisNotCanonical", "audioAnalysis", "分析文件必须使用规范完整编码。");
        return result;
    }
}
