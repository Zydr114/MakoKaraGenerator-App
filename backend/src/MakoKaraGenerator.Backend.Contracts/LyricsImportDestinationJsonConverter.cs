using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>严格解析导入目标判别联合，拒绝重复、未知、混合或空字段。</summary>
public sealed class LyricsImportDestinationJsonConverter : JsonConverter<LyricsImportDestinationDto>
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeof(LyricsImportDestinationDto).IsAssignableFrom(typeToConvert);
    /// <inheritdoc />
    public override LyricsImportDestinationDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        var fields = ImportCommandJson.Fields(document.RootElement, options);
        string kind = ImportCommandJson.RequiredString(fields, "kind");
        LyricsImportDestinationDto result;
        switch (kind)
        {
            case "existing":
                ImportCommandJson.Exact(fields, ["kind", "branchId"], ["kind", "branchId"]);
                result = new ExistingLyricsDestinationDto(ImportCommandJson.RequiredString(fields, "branchId")); break;
            case "newBranch":
                ImportCommandJson.Exact(fields, ["kind", "name", "role"], ["kind", "name", "role"]);
                ProjectLyricBranchRole role = ImportCommandJson.Enum<ProjectLyricBranchRole>(fields["role"]);
                if (role == ProjectLyricBranchRole.Primary) throw new JsonException("新来源角色不能是 primary。");
                result = new NewBranchLyricsDestinationDto(ImportCommandJson.RequiredString(fields, "name"), role); break;
            case "newTranslation":
                ImportCommandJson.Exact(fields, ["kind", "name"], ["kind", "name"]);
                result = new NewTranslationLyricsDestinationDto(ImportCommandJson.RequiredString(fields, "name")); break;
            case "allSources":
                ImportCommandJson.Exact(fields, ["kind"], ["kind"]);
                result = new AllSourcesLyricsDestinationDto(); break;
            default: throw new JsonException("destination.kind 不受支持。");
        }
        if (!typeToConvert.IsInstanceOfType(result)) throw new JsonException("目标 DTO 类型不一致。");
        return result;
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, LyricsImportDestinationDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("kind", value.Kind);
        switch (value)
        {
            case ExistingLyricsDestinationDto existing: writer.WriteString("branchId", existing.BranchId); break;
            case NewBranchLyricsDestinationDto branch:
                writer.WriteString("name", branch.Name); writer.WriteString("role", JsonNamingPolicy.CamelCase.ConvertName(branch.Role.ToString())); break;
            case NewTranslationLyricsDestinationDto translation: writer.WriteString("name", translation.Name); break;
            case AllSourcesLyricsDestinationDto: break;
            default: throw new JsonException("未知目标 DTO。");
        }
        writer.WriteEndObject();
    }
}

