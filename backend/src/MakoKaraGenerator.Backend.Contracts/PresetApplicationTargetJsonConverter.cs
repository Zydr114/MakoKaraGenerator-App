using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>Shared target union for assignment commands and read-only candidate previews.</summary>
public sealed class PresetApplicationTargetJsonConverter : JsonConverter<PresetApplicationTargetDto>
{
    public override PresetApplicationTargetDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
        Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
            if (property.Name is not ("kind" or "branchId" or "lineId" or "tokenId" or "nodeId" or "includeDescendants" or "clipIds")
                || !fields.TryAdd(property.Name, property.Value)) throw Invalid();
        string? Text(string name)
        {
            if (!fields.TryGetValue(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : throw Invalid();
        }
        bool? descendants = null;
        if (fields.TryGetValue("includeDescendants", out JsonElement flag) && flag.ValueKind != JsonValueKind.Null)
            descendants = flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : throw Invalid();
        string[]? clips = null;
        if (fields.TryGetValue("clipIds", out JsonElement clipIds) && clipIds.ValueKind != JsonValueKind.Null)
        {
            if (clipIds.ValueKind != JsonValueKind.Array) throw Invalid();
            clips = [.. clipIds.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw Invalid())];
        }
        PresetApplicationTargetDto result = new(Text("kind") ?? throw Invalid(), Text("branchId"), Text("lineId"),
            Text("tokenId"), Text("nodeId"), descendants, clips);
        Validate(result);
        return result;
    }

    public override void Write(Utf8JsonWriter writer, PresetApplicationTargetDto value, JsonSerializerOptions options)
    {
        Validate(value);
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);
        if (value.BranchId is not null) writer.WriteString("branchId", value.BranchId);
        if (value.LineId is not null) writer.WriteString("lineId", value.LineId);
        if (value.TokenId is not null) writer.WriteString("tokenId", value.TokenId);
        if (value.NodeId is not null) writer.WriteString("nodeId", value.NodeId);
        if (value.IncludeDescendants.HasValue) writer.WriteBoolean("includeDescendants", value.IncludeDescendants.Value);
        if (value.ClipIds is not null)
        {
            writer.WriteStartArray("clipIds");
            foreach (string clip in value.ClipIds) writer.WriteStringValue(clip);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    public static void Validate(PresetApplicationTargetDto value)
    {
        if (value is null) throw Invalid();
        bool Text(string? text) => !string.IsNullOrWhiteSpace(text);
        bool lyric = value.NodeId is null && value.IncludeDescendants is null && value.ClipIds is null;
        bool valid = value.Kind switch
        {
            "project" => lyric && value.BranchId is null && value.LineId is null && value.TokenId is null,
            "branch" => lyric && Text(value.BranchId) && value.LineId is null && value.TokenId is null,
            "line" => lyric && Text(value.BranchId) && Text(value.LineId) && value.TokenId is null,
            "token" => lyric && Text(value.BranchId) && Text(value.LineId) && Text(value.TokenId),
            "clips" => value.NodeId is null && value.IncludeDescendants is null && Text(value.BranchId)
                && value.LineId is null && value.TokenId is null && value.ClipIds is { Length: > 0 and <= 1000 } clips
                && clips.All(Text) && clips.Distinct(StringComparer.Ordinal).Count() == clips.Length,
            "node" => value.ClipIds is null && Text(value.NodeId) && value.NodeId!.Length <= 128 && value.IncludeDescendants.HasValue
                && value.BranchId is null && value.LineId is null && value.TokenId is null,
            _ => false,
        };
        if (!valid) throw Invalid();
    }

    private static JsonException Invalid() => new("预设目标必须严格匹配 project、branch、line、token、clips 或 node；clips 要求 branchId 和1至1000个唯一 clipIds；node 要求 nodeId 和显式 includeDescendants，nodeId最多128字符。");
}
