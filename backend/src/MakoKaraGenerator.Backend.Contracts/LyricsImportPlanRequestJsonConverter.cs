using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>与 importAsset 共享严格选项语义。</summary>
public sealed class LyricsImportPlanRequestJsonConverter : JsonConverter<LyricsImportPlanRequest>
{
    /// <inheritdoc />
    public override LyricsImportPlanRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        var fields = ImportCommandJson.Fields(document.RootElement, options);
        ImportCommandJson.Exact(fields, ["assetId", "options"], ["assetId"]);
        return new(ImportCommandJson.RequiredString(fields, "assetId"),
            fields.TryGetValue("options", out var selected) ? ImportCommandJson.ReadOptions(selected, options) : null);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, LyricsImportPlanRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("assetId", value.AssetId);
        if (value.Options is { } selected)
        {
            writer.WritePropertyName("options");
            ImportCommandJson.WriteOptions(writer, selected);
        }
        writer.WriteEndObject();
    }
}
