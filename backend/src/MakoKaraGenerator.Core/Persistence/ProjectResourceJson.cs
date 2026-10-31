using System.Text.Json;
using MakoKaraGenerator.Core.Projects;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Persistence;

internal static class ProjectResourceJson
{
    internal static JsonElement Media(Project project)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        { writer.WriteStartObject(); writer.WritePropertyName("audio"); WriteMedia(writer, project.AudioMedia); writer.WritePropertyName("background"); WriteMedia(writer, project.Background.Media); writer.WriteEndObject(); }
        return Element(stream.ToArray());
    }
    private static void WriteMedia(Utf8JsonWriter writer, MediaFileRevision? value)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject(); writer.WriteString("hash", value.Hash); writer.WriteNumber("byteLength", value.ByteLength); writer.WriteString("originalName", value.OriginalName); writer.WriteEndObject();
    }
    internal static (MediaFileRevision? Audio, MediaFileRevision? Background) ReadMedia(JsonElement? value, string? audioPath, string? backgroundPath, string directory, ProjectStoreLimits limits)
    {
        if (value is null) throw new IOException("工程缺少media源闭包。");
        var root = value.Value; Keys(root, "media", ["audio", "background"], []);
        return (Read(root.GetProperty("audio"), audioPath), Read(root.GetProperty("background"), backgroundPath));
        MediaFileRevision? Read(JsonElement data, string? path)
        {
            if (data.ValueKind == JsonValueKind.Null)
            { if (path is not null) throw new IOException("媒体路径存在但没有固定修订。"); return null; }
            Keys(data, "media", ["hash", "byteLength", "originalName"], []);
            var reference = new MediaFileRevision(RequiredText(data, "hash", "media"), Whole(data.GetProperty("byteLength"), "media.byteLength"), RequiredText(data, "originalName", "media"));
            if (path != reference.RelativePath) throw new IOException("媒体路径必须等于固定内容地址。");
            ContentAddressFileStore.Verify(Path.Combine(directory, reference.RelativePath), reference.Hash, reference.ByteLength, limits.MaximumMediaBytes);
            return reference;
        }
    }
    internal static JsonElement PublishAnalysis(Project project, string directory, ProjectStoreLimits limits)
    {
        var analysis = project.AudioAnalysis;
        if (project.AudioPath is null)
        {
            if (analysis is not null) throw new IOException("没有音频源时不能保存旧分析。");
            return Element("{\"state\":\"noAudio\"}"u8.ToArray());
        }
        if (analysis is null) return Element("{\"state\":\"notAnalyzed\"}"u8.ToArray());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (analysis.Data is { } data)
            {
                if (project.AudioMedia?.Hash != data.SourceHash) throw new IOException("分析源hash与本次固定音频不符；请接受新分析。");
                ContentAddressFileStore.PublishBytes(directory, data.RelativePath, AudioAnalysisJson.Encode(data), limits.MaximumAnalysisBytes);
                writer.WriteString("state", "available"); writer.WriteString("file", data.RelativePath); writer.WriteString("revision", data.Revision);
            }
            else { writer.WriteString("state", "failed"); writer.WriteString("availability", analysis.MissingStream ? "noAudio" : "analysisFailed"); writer.WriteString("code", analysis.DiagnosticCode); writer.WriteString("message", analysis.DiagnosticMessage); }
            writer.WriteEndObject();
        }
        return Element(stream.ToArray());
    }
    internal static ProjectAudioAnalysis? ReadAnalysis(JsonElement? value, MediaFileRevision? audio, string directory, ProjectStoreLimits limits)
    {
        if (value is null) throw new IOException("工程缺少audioAnalysis状态。");
        var root = value.Value; string state = RequiredText(root, "state", "audioAnalysis");
        string[] keys = state switch { "noAudio" or "notAnalyzed" => ["state"], "failed" => ["state", "availability", "code", "message"], "available" => ["state", "file", "revision"], _ => throw Type("audioAnalysis.state") };
        Keys(root, "audioAnalysis", keys, []);
        if (state == "noAudio" ? audio is not null : audio is null) throw new IOException("分析状态与音频源存在性不符。");
        if (state is "noAudio" or "notAnalyzed") return null;
        if (state == "failed") return RequiredText(root, "availability", "audioAnalysis") switch
        {
            "noAudio" => ProjectAudioAnalysis.NoStream(RequiredText(root, "code", "audioAnalysis"), RequiredText(root, "message", "audioAnalysis")),
            "analysisFailed" => ProjectAudioAnalysis.Failed(RequiredText(root, "code", "audioAnalysis"), RequiredText(root, "message", "audioAnalysis")),
            _ => throw Type("audioAnalysis.availability"),
        };
        string file = RequiredText(root, "file", "audioAnalysis"); const string prefix = "data/analysis/";
        if (file.Length != prefix.Length + 64 + 5 || !file.StartsWith(prefix, StringComparison.Ordinal) || !file.EndsWith(".json", StringComparison.Ordinal)) throw new IOException("分析文件必须为规范内容地址。");
        string hash = file.Substring(prefix.Length, 64); MediaFileRevision.ValidateHash(hash);
        var data = AudioAnalysisJson.Read(ContentAddressFileStore.ReadBytes(Path.Combine(directory, file), hash, limits.MaximumAnalysisBytes));
        if (data.ContentHash != hash || data.Revision != RequiredText(root, "revision", "audioAnalysis") || data.SourceHash != audio!.Hash) throw new IOException("分析字节、语义修订或源音频身份不符。");
        return ProjectAudioAnalysis.Available(data);
    }
    private static JsonElement Element(byte[] bytes) { using var document = JsonDocument.Parse(bytes); return document.RootElement.Clone(); }
}
