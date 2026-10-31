using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScriptConfigurationListResponse(string Revision, ScriptConfigurationSummaryDto[] Configurations,
    string ProtocolVersion = BackendProtocol.Version);
public sealed record ScriptConfigurationSummaryDto(string ConfigurationId, string Name, string Kind, string PackageId,
    string PackageRevision, string SourceRevision, string ExportId, string[] BindingKinds);
public sealed record ScriptConfigurationDetailResponse(string Revision, ScriptConfigurationDto Configuration,
    EffectPackageInspectResponse Package, string ProtocolVersion = BackendProtocol.Version);
public sealed record ScriptConfigurationDto(string ConfigurationId, string Name, string Kind, string PackageId,
    string PackageRevision, string SourceRevision, string ExportId, string ParametersJson,
    TemplateEffectTargetDto RelativeTarget, TemplateScriptReadScopeDto ReadScope, string? BranchInput,
    string? FixedSeed, TemplateEffectOverlayLayerDto OverlayLayer, string[] BindingKinds);

[JsonConverter(typeof(ScriptConfigurationSourceJsonConverter))]
public sealed record ScriptConfigurationSourceDto(string? DraftId = null, string? ExpectedDraftRevision = null,
    string? SourceRevision = null, string? ConfigurationId = null, string? ExpectedConfigurationRevision = null,
    EffectPackageReferenceDto? Package = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScriptConfigurationInputDto(string Name, string ExportId, string ParametersJson,
    TemplateEffectTargetDto RelativeTarget, TemplateScriptReadScopeDto ReadScope, string? BranchInput,
    string? FixedSeed, TemplateEffectOverlayLayerDto OverlayLayer, ScriptConfigurationSourceDto Source);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScriptConfigurationCreateCommandDto(string Type, ScriptConfigurationInputDto Configuration) : ScriptConfigurationCommandDto(Type);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScriptConfigurationReplaceCommandDto(string Type, string ConfigurationId, ScriptConfigurationInputDto Configuration) : ScriptConfigurationCommandDto(Type);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScriptConfigurationCopyCommandDto(string Type, string ConfigurationId, string Name) : ScriptConfigurationCommandDto(Type);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScriptConfigurationRenameCommandDto(string Type, string ConfigurationId, string Name) : ScriptConfigurationCommandDto(Type);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScriptConfigurationDeleteCommandDto(string Type, string ConfigurationId) : ScriptConfigurationCommandDto(Type);
[JsonConverter(typeof(ScriptConfigurationCommandJsonConverter))]
public abstract record ScriptConfigurationCommandDto(string Type);
[JsonConverter(typeof(ScriptConfigurationRequestJsonConverter))]
public sealed record ScriptConfigurationCommandRequest(string ExpectedRevision, RequestMetadataDto Metadata,
    ScriptConfigurationCommandDto Command);
public sealed record ScriptConfigurationCommandResponse(string PreviousRevision, string Revision, bool Changed,
    string ConfigurationId, string? PreviousConfigurationId = null, string ProtocolVersion = BackendProtocol.Version);

public sealed class ScriptConfigurationCommandJsonConverter : JsonConverter<ScriptConfigurationCommandDto>
{
    public override ScriptConfigurationCommandDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("配置命令必须为对象。");
        if (!root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) throw new JsonException("缺少配置命令type。");
        string typeName = t.GetString()!;
        var fields = typeName switch
        {
            "create" => StyleCommandJson.Fields(root, "type", "configuration"),
            "replace" => StyleCommandJson.Fields(root, "type", "configurationId", "configuration"),
            "copy" or "rename" => StyleCommandJson.Fields(root, "type", "configurationId", "name"),
            "delete" => StyleCommandJson.Fields(root, "type", "configurationId"),
            _ => throw new JsonException("未知配置命令type。"),
        };
        string Text(string name) => StyleCommandJson.String(fields[name]);
        if (typeName is "create" or "replace")
            _ = StyleCommandJson.Fields(fields["configuration"], "name", "exportId", "parametersJson", "relativeTarget", "readScope", "branchInput", "fixedSeed", "overlayLayer", "source");
        return typeName switch
        {
            "create" => new ScriptConfigurationCreateCommandDto(typeName, JsonSerializer.Deserialize<ScriptConfigurationInputDto>(fields["configuration"], options)!),
            "replace" => new ScriptConfigurationReplaceCommandDto(typeName, Text("configurationId"), JsonSerializer.Deserialize<ScriptConfigurationInputDto>(fields["configuration"], options)! ),
            "copy" => new ScriptConfigurationCopyCommandDto(typeName, Text("configurationId"), Text("name")),
            "rename" => new ScriptConfigurationRenameCommandDto(typeName, Text("configurationId"), Text("name")),
            "delete" => new ScriptConfigurationDeleteCommandDto(typeName, Text("configurationId")),
            _ => throw new JsonException("未知配置命令type。"),
        };
    }
    public override void Write(Utf8JsonWriter writer, ScriptConfigurationCommandDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("type", value.Type);
        switch (value)
        {
            case ScriptConfigurationCreateCommandDto c: writer.WritePropertyName("configuration"); JsonSerializer.Serialize(writer, c.Configuration, options); break;
            case ScriptConfigurationReplaceCommandDto r: writer.WriteString("configurationId", r.ConfigurationId); writer.WritePropertyName("configuration"); JsonSerializer.Serialize(writer, r.Configuration, options); break;
            case ScriptConfigurationCopyCommandDto c: writer.WriteString("configurationId", c.ConfigurationId); writer.WriteString("name", c.Name); break;
            case ScriptConfigurationRenameCommandDto r: writer.WriteString("configurationId", r.ConfigurationId); writer.WriteString("name", r.Name); break;
            case ScriptConfigurationDeleteCommandDto d: writer.WriteString("configurationId", d.ConfigurationId); break;
            default: throw new JsonException("未知配置命令。");
        }
        writer.WriteEndObject();
    }
}

