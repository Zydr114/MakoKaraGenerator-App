using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>模板库新增输入超限，区别于形状错误。</summary>
public sealed class TemplateLibraryBudgetExceededException : JsonException
{
    /// <summary>创建固定输入预算错误。</summary>
    public TemplateLibraryBudgetExceededException() : base("模板库命令超过新增输入预算。") { }
}
/// <summary>完整静态定义的严格读取；旧读数据不套新增值限额。</summary>
public sealed class StaticTemplateDefinitionJsonConverter : JsonConverter<StaticTemplateDefinitionDto>
{
    /// <inheritdoc />
    public override StaticTemplateDefinitionDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return TemplateLibraryJson.Definition(document.RootElement);
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, StaticTemplateDefinitionDto value, JsonSerializerOptions options)
        => TemplateLibraryJson.WriteDefinition(writer, value, options);
}
/// <summary>完整高级定义的严格读取，外层显式full分支选择此转换器。</summary>
public sealed class FullTemplateDefinitionJsonConverter : JsonConverter<FullTemplateDefinitionDto>
{
    /// <inheritdoc />
    public override FullTemplateDefinitionDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return TemplateLibraryJson.FullDefinition(document.RootElement, options);
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, FullTemplateDefinitionDto value, JsonSerializerOptions options)
        => TemplateLibraryJson.WriteDefinition(writer, value, options);
}
/// <summary>详情按显式definitionFormat读取完整联合；旧静态定义转换器保持严格。</summary>
public sealed class TemplateDetailResponseJsonConverter : JsonConverter<TemplateDetailResponse>
{
    /// <inheritdoc />
    public override TemplateDetailResponse Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader); var root = document.RootElement;
        List<string> keys = ["libraryId", "revision", "summary", "definitionAvailability", "definition", "protocolVersion"];
        if (root.TryGetProperty("definitionFormat", out _)) keys.Add("definitionFormat");
        if (root.TryGetProperty("packageClosureDetails", out _)) keys.Add("packageClosureDetails");
        var f = StyleCommandJson.Fields(root, [.. keys]);
        string? format = f.TryGetValue("definitionFormat", out var formatValue) ? StyleCommandJson.String(formatValue) : null;
        if (format is not null and not "full") throw new JsonException("未知模板定义格式。");
        StaticTemplateDefinitionDto? definition = f["definition"].ValueKind == JsonValueKind.Null ? null
            : format == "full" ? TemplateLibraryJson.FullDefinition(f["definition"], options) : TemplateLibraryJson.Definition(f["definition"]);
        return new(StyleCommandJson.String(f["libraryId"]), StyleCommandJson.String(f["revision"]),
            JsonSerializer.Deserialize<TemplateSummaryDto>(f["summary"], options)!, StyleCommandJson.String(f["definitionAvailability"]),
            definition, StyleCommandJson.String(f["protocolVersion"]), format,
            f.TryGetValue("packageClosureDetails", out var closure) ? JsonSerializer.Deserialize<EffectPackageInspectResponse[]>(closure, options) : null);
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TemplateDetailResponse value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); Put("libraryId", value.LibraryId); Put("revision", value.Revision); Put("summary", value.Summary);
        Put("definitionAvailability", value.DefinitionAvailability); Put("definition", value.Definition); Put("protocolVersion", value.ProtocolVersion);
        if (value.DefinitionFormat is not null) Put("definitionFormat", value.DefinitionFormat);
        if (value.PackageClosureDetails is not null) Put("packageClosureDetails", value.PackageClosureDetails);
        writer.WriteEndObject();
        void Put<T>(string name, T item) { writer.WritePropertyName(name); JsonSerializer.Serialize(writer, item, options); }
    }
}
/// <summary>配方严格字段及无损种子读取；参数语义仍交原包解析器。</summary>
public sealed class TemplateEffectRecipeJsonConverter : JsonConverter<TemplateEffectRecipeDto>
{
    /// <inheritdoc />
    public override TemplateEffectRecipeDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var f = StyleCommandJson.Fields(document.RootElement, "recipeId", "name", "target", "packageId", "revision", "exportId",
            "parametersJson", "overlayLayer", "enabled", "readScope", "branchInput", "fixedSeed", "configurationId");
        if (!TemplateLibraryJson.IsParameterObject(StyleCommandJson.String(f["parametersJson"]))) throw new JsonException("配方参数必须是对象。");
        string? seed = StyleCommandJson.NullableString(f["fixedSeed"]);
        if (seed is not null && (seed.Length == 0 || seed.Any(c => !char.IsAsciiDigit(c))
            || !ulong.TryParse(seed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)))
            throw new JsonException("fixedSeed必须是UInt64十进制字符串或null。");
        return new(StyleCommandJson.String(f["recipeId"]), StyleCommandJson.String(f["name"]), StyleCommandJson.Enum<TemplateEffectTargetDto>(f["target"]),
            StyleCommandJson.String(f["packageId"]), StyleCommandJson.String(f["revision"]), StyleCommandJson.String(f["exportId"]), StyleCommandJson.String(f["parametersJson"]),
            StyleCommandJson.Enum<TemplateEffectOverlayLayerDto>(f["overlayLayer"]), StyleCommandJson.Boolean(f["enabled"]),
            StyleCommandJson.Enum<TemplateScriptReadScopeDto>(f["readScope"]), StyleCommandJson.NullableString(f["branchInput"]), seed,
            StyleCommandJson.NullableString(f["configurationId"]));
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TemplateEffectRecipeDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        Put("recipeId", value.RecipeId); Put("name", value.Name); Put("target", value.Target); Put("packageId", value.PackageId);
        Put("revision", value.Revision); Put("exportId", value.ExportId); Put("parametersJson", value.ParametersJson); Put("overlayLayer", value.OverlayLayer);
        Put("enabled", value.Enabled); Put("readScope", value.ReadScope); PutNullable("branchInput", value.BranchInput); PutNullable("fixedSeed", value.FixedSeed);
        PutNullable("configurationId", value.ConfigurationId); writer.WriteEndObject();
        void Put<T>(string name, T item) { writer.WritePropertyName(name); JsonSerializer.Serialize(writer, item, options); }
        void PutNullable(string name, string? item) { writer.WritePropertyName(name); if (item is null) writer.WriteNullValue(); else writer.WriteStringValue(item); }
    }
}
/// <summary>模板库严格具名命令联合，不影响工程 commands。</summary>
public sealed class TemplateLibraryCommandJsonConverter : JsonConverter<TemplateLibraryCommandDto>
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeof(TemplateLibraryCommandDto).IsAssignableFrom(typeToConvert);
    /// <inheritdoc />
    public override TemplateLibraryCommandDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var result = TemplateLibraryJson.Command(document.RootElement, options);
        return typeToConvert.IsInstanceOfType(result) ? result : throw new JsonException("模板库命令派生类型不一致。");
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TemplateLibraryCommandDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("type", value.Type);
        switch (value)
        {
            case CreateTemplateCommandDto create:
                if (create.DefinitionFormat is not null) writer.WriteString("definitionFormat", create.DefinitionFormat);
                Put("definition", create.Definition); break;
            case ReplaceTemplateCommandDto replace:
                writer.WriteString("templateId", replace.TemplateId);
                if (replace.DefinitionFormat is not null) writer.WriteString("definitionFormat", replace.DefinitionFormat);
                Put("definition", replace.Definition); break;
            case CopyTemplateCommandDto copy:
                writer.WriteString("sourceLibraryId", copy.SourceLibraryId); writer.WriteString("sourceRevision", copy.SourceRevision);
                writer.WriteString("templateId", copy.TemplateId); writer.WriteString("name", copy.Name); break;
            case RenameTemplateCommandDto rename: writer.WriteString("templateId", rename.TemplateId); writer.WriteString("name", rename.Name); break;
            case DeleteTemplateCommandDto delete: writer.WriteString("templateId", delete.TemplateId); break;
            default: throw new JsonException("未知模板库命令 DTO。");
        }
        writer.WriteEndObject();
        void Put<T>(string name, T item) { writer.WritePropertyName(name); JsonSerializer.Serialize(writer, item, options); }
    }
}
/// <summary>严格模板库请求，metadata 和三字段均显式必填。</summary>
public sealed class TemplateLibraryCommandRequestJsonConverter : JsonConverter<TemplateLibraryCommandRequest>
{
    /// <inheritdoc />
    public override TemplateLibraryCommandRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var fields = StyleCommandJson.Fields(document.RootElement, "expectedRevision", "metadata", "command");
        var metadata = StyleCommandJson.Fields(fields["metadata"], "requestId", "hostInstanceId", "validUntilUtc");
        string revision = StyleCommandJson.String(fields["expectedRevision"]);
        string requestId = StyleCommandJson.String(metadata["requestId"]), host = StyleCommandJson.String(metadata["hostInstanceId"]);
        if (metadata["validUntilUtc"].ValueKind != JsonValueKind.String
            || !HasExplicitOffset(metadata["validUntilUtc"].GetString()!)
            || !metadata["validUntilUtc"].TryGetDateTimeOffset(out var deadline))
            throw new JsonException("validUntilUtc 必须是带时区时间字符串。");
        return new(revision, new(requestId, host, deadline), TemplateLibraryJson.Command(fields["command"], options));
    }
    private static bool HasExplicitOffset(string value) => value.EndsWith('Z')
        || (value.Length >= 6 && value[^6] is '+' or '-' && value[^3] == ':'
            && char.IsAsciiDigit(value[^5]) && char.IsAsciiDigit(value[^4])
            && char.IsAsciiDigit(value[^2]) && char.IsAsciiDigit(value[^1]));
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TemplateLibraryCommandRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("expectedRevision", value.ExpectedRevision);
        writer.WritePropertyName("metadata");
        if (value.Metadata is not { } metadata) writer.WriteNullValue();
        else
        {
            writer.WriteStartObject(); writer.WriteString("requestId", metadata.RequestId); writer.WriteString("hostInstanceId", metadata.HostInstanceId);
            writer.WritePropertyName("validUntilUtc"); JsonSerializer.Serialize(writer, metadata.ValidUntilUtc, options); writer.WriteEndObject();
        }
        writer.WritePropertyName("command"); JsonSerializer.Serialize(writer, value.Command, options); writer.WriteEndObject();
    }
}
/// <summary>固定普通调用者的可变数组并复用严格 wire 校验；宿主在序列化准入处映射异常。</summary>
public static class TemplateLibraryCommandCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>复制并验证完整请求；不检查库修订、存在性或产生副作用。</summary>
    /// <exception cref="JsonException">输入形状或类型非法。</exception>
    /// <exception cref="ArgumentException">CLR 非有限数不能表示为 JSON。</exception>
    public static TemplateLibraryCommandRequest Capture(TemplateLibraryCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using (var counter = new TemplateIntentStream()) JsonSerializer.Serialize(counter, request.Command, Json);
            // The command is now bounded; decoding creates detached copies of all nested arrays.
            return JsonSerializer.Deserialize<TemplateLibraryCommandRequest>(JsonSerializer.SerializeToUtf8Bytes(request, Json), Json)!;
        }
        catch (TemplateLibraryBudgetExceededException error)
        {
            // In-process callers retain the existing invalid-request contract;
            // the HTTP parser sees the original typed exception and maps the
            // same new-input limit to 413 before a receipt is accepted.
            throw new JsonException(error.Message, error);
        }
    }
}
internal static class TemplateLibraryJson
{
    internal static bool IsParameterObject(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.ValueKind == JsonValueKind.Object;
    }
    internal static void WriteDefinition(Utf8JsonWriter writer, StaticTemplateDefinitionDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("name", value.Name);
        Put("kind", value.Kind); Put("style", value.Style); Put("properties", value.Properties);
        if (value is FullTemplateDefinitionDto full) { Put("effectRecipes", full.EffectRecipes); Put("packages", full.Packages); }
        writer.WriteEndObject();
        void Put<T>(string name, T item) { writer.WritePropertyName(name); JsonSerializer.Serialize(writer, item, options); }
    }
    internal static FullTemplateDefinitionDto FullDefinition(JsonElement root, JsonSerializerOptions options)
    {
        var f = StyleCommandJson.Fields(root, "name", "kind", "style", "properties", "effectRecipes", "packages");
        if (f["properties"].ValueKind != JsonValueKind.Array || f["effectRecipes"].ValueKind != JsonValueKind.Array
            || f["packages"].ValueKind != JsonValueKind.Array) throw new JsonException("完整定义集合必须是数组。");
        if (f["effectRecipes"].EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Object)
            || f["packages"].EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Object))
            throw new JsonException("配方与包引用成员必须是对象。");
        TemplateProperty[] properties = [.. f["properties"].EnumerateArray().Select(StyleCommandJson.Enum<TemplateProperty>)];
        if (properties.Distinct().Count() != properties.Length) throw new JsonException("模板属性不能重复。");
        return new(StyleCommandJson.String(f["name"]), StyleCommandJson.Enum<TemplateKind>(f["kind"]),
            StyleCommandJson.CanonicalStyle(f["style"], fullTemplate: true), properties,
            JsonSerializer.Deserialize<TemplateEffectRecipeDto[]>(f["effectRecipes"], options)!,
            JsonSerializer.Deserialize<EffectPackageReferenceDto[]>(f["packages"], options)!);
    }
    internal static StaticTemplateDefinitionDto Definition(JsonElement root)
    {
        var f = StyleCommandJson.Fields(root, "name", "kind", "style", "properties");
        if (f["properties"].ValueKind != JsonValueKind.Array || f["properties"].GetArrayLength() is < 1 or > 26)
            throw new JsonException("静态模板 properties 必须包含1..26个模板属性。");
        TemplateProperty[] properties = [.. f["properties"].EnumerateArray().Select(StyleCommandJson.Enum<TemplateProperty>)];
        if (properties.Distinct().Count() != properties.Length) throw new JsonException("模板属性不能重复。");
        CanonicalStyleDto style = StyleCommandJson.CanonicalStyle(f["style"]);
        if (style.UnsungFill.Material is PackageStyleMaterialDto || style.SungFill.Material is PackageStyleMaterialDto || style.Stroke?.Material is PackageStyleMaterialDto)
            throw new JsonException("静态定义不接受包材质，包括未选字段。");
        return new(StyleCommandJson.String(f["name"]), StyleCommandJson.Enum<TemplateKind>(f["kind"]), style, properties);
    }
    internal static TemplateLibraryCommandDto Command(JsonElement root, JsonSerializerOptions options)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeValue)) throw new JsonException("缺少模板库 command.type。");
        string type = StyleCommandJson.String(typeValue);
        bool full = root.TryGetProperty("definitionFormat", out var format);
        if (full && (type is not ("create" or "replace") || StyleCommandJson.String(format) != "full"))
            throw new JsonException("definitionFormat仅接受full。");
        var fields = type switch
        {
            "create" => full ? StyleCommandJson.Fields(root, "type", "definitionFormat", "definition") : StyleCommandJson.Fields(root, "type", "definition"),
            "replace" => full ? StyleCommandJson.Fields(root, "type", "templateId", "definitionFormat", "definition") : StyleCommandJson.Fields(root, "type", "templateId", "definition"),
            "copy" => StyleCommandJson.Fields(root, "type", "sourceLibraryId", "sourceRevision", "templateId", "name"),
            "rename" => StyleCommandJson.Fields(root, "type", "templateId", "name"),
            "delete" => StyleCommandJson.Fields(root, "type", "templateId"),
            _ => throw new JsonException("未知模板库命令 type。"),
        };
        string Text(string key) => StyleCommandJson.String(fields[key]);
        if (type is "create" or "replace")
        {
            if (!full) CheckNewDefinition(fields["definition"]);
        }
        if (type is "copy" or "rename") CheckName(Text("name"));
        TemplateLibraryCommandDto result = type switch
        {
            "create" => new CreateTemplateCommandDto(full ? FullDefinition(fields["definition"], options) : Definition(fields["definition"]), full ? "full" : null),
            "replace" => new ReplaceTemplateCommandDto(Text("templateId"), full ? FullDefinition(fields["definition"], options) : Definition(fields["definition"]), full ? "full" : null),
            "copy" => new CopyTemplateCommandDto(Text("sourceLibraryId"), Text("sourceRevision"), Text("templateId"), Text("name")),
            "rename" => new RenameTemplateCommandDto(Text("templateId"), Text("name")),
            _ => new DeleteTemplateCommandDto(Text("templateId")),
        };
        if (result is CreateTemplateCommandDto create) CheckName(create.Definition.Name);
        if (result is ReplaceTemplateCommandDto replace) CheckName(replace.Definition.Name);
        using (var counter = new TemplateIntentStream()) JsonSerializer.Serialize(counter, result, options);
        return result;
    }
    private static void CheckName(string name)
    { if (name.Length > TemplateLibraryLimits.MaxNameLength) throw new TemplateLibraryBudgetExceededException(); }
    private static void CheckNewDefinition(JsonElement definition)
    {
        var d = StyleCommandJson.Fields(definition, "name", "kind", "style", "properties"); CheckName(StyleCommandJson.String(d["name"]));
        var s = StyleCommandJson.Fields(d["style"], "typography", "unsungFill", "sungFill", "stroke", "shadow", "verticalCenterRatio", "horizontalAlignment", "opacity", "scale", "glow");
        var t = StyleCommandJson.Fields(s["typography"], "fontFamilyName", "fallbackFamilyNames", "fontSizePx", "fontWeight", "isBold", "isItalic", "letterSpacingPx");
        if (t["fontFamilyName"].ValueKind == JsonValueKind.String && t["fontFamilyName"].GetString()!.Length > StyleEditingLimits.MaxFontLength) throw new TemplateLibraryBudgetExceededException();
        if (t["fallbackFamilyNames"].ValueKind == JsonValueKind.Array)
        {
            if (t["fallbackFamilyNames"].GetArrayLength() > StyleEditingLimits.MaxFallbacks) throw new TemplateLibraryBudgetExceededException();
            foreach (var name in t["fallbackFamilyNames"].EnumerateArray())
                if (name.ValueKind == JsonValueKind.String && name.GetString()!.Length > StyleEditingLimits.MaxFontLength) throw new TemplateLibraryBudgetExceededException();
        }
        CheckMaterial(StyleCommandJson.Fields(s["unsungFill"], "material")["material"]);
        CheckMaterial(StyleCommandJson.Fields(s["sungFill"], "material")["material"]);
        if (s["stroke"].ValueKind != JsonValueKind.Null) CheckMaterial(StyleCommandJson.Fields(s["stroke"], "material", "widthPx", "join", "position")["material"]);
    }
    private static void CheckMaterial(JsonElement material)
    {
        if (material.ValueKind == JsonValueKind.Object && material.TryGetProperty("gradient", out var gradient) && gradient.ValueKind == JsonValueKind.Object
            && gradient.TryGetProperty("stops", out var stops) && stops.ValueKind == JsonValueKind.Array && stops.GetArrayLength() > StyleEditingLimits.MaxStops)
            throw new TemplateLibraryBudgetExceededException();
    }
}
internal sealed class TemplateIntentStream : Stream
{
    private long _length;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > TemplateLibraryLimits.MaxIntentBytes - _length) throw new TemplateLibraryBudgetExceededException();
        _length += buffer.Length;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
