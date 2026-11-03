using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>新样式意图或新增值超过资源预算，区别于 JSON 形状错误。</summary>
public sealed class StyleCommandBudgetExceededException : JsonException
{
    /// <summary>创建资源预算错误。</summary>
    public StyleCommandBudgetExceededException() : base("样式命令超过新增输入预算。") { }
}
/// <summary>严格稳定样式目标联合。</summary>
public sealed class StyleTargetJsonConverter : JsonConverter<StyleTargetDto>
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeof(StyleTargetDto).IsAssignableFrom(typeToConvert);
    /// <inheritdoc />
    public override StyleTargetDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var result = StyleCommandJson.Target(document.RootElement);
        return typeToConvert.IsInstanceOfType(result) ? result : throw new JsonException("目标派生类型不一致。");
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, StyleTargetDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("kind", value.Kind);
        switch (value)
        {
            case TokenStyleTargetDto token: writer.WriteString("branchId", token.BranchId); writer.WriteString("lineId", token.LineId); writer.WriteString("tokenId", token.TokenId); break;
            case LineStyleTargetDto line: writer.WriteString("branchId", line.BranchId); writer.WriteString("lineId", line.LineId); break;
            case BranchStyleTargetDto branch: writer.WriteString("branchId", branch.BranchId); break;
            case ProjectStyleTargetDto: break;
            default: throw new JsonException("未知样式目标类型。");
        }
        writer.WriteEndObject();
    }
}
/// <summary>canonical 材质读写；命令另外禁止只读包描述。</summary>
public sealed class StyleMaterialJsonConverter : JsonConverter<StyleMaterialDto>
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeof(StyleMaterialDto).IsAssignableFrom(typeToConvert);
    /// <inheritdoc />
    public override StyleMaterialDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader); var result = StyleCommandJson.Material(document.RootElement);
        return typeToConvert.IsInstanceOfType(result) ? result : throw new JsonException("材质派生类型不一致。");
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, StyleMaterialDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("kind", value.Kind);
        switch (value)
        {
            case SolidStyleMaterialDto solid: Write("color", solid.Color); break;
            case GradientStyleMaterialDto gradient: Write("gradient", gradient.Gradient); break;
            case PackageStyleMaterialDto package:
                writer.WriteString("packageId", package.PackageId); writer.WriteString("revision", package.Revision); writer.WriteString("exportId", package.ExportId);
                if (package.ParametersJson is { } parameters) Write("parametersJson", parameters);
                break;
            default: throw new JsonException("未知样式材质类型。");
        }
        writer.WriteEndObject();
        void Write<T>(string key, T item) { writer.WritePropertyName(key); JsonSerializer.Serialize(writer, item, options); }
    }
}
/// <summary>严格 property/value 联合；null value 始终写出。</summary>
public sealed class StyleChangeJsonConverter : JsonConverter<StyleChangeDto>
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeof(StyleChangeDto).IsAssignableFrom(typeToConvert);
    /// <inheritdoc />
    public override StyleChangeDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader); var result = StyleCommandJson.Change(document.RootElement);
        return typeToConvert.IsInstanceOfType(result) ? result : throw new JsonException("属性派生类型不一致。");
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, StyleChangeDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("property", value.Property); writer.WritePropertyName("value");
        switch (value)
        {
            case StyleFontFamilyChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleFallbackFamiliesChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleFontSizeChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleFontWeightChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleBoldChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleItalicChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleLetterSpacingChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungMaterialChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungMaterialChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleStrokeChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleStrokeMaterialChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleStrokeWidthChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleStrokeJoinChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleStrokePositionChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleShadowChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleShadowColorChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleShadowOpacityChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleShadowAngleChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleShadowDistanceChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleShadowBlurChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleGlowChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleGlowColorChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleGlowOpacityChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleGlowBlurChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleGlowSpreadChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleVerticalCenterChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleAlignmentChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleOpacityChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleScaleChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            default: throw new JsonException("未知样式属性类型。");
        }
        writer.WriteEndObject();
    }
}