internal static class ImportCommandJson
{
    internal static Dictionary<string, JsonElement> Fields(JsonElement root, JsonSerializerOptions options)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("必须是对象。");
        var fields = new Dictionary<string, JsonElement>(options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!fields.TryAdd(property.Name, property.Value)) throw new JsonException("字段不能重复。");
        return fields;
    }
    internal static void Exact(Dictionary<string, JsonElement> fields, string[] allowed, string[] required)
    {
        if (fields.Keys.Any(key => !allowed.Any(value => fields.Comparer.Equals(value, key))) || required.Any(key => !fields.ContainsKey(key)))
            throw new JsonException("包含未知字段或缺少必填字段。");
    }
    internal static string RequiredString(Dictionary<string, JsonElement> fields, string key) =>
        fields.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException("必填字段必须是字符串。");
    internal static T Enum<T>(JsonElement value) where T : struct, Enum
    {
        if (value.ValueKind != JsonValueKind.String) throw new JsonException("枚举必须是命名字符串。");
        string? text = value.GetString();
        foreach (T item in System.Enum.GetValues<T>())
            if (JsonNamingPolicy.CamelCase.ConvertName(item.ToString()) == text) return item;
        throw new JsonException("枚举名称非法。");
    }
    internal static ProjectCommandDto Read(JsonElement root, string type, JsonSerializerOptions options)
    {
        var fields = Fields(root, options);
        bool import = type == "lyrics.importAsset";
        Exact(fields, import ? ["type", "assetId", "destination", "options"] : ["type", "text", "destination"],
            import ? ["type", "assetId", "destination"] : ["type", "text", "destination"]);
        var destination = fields["destination"].Deserialize<LyricsImportDestinationDto>(options) ?? throw new JsonException("destination 不能为空。");
        if (import && destination is NewTranslationLyricsDestinationDto
            || !import && destination is NewBranchLyricsDestinationDto or AllSourcesLyricsDestinationDto)
            throw new JsonException("命令不接受此目标种类。");
        if (!import) return new AppendTextCommandDto(RequiredString(fields, "text"), destination);
        LyricsImportOptionsDto? selected = fields.TryGetValue("options", out var option) ? ReadOptions(option, options) : null;
        return new ImportAssetCommandDto(RequiredString(fields, "assetId"), destination, selected);
    }
    internal static LyricsImportOptionsDto ReadOptions(JsonElement option, JsonSerializerOptions options)
    {
        var values = Fields(option, options); Exact(values, ["importerId", "encodingName", "splitRule", "estimateSplitTimes", "sourceBranchId"], []);
        string? OptionalString(string key) => !values.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null ? null
            : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new JsonException("选项必须是字符串或空。");
        bool estimate = false;
        if (values.TryGetValue("estimateSplitTimes", out var flag))
            estimate = flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : throw new JsonException("estimateSplitTimes 必须是布尔值。");
        string? sourceBranchId = values.ContainsKey("sourceBranchId") ? RequiredString(values, "sourceBranchId") : null;
        if (sourceBranchId is not null && (sourceBranchId.Length is 0 or > 128 || sourceBranchId.Any(char.IsWhiteSpace)))
            throw new JsonException("sourceBranchId 必须为有效来源标识。");
        return new(OptionalString("importerId"), OptionalString("encodingName"),
            values.TryGetValue("splitRule", out var rule) ? Enum<LyricsImportSplitRule>(rule) : LyricsImportSplitRule.None, estimate, sourceBranchId);
    }
    internal static void WriteOptions(Utf8JsonWriter writer, LyricsImportOptionsDto selected)
    {
        writer.WriteStartObject();
        if (selected.ImporterId is not null) writer.WriteString("importerId", selected.ImporterId);
        if (selected.EncodingName is not null) writer.WriteString("encodingName", selected.EncodingName);
        if (selected.SourceBranchId is not null) writer.WriteString("sourceBranchId", selected.SourceBranchId);
        writer.WriteString("splitRule", JsonNamingPolicy.CamelCase.ConvertName(selected.SplitRule.ToString()));
        writer.WriteBoolean("estimateSplitTimes", selected.EstimateSplitTimes);
        writer.WriteEndObject();
    }

    internal static void Write(Utf8JsonWriter writer, ProjectCommandDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("type", value.Type);
        switch (value)
        {
            case ImportAssetCommandDto import:
                writer.WriteString("assetId", import.AssetId); writer.WritePropertyName("destination"); JsonSerializer.Serialize(writer, import.Destination, options);
                if (import.Options is { } selected)
                {
                    writer.WritePropertyName("options");
                    WriteOptions(writer, selected);
                }
                break;
            case AppendTextCommandDto append:
                writer.WriteString("text", append.Text); writer.WritePropertyName("destination"); JsonSerializer.Serialize(writer, append.Destination, options); break;
            default: throw new JsonException("未知导入命令 DTO。");
        }
        // Preserve invalid inherited slots in the receipt fingerprint; runtime admission rejects them.
        if (value.Target is not null) { writer.WritePropertyName("target"); JsonSerializer.Serialize(writer, value.Target, options); }
        if (value.StartUs is not null) writer.WriteString("startUs", value.StartUs);
        if (value.EndUs is not null) writer.WriteString("endUs", value.EndUs);
        if (value.AnalysisJobId is not null) writer.WriteString("analysisJobId", value.AnalysisJobId);
        writer.WriteEndObject();
    }
}
