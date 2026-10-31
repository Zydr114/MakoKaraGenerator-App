using System.Text.Json;

namespace MakoKaraGenerator.Backend.Contracts;

internal static class BasicMediaCommandJson
{
    internal static bool IsType(string? type) => type is "audio.replaceAsset" or "audio.clear" or "project.rename";
    internal static ProjectCommandDto Read(JsonElement root, string type)
    {
        string[] names = type switch { "audio.replaceAsset" => ["type", "assetId"], "project.rename" => ["type", "name"], _ => ["type"] };
        Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (!names.Contains(field.Name, StringComparer.Ordinal) || !fields.TryAdd(field.Name, field.Value)) throw new JsonException("工程媒体命令含未知或重复字段。");
        if (fields.Count != names.Length) throw new JsonException("工程媒体命令字段不完整。");
        if (type == "audio.clear") return new ClearProjectAudioCommandDto();
        if (type == "audio.replaceAsset")
            return fields["assetId"].ValueKind == JsonValueKind.String && fields["assetId"].GetString() is { Length: > 0 and <= 128 } id
                && !string.IsNullOrWhiteSpace(id) ? new ReplaceProjectAudioAssetCommandDto(id) : throw new JsonException("assetId 必须是稳定资源身份。");
        return fields["name"].ValueKind switch
        {
            JsonValueKind.Null => new RenameProjectCommandDto(null),
            JsonValueKind.String => new RenameProjectCommandDto(fields["name"].GetString()),
            _ => throw new JsonException("name 必须是字符串或显式null。"),
        };
    }
    internal static void Write(Utf8JsonWriter writer, ProjectCommandDto command, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("type", command.Type);
        if (command is ReplaceProjectAudioAssetCommandDto replace) writer.WriteString("assetId", replace.AssetId);
        if (command is RenameProjectCommandDto rename) writer.WriteString("name", rename.Name);
        if (command.Target is not null) { writer.WritePropertyName("target"); JsonSerializer.Serialize(writer, command.Target, options); }
        if (command.StartUs is not null) writer.WriteString("startUs", command.StartUs);
        if (command.EndUs is not null) writer.WriteString("endUs", command.EndUs);
        if (command.AnalysisJobId is not null) writer.WriteString("analysisJobId", command.AnalysisJobId);
        writer.WriteEndObject();
    }
}
