using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>显式完整定义分支与旧静态分支共用的严格模板来源联合。</summary>
public sealed class TemplatePreviewSourceJsonConverter : JsonConverter<TemplatePreviewSourceDto>
{
    /// <inheritdoc />
    public override TemplatePreviewSourceDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
        Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
        foreach (var field in document.RootElement.EnumerateObject())
            if (field.Name is not ("kind" or "libraryId" or "libraryRevision" or "templateId" or "templateRevision"
                or "draftRevision" or "definition" or "definitionFormat") || !fields.TryAdd(field.Name, field.Value)) throw Invalid();
        string? Text(string name) => !fields.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : throw Invalid();
        string? format = Text("definitionFormat");
        if (format is not (null or "static" or "full")) throw Invalid();
        StaticTemplateDefinitionDto? definition = null;
        if (fields.TryGetValue("definition", out var body) && body.ValueKind != JsonValueKind.Null)
            definition = format == "full" ? body.Deserialize<FullTemplateDefinitionDto>(options)
                : body.Deserialize<StaticTemplateDefinitionDto>(options);
        return new(Text("kind") ?? throw Invalid(), Text("libraryId"), Text("libraryRevision"), Text("templateId"),
            Text("templateRevision"), Text("draftRevision"), definition, format);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TemplatePreviewSourceDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("kind", value.Kind);
        Put("libraryId", value.LibraryId); Put("libraryRevision", value.LibraryRevision);
        Put("templateId", value.TemplateId); Put("templateRevision", value.TemplateRevision);
        Put("draftRevision", value.DraftRevision); Put("definitionFormat", value.DefinitionFormat);
        if (value.Definition is not null)
        {
            writer.WritePropertyName("definition");
            JsonSerializer.Serialize(writer, value.Definition, options);
        }
        writer.WriteEndObject();
        void Put(string name, string? text) { if (text is not null) writer.WriteString(name, text); }
    }

    private static JsonException Invalid() => new("模板来源字段或definitionFormat非法。");
}