public sealed class ScriptConfigurationSourceJsonConverter : JsonConverter<ScriptConfigurationSourceDto>
{
    public override ScriptConfigurationSourceDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("配置来源必须为对象。");
        if (root.TryGetProperty("draftId", out _))
        {
            var f = StyleCommandJson.Fields(root, "draftId", "expectedDraftRevision", "sourceRevision");
            return new(DraftId: StyleCommandJson.String(f["draftId"]), ExpectedDraftRevision: StyleCommandJson.String(f["expectedDraftRevision"]), SourceRevision: StyleCommandJson.String(f["sourceRevision"]));
        }
        if (root.TryGetProperty("configurationId", out _))
        {
            var f = StyleCommandJson.Fields(root, "configurationId", "expectedConfigurationRevision");
            return new(ConfigurationId: StyleCommandJson.String(f["configurationId"]), ExpectedConfigurationRevision: StyleCommandJson.String(f["expectedConfigurationRevision"]));
        }
        var packageFields = StyleCommandJson.Fields(root, "package");
        return new(Package: JsonSerializer.Deserialize<EffectPackageReferenceDto>(packageFields["package"], options) ?? throw new JsonException("包来源不能为空。"));
    }
    public override void Write(Utf8JsonWriter writer, ScriptConfigurationSourceDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        Put("draftId", value.DraftId); Put("expectedDraftRevision", value.ExpectedDraftRevision); Put("sourceRevision", value.SourceRevision);
        Put("configurationId", value.ConfigurationId); Put("expectedConfigurationRevision", value.ExpectedConfigurationRevision);
        if (value.Package is not null) { writer.WritePropertyName("package"); JsonSerializer.Serialize(writer, value.Package, options); }
        writer.WriteEndObject();
        void Put(string name, string? text) { if (text is not null) writer.WriteString(name, text); }
    }
}

public sealed class ScriptConfigurationRequestJsonConverter : JsonConverter<ScriptConfigurationCommandRequest>
{
    public override ScriptConfigurationCommandRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var f = StyleCommandJson.Fields(doc.RootElement, "expectedRevision", "metadata", "command");
        _ = StyleCommandJson.Fields(f["metadata"], "requestId", "hostInstanceId", "validUntilUtc");
        return new(StyleCommandJson.String(f["expectedRevision"]), JsonSerializer.Deserialize<RequestMetadataDto>(f["metadata"], options)!,
            JsonSerializer.Deserialize<ScriptConfigurationCommandDto>(f["command"], options)!);
    }
    public override void Write(Utf8JsonWriter writer, ScriptConfigurationCommandRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("expectedRevision", value.ExpectedRevision);
        writer.WritePropertyName("metadata"); JsonSerializer.Serialize(writer, value.Metadata, options);
        writer.WritePropertyName("command"); JsonSerializer.Serialize(writer, value.Command, options); writer.WriteEndObject();
    }
}