internal static class StyleCommandJson
{
    internal static readonly string[] Properties =
    [
        "typography.fontFamilyName",
        "typography.fallbackFamilyNames",
        "typography.fontSizePx",
        "typography.fontWeight",
        "typography.isBold",
        "typography.isItalic",
        "typography.letterSpacingPx",
        "unsungFill.material",
        "sungFill.material",
        "stroke",
        "stroke.material",
        "stroke.widthPx",
        "stroke.join",
        "stroke.position",
        "shadow",
        "shadow.color",
        "shadow.opacity",
        "shadow.angleDegrees",
        "shadow.distancePx",
        "shadow.blurRadiusPx",
        "verticalCenterRatio",
        "horizontalAlignment",
        "opacity",
        "scale",
        "glow", "glow.color", "glow.opacity", "glow.blurRadiusPx", "glow.spreadPx",
    ];
    internal static Dictionary<string, JsonElement> Fields(JsonElement root, params string[] keys)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("样式成员必须是对象。");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (!keys.Contains(field.Name, StringComparer.Ordinal) || !result.TryAdd(field.Name, field.Value))
                throw new JsonException("样式对象包含未知或重复字段。");
        if (result.Count != keys.Length) throw new JsonException("样式对象缺少必填字段。");
        return result;
    }
    internal static StyleTargetDto Target(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var value)) throw new JsonException("缺少目标 kind。");
        string kind = String(value);
        var fields = kind switch
        {
            "project" => Fields(root, "kind"),
            "branch" => Fields(root, "kind", "branchId"),
            "line" => Fields(root, "kind", "branchId", "lineId"),
            "token" => Fields(root, "kind", "branchId", "lineId", "tokenId"),
            _ => throw new JsonException("未知样式目标 kind。"),
        };
        return kind switch
        {
            "project" => new ProjectStyleTargetDto(),
            "branch" => new BranchStyleTargetDto(String(fields["branchId"])),
            "line" => new LineStyleTargetDto(String(fields["branchId"]), String(fields["lineId"])),
            _ => new TokenStyleTargetDto(String(fields["branchId"]), String(fields["lineId"]), String(fields["tokenId"])),
        };
    }
    internal static CanonicalStyleDto CanonicalStyle(JsonElement root, bool fullTemplate = false)
    {
        var f = Fields(root, "typography", "unsungFill", "sungFill", "stroke", "shadow", "verticalCenterRatio", "horizontalAlignment", "opacity", "scale", "glow");
        var t = Fields(f["typography"], "fontFamilyName", "fallbackFamilyNames", "fontSizePx", "fontWeight", "isBold", "isItalic", "letterSpacingPx");
        var unsung = Fields(f["unsungFill"], "material"); var sung = Fields(f["sungFill"], "material");
        return new(new(NullableString(t["fontFamilyName"]), Strings(t["fallbackFamilyNames"]), Float(t["fontSizePx"]), Integer(t["fontWeight"]),
                Boolean(t["isBold"]), Boolean(t["isItalic"]), Float(t["letterSpacingPx"])),
            new(Material(unsung["material"], fullTemplate)), new(Material(sung["material"], fullTemplate)), f["stroke"].ValueKind == JsonValueKind.Null ? null : Stroke(f["stroke"], fullTemplate),
            f["shadow"].ValueKind == JsonValueKind.Null ? null : Shadow(f["shadow"]), Number(f["verticalCenterRatio"]),
            Enum<StyleHorizontalAlignment>(f["horizontalAlignment"]), Float(f["opacity"]), Float(f["scale"]), f["glow"].ValueKind == JsonValueKind.Null ? null : Glow(f["glow"]));
    }

    internal static StyleChangeDto Change(JsonElement root)
    {
        var fields = Fields(root, "property", "value"); string property = String(fields["property"]); var v = fields["value"];
        return property switch
        {
            "typography.fontFamilyName" => new StyleFontFamilyChangeDto(NullableString(v)),
            "typography.fallbackFamilyNames" => new StyleFallbackFamiliesChangeDto(Strings(v)),
            "typography.fontSizePx" => new StyleFontSizeChangeDto(Float(v)),
            "typography.fontWeight" => new StyleFontWeightChangeDto(Integer(v)),
            "typography.isBold" => new StyleBoldChangeDto(Boolean(v)),
            "typography.isItalic" => new StyleItalicChangeDto(Boolean(v)),
            "typography.letterSpacingPx" => new StyleLetterSpacingChangeDto(Float(v)),
            "unsungFill.material" => new StyleUnsungMaterialChangeDto(Material(v)),
            "sungFill.material" => new StyleSungMaterialChangeDto(Material(v)),
            "stroke" => new StyleStrokeChangeDto(v.ValueKind == JsonValueKind.Null ? null : Stroke(v)),
            "stroke.material" => new StyleStrokeMaterialChangeDto(Material(v)),
            "stroke.widthPx" => new StyleStrokeWidthChangeDto(Float(v)),
            "stroke.join" => new StyleStrokeJoinChangeDto(Enum<StyleStrokeJoin>(v)),
            "stroke.position" => new StyleStrokePositionChangeDto(Enum<StyleStrokePosition>(v)),
            "shadow" => new StyleShadowChangeDto(v.ValueKind == JsonValueKind.Null ? null : Shadow(v)),
            "shadow.color" => new StyleShadowColorChangeDto(Color(v)),
            "shadow.opacity" => new StyleShadowOpacityChangeDto(Float(v)),
            "shadow.angleDegrees" => new StyleShadowAngleChangeDto(Number(v)),
            "shadow.distancePx" => new StyleShadowDistanceChangeDto(Float(v)),
            "shadow.blurRadiusPx" => new StyleShadowBlurChangeDto(Float(v)),
            "glow" => new StyleGlowChangeDto(v.ValueKind == JsonValueKind.Null ? null : Glow(v)),
            "glow.color" => new StyleGlowColorChangeDto(Color(v)),
            "glow.opacity" => new StyleGlowOpacityChangeDto(Float(v)),
            "glow.blurRadiusPx" => new StyleGlowBlurChangeDto(Float(v)),
            "glow.spreadPx" => new StyleGlowSpreadChangeDto(Float(v)),
            "verticalCenterRatio" => new StyleVerticalCenterChangeDto(Number(v)),
            "horizontalAlignment" => new StyleAlignmentChangeDto(Enum<StyleHorizontalAlignment>(v)),
            "opacity" => new StyleOpacityChangeDto(Float(v)),
            "scale" => new StyleScaleChangeDto(Float(v)),
            _ => throw new JsonException("未知样式属性。"),
        };
    }
    internal static StyleMaterialDto Material(JsonElement root, bool fullTemplate = false)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var k)) throw new JsonException("材质必须提供 kind。");
        switch (String(k))
        {
            case "solid": var solid = Fields(root, "kind", "color"); return new SolidStyleMaterialDto(Color(solid["color"]));
            case "gradient": var gradient = Fields(root, "kind", "gradient"); return new GradientStyleMaterialDto(Gradient(gradient["gradient"]));
            case "package":
                var package = fullTemplate ? Fields(root, "kind", "packageId", "revision", "exportId", "parametersJson")
                    : Fields(root, "kind", "packageId", "revision", "exportId");
                if (fullTemplate && !TemplateLibraryJson.IsParameterObject(String(package["parametersJson"]))) throw new JsonException("包材质参数必须是对象。");
                return new PackageStyleMaterialDto(String(package["packageId"]), String(package["revision"]), String(package["exportId"]),
                    fullTemplate ? String(package["parametersJson"]) : null);
            default: throw new JsonException("未知材质 kind。");
        }
    }
    internal static StyleColorDto Color(JsonElement root)
    {
        var f = Fields(root, "red", "green", "blue", "alpha");
        return new(Byte(f["red"]), Byte(f["green"]), Byte(f["blue"]), Byte(f["alpha"]));
    }
    internal static StyleGradientDto Gradient(JsonElement root)
    {
        var f = Fields(root, "kind", "angleDegrees", "centerX", "centerY", "radiusX", "radiusY", "coordinateSpace", "colorSpace", "huePath", "stops");
        if (f["stops"].ValueKind != JsonValueKind.Array) throw new JsonException("stops 必须是数组。");
        return new(Enum<StyleGradientKind>(f["kind"]), Number(f["angleDegrees"]), Number(f["centerX"]), Number(f["centerY"]), Number(f["radiusX"]), Number(f["radiusY"]),
            Enum<StyleGradientCoordinateSpace>(f["coordinateSpace"]), Enum<StyleGradientColorSpace>(f["colorSpace"]), Enum<StyleGradientHuePath>(f["huePath"]),
            [.. f["stops"].EnumerateArray().Select(item => { var stop = Fields(item, "offset", "color"); return new StyleGradientStopDto(Number(stop["offset"]), Color(stop["color"])); })]);
    }
    internal static StyleStrokeDto Stroke(JsonElement root, bool fullTemplate = false)
    {
        var f = Fields(root, "material", "widthPx", "join", "position"); return new(Material(f["material"], fullTemplate), Float(f["widthPx"]), Enum<StyleStrokeJoin>(f["join"]), Enum<StyleStrokePosition>(f["position"]));
    }
    internal static StyleShadowDto Shadow(JsonElement root)
    {
        var f = Fields(root, "color", "opacity", "angleDegrees", "distancePx", "blurRadiusPx"); return new(Color(f["color"]), Float(f["opacity"]), Number(f["angleDegrees"]), Float(f["distancePx"]), Float(f["blurRadiusPx"]));
    }
    internal static StyleGlowDto Glow(JsonElement root)
    {
        var f = Fields(root, "color", "opacity", "blurRadiusPx", "spreadPx");
        return new(Color(f["color"]), Float(f["opacity"]), Float(f["blurRadiusPx"]), Float(f["spreadPx"]));
    }
    internal static string String(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new JsonException("必须是字符串。");
    internal static string? NullableString(JsonElement v) => v.ValueKind == JsonValueKind.Null ? null : String(v);
    internal static string[] Strings(JsonElement v) => v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(String)] : throw new JsonException("必须是字符串数组。");
    internal static bool Boolean(JsonElement v) => v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw new JsonException("必须是 bool。");
    internal static int Integer(JsonElement v) => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : throw new JsonException("必须是 JSON int32。");
    private static byte Byte(JsonElement v) => v.ValueKind == JsonValueKind.Number && v.TryGetByte(out byte n) ? n : throw new JsonException("必须是 JSON byte。");
    internal static float Float(JsonElement v) => v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out float n) && float.IsFinite(n) ? n : throw new JsonException("必须是有限 JSON float。");
    internal static double Number(JsonElement v) => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double n) && double.IsFinite(n) ? n : throw new JsonException("必须是有限 JSON number。");
    internal static T Enum<T>(JsonElement v) where T : struct, Enum
    {
        string text = String(v);
        foreach (T item in System.Enum.GetValues<T>()) if (JsonNamingPolicy.CamelCase.ConvertName(item.ToString()) == text) return item;
        throw new JsonException("枚举名称必须准确匹配。");
    }
    internal static ProjectCommandDto Read(JsonElement root, string type, JsonSerializerOptions options)
    {
        bool patch = type == "styles.patch"; var f = Fields(root, "type", "targets", patch ? "changes" : "properties");
        if (f["targets"].ValueKind != JsonValueKind.Array || f["targets"].GetArrayLength() is < 1 or > StyleEditingLimits.MaxTargets) throw new JsonException("targets 必须是 1..1000 数组。");
        var targets = f["targets"].EnumerateArray().Select(Target).ToArray();
        if (targets.Select(t => t.Kind).Distinct().Count() != 1 || targets.Distinct().Count() != targets.Length || targets[0] is ProjectStyleTargetDto && targets.Length != 1) throw new JsonException("目标必须同种且唯一。");
        ProjectCommandDto result;
        string[] names;
        if (patch)
        {
            if (f["changes"].ValueKind != JsonValueKind.Array || f["changes"].GetArrayLength() is < 1 or > 29) throw new JsonException("changes 必须是非空闭合属性数组。");
            // Resource checks precede materialization of new arrays and names; old GET values have no such input limits.
            foreach (var item in f["changes"].EnumerateArray()) CheckNewValue(item);
            var changes = f["changes"].EnumerateArray().Select(Change).ToArray(); names = changes.Select(c => c.Property).ToArray();
            result = new PatchStylesCommandDto(targets, changes);
        }
        else
        {
            names = Strings(f["properties"]);
            if (names.Length is < 1 or > 29 || names.Any(n => !Properties.Contains(n, StringComparer.Ordinal))) throw new JsonException("properties 必须包含已知属性。");
            result = new RestoreStylesCommandDto(targets, names);
        }
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(a => names.Any(b => b.StartsWith(a + ".", StringComparison.Ordinal)))) throw new JsonException("属性重复或父子冲突。");
        using (var counter = new StyleIntentCountingStream()) JsonSerializer.Serialize(counter, result, options);
        return result;
    }
    private static void CheckNewValue(JsonElement change)
    {
        var f = Fields(change, "property", "value"); string property = String(f["property"]); var value = f["value"];
        if (property == "typography.fontFamilyName" && value.ValueKind == JsonValueKind.String && value.GetString()!.Length > StyleEditingLimits.MaxFontLength) throw new StyleCommandBudgetExceededException();
        if (property == "typography.fallbackFamilyNames" && value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() > StyleEditingLimits.MaxFallbacks) throw new StyleCommandBudgetExceededException();
            foreach (var name in value.EnumerateArray()) if (name.ValueKind == JsonValueKind.String && name.GetString()!.Length > StyleEditingLimits.MaxFontLength) throw new StyleCommandBudgetExceededException();
        }
        if (property is "unsungFill.material" or "sungFill.material" or "stroke.material") MaterialInput(value);
        if (property == "stroke" && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("material", out var material)) MaterialInput(material);
    }
    private static void MaterialInput(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("kind", out var kind)) return;
        if (kind.ValueKind == JsonValueKind.String && kind.GetString() == "package") throw new JsonException("包材质描述仅供读取。");
        if (value.TryGetProperty("gradient", out var gradient) && gradient.ValueKind == JsonValueKind.Object && gradient.TryGetProperty("stops", out var stops) && stops.ValueKind == JsonValueKind.Array && stops.GetArrayLength() > StyleEditingLimits.MaxStops) throw new StyleCommandBudgetExceededException();
    }
    internal static void Write(Utf8JsonWriter writer, ProjectCommandDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteString("type", value.Type);
        switch (value)
        {
            case PatchStylesCommandDto patch: Put("targets", patch.Targets); Put("changes", patch.Changes); break;
            case RestoreStylesCommandDto restore: Put("targets", restore.Targets); Put("properties", restore.Properties); break;
            default: throw new JsonException("未知样式命令。");
        }
        // Invalid inherited slots remain fingerprinted, while HTTP never admits them.
        if (value.Target is not null) Put("target", value.Target);
        if (value.StartUs is not null) Put("startUs", value.StartUs);
        if (value.EndUs is not null) Put("endUs", value.EndUs);
        if (value.AnalysisJobId is not null) Put("analysisJobId", value.AnalysisJobId);
        writer.WriteEndObject();
        void Put<T>(string key, T v) { writer.WritePropertyName(key); JsonSerializer.Serialize(writer, v, options); }
    }
}

/// <summary>样式枚举采用准确 camelCase 命名，拒绝整数和大小写变体。</summary>
public sealed class StyleEnumJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    /// <inheritdoc />
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            foreach (T value in Enum.GetValues<T>())
                if (JsonNamingPolicy.CamelCase.ConvertName(value.ToString()) == reader.GetString()) return value;
        throw new JsonException("样式枚举名称必须准确匹配。");
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}

internal sealed class StyleIntentCountingStream : Stream
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
        if (buffer.Length > StyleEditingLimits.MaxIntentBytes - _length) throw new StyleCommandBudgetExceededException();
        _length += buffer.Length;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
