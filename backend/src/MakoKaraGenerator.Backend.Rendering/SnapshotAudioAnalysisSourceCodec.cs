using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Rendering;

internal static class SnapshotAudioAnalysisSourceCodec
{
    internal static JsonElement? Encode(ProjectAudioAnalysis? analysis)
    {
        if (analysis is null) return null;
        using var stream = new StaticProjectSnapshotCodec.BoundedSnapshotStream(StaticProjectSnapshotCodec.MaximumBytes);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (analysis.Data is { } data)
            {
                byte[] canonical;
                try { canonical = BoundedAudioAnalysisCodec.Encode(data); }
                catch (InvalidDataException error) { throw Invalid(error); }
                writer.WriteString("state", "available");
                writer.WriteString("contentHash", data.ContentHash);
                writer.WritePropertyName("data");
                writer.WriteRawValue(canonical);
            }
            else
            {
                if ((long)analysis.DiagnosticCode!.Length + analysis.DiagnosticMessage!.Length > StaticProjectSnapshotCodec.MaximumBytes)
                    throw new SnapshotContractException("SnapshotSizeExceeded", "分析诊断源文本超过快照预算。");
                writer.WriteString("state", analysis.MissingStream ? "noStream" : "failed");
                writer.WriteString("code", analysis.DiagnosticCode);
                writer.WriteString("message", analysis.DiagnosticMessage);
            }
            writer.WriteEndObject();
        }
        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    internal static ProjectAudioAnalysis? Decode(JsonElement? value, string? sourceSha256)
    {
        if (value is null) return null;
        try
        {
            JsonElement root = value.Value;
            string state = root.GetProperty("state").GetString() ?? throw Invalid();
            switch (state)
            {
                case "available":
                    Keys(root, ["state", "contentHash", "data"]);
                    if (sourceSha256 is null) throw Invalid();
                    byte[] canonical = Encoding.UTF8.GetBytes(root.GetProperty("data").GetRawText());
                    AudioAnalysisRevision data = BoundedAudioAnalysisCodec.Decode(canonical, sourceSha256);
                    if (root.GetProperty("contentHash").GetString() != data.ContentHash) throw Invalid();
                    return ProjectAudioAnalysis.Available(data);
                case "failed":
                    Keys(root, ["state", "code", "message"]);
                    return ProjectAudioAnalysis.Failed(root.GetProperty("code").GetString()!, root.GetProperty("message").GetString()!);
                case "noStream":
                    Keys(root, ["state", "code", "message"]);
                    return ProjectAudioAnalysis.NoStream(root.GetProperty("code").GetString()!, root.GetProperty("message").GetString()!);
                default:
                    throw Invalid();
            }
        }
        catch (SnapshotContractException) { throw; }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { throw Invalid(error); }
    }

    private static void Keys(JsonElement value, string[] expected)
    {
        string[] actual = [.. value.EnumerateObject().Select(property => property.Name)];
        if (actual.Length != expected.Length || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected)) throw Invalid();
    }

    private static SnapshotContractException Invalid(Exception? error = null) =>
        new("SnapshotAnalysisInvalid", "快照完整分析状态、规范数据或固定身份无效。", error);
}
