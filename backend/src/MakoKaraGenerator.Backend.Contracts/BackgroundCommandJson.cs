using System.Text.Json;

namespace MakoKaraGenerator.Backend.Contracts;

public sealed record SetBackgroundAssetCommandDto(string? AssetId, string? Kind, string? Fit = null, string? Playback = null)
    : ProjectCommandDto("composition.setBackgroundAsset");
public sealed record ClearBackgroundCommandDto() : ProjectCommandDto("composition.clearBackground");
public sealed record SetBackgroundFitCommandDto(string? Fit) : ProjectCommandDto("composition.setBackgroundFit");
public sealed record SetBackgroundPlaybackCommandDto(string? Playback) : ProjectCommandDto("composition.setBackgroundPlayback");

internal static class BackgroundCommandJson
{
    internal static bool IsType(string? type) => type is "composition.setBackgroundAsset" or "composition.clearBackground"
        or "composition.setBackgroundFit" or "composition.setBackgroundPlayback";
    internal static ProjectCommandDto Read(JsonElement root, string type)
    {
        string[] allowed = type switch
        {
            "composition.setBackgroundAsset" => ["type", "assetId", "kind", "fit", "playback"],
            "composition.setBackgroundFit" => ["type", "fit"],
            "composition.setBackgroundPlayback" => ["type", "playback"],
            _ => ["type"],
        };
        Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
        foreach (JsonProperty field in root.EnumerateObject())
            if (!allowed.Contains(field.Name, StringComparer.Ordinal) || !fields.TryAdd(field.Name, field.Value))
                throw new JsonException("背景命令含未知或重复字段。");
        string? Read(string key, bool optional = false)
        {
            if (!fields.TryGetValue(key, out JsonElement value) && optional) return null;
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new JsonException(key + " 必须是非空字符串。");
            return value.GetString();
        }
        return type switch
        {
            "composition.setBackgroundAsset" => new SetBackgroundAssetCommandDto(Read("assetId"), Read("kind"), Read("fit", true), Read("playback", true)),
            "composition.setBackgroundFit" => new SetBackgroundFitCommandDto(Read("fit")),
            "composition.setBackgroundPlayback" => new SetBackgroundPlaybackCommandDto(Read("playback")),
            _ => new ClearBackgroundCommandDto(),
        };
    }
    internal static void Write(Utf8JsonWriter writer, ProjectCommandDto command)
    {
        writer.WriteStartObject(); writer.WriteString("type", command.Type);
        if (command is SetBackgroundAssetCommandDto asset)
        {
            writer.WriteString("assetId", asset.AssetId); writer.WriteString("kind", asset.Kind);
            if (asset.Fit is not null) writer.WriteString("fit", asset.Fit);
            if (asset.Playback is not null) writer.WriteString("playback", asset.Playback);
        }
        if (command is SetBackgroundFitCommandDto fit) writer.WriteString("fit", fit.Fit);
        if (command is SetBackgroundPlaybackCommandDto playback) writer.WriteString("playback", playback.Playback);
        writer.WriteEndObject();
    }
}
