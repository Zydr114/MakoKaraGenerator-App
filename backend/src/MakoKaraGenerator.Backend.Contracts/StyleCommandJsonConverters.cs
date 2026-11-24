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
            case StyleRubyRelativeScaleChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleRubyGapChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungFillChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungFillChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungStrokesChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungStrokesChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungShadowChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungShadowColorChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungShadowGradientChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungShadowOpacityChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungShadowAngleChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungShadowDistanceChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungShadowBlurChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungShadowSpreadChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungShadowChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungShadowColorChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungShadowGradientChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungShadowOpacityChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungShadowAngleChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungShadowDistanceChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungShadowBlurChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungShadowSpreadChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungGlowChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungGlowColorChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungGlowOpacityChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungGlowBlurChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleUnsungGlowSpreadChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungGlowChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungGlowColorChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungGlowOpacityChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungGlowBlurChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleSungGlowSpreadChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleWipeFeatherChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleWipeHighlightChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleWipeHighlightColorChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleWipeHighlightWidthChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleWipeHighlightOpacityChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleWipeHighlightBlendChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleVerticalCenterChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleAlignmentChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleOpacityChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            case StyleScaleChangeDto item: JsonSerializer.Serialize(writer, item.Value, options); break;
            default: throw new JsonException("未知样式属性类型。");
        }
        writer.WriteEndObject();
    }
}

/// <summary>已唱描边的跟随／显式联合。</summary>
public sealed class StyleSungStrokesJsonConverter : JsonConverter<StyleSungStrokesDto>
{
    /// <inheritdoc />
    public override StyleSungStrokesDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader); return StyleCommandJson.SungStrokes(document.RootElement);
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, StyleSungStrokesDto value, JsonSerializerOptions options)
    {
        if (value.Kind == StyleFollowKind.Set && value.Value is null) throw new JsonException("显式已唱描边必须是数组。");
        StyleCommandJson.WriteFollowable(writer, value.Kind, value.Value, options);
    }
}
/// <summary>已唱阴影的跟随／显式联合。</summary>
public sealed class StyleSungShadowJsonConverter : JsonConverter<StyleSungShadowDto>
{
    /// <inheritdoc />
    public override StyleSungShadowDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader); return StyleCommandJson.SungShadow(document.RootElement);
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, StyleSungShadowDto value, JsonSerializerOptions options)
        => StyleCommandJson.WriteFollowable(writer, value.Kind, value.Value, options);
}
/// <summary>已唱辉光的跟随／显式联合。</summary>
public sealed class StyleSungGlowJsonConverter : JsonConverter<StyleSungGlowDto>
{
    /// <inheritdoc />
    public override StyleSungGlowDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader); return StyleCommandJson.SungGlow(document.RootElement);
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, StyleSungGlowDto value, JsonSerializerOptions options)
        => StyleCommandJson.WriteFollowable(writer, value.Kind, value.Value, options);
}

internal static class StyleCommandJson
{
    internal static void WriteFollowable<T>(Utf8JsonWriter writer, StyleFollowKind kind, T? value, JsonSerializerOptions options) where T : class
    {
        if (!System.Enum.IsDefined(kind) || kind == StyleFollowKind.Follow && value is not null) throw new JsonException("跟随项不能携带 value。");
        writer.WriteStartObject(); writer.WriteString("kind", JsonNamingPolicy.CamelCase.ConvertName(kind.ToString()));
        if (kind == StyleFollowKind.Set) { writer.WritePropertyName("value"); JsonSerializer.Serialize(writer, value, options); }
        writer.WriteEndObject();
    }
    internal static readonly string[] Properties =
    [
        "typography.fontFamilyName",
        "typography.fallbackFamilyNames",
        "typography.fontSizePx",
        "typography.fontWeight",
        "typography.isBold",
        "typography.isItalic",
        "typography.letterSpacingPx",
        "ruby.relativeScale",
        "ruby.gapPx",
        "unsung.fill",
        "sung.fill",
        "unsung.strokes",
        "sung.strokes",
        "unsung.shadow",
        "unsung.shadow.color",
        "unsung.shadow.gradient",
        "unsung.shadow.opacity",
        "unsung.shadow.angleDegrees",
        "unsung.shadow.distancePx",
        "unsung.shadow.blurRadiusPx",
        "unsung.shadow.spreadPx",
        "sung.shadow",
        "sung.shadow.color",
        "sung.shadow.gradient",
        "sung.shadow.opacity",
        "sung.shadow.angleDegrees",
        "sung.shadow.distancePx",
        "sung.shadow.blurRadiusPx",
        "sung.shadow.spreadPx",
        "unsung.glow",
        "unsung.glow.color",
        "unsung.glow.opacity",
        "unsung.glow.blurRadiusPx",
        "unsung.glow.spreadPx",
        "sung.glow",
        "sung.glow.color",
        "sung.glow.opacity",
        "sung.glow.blurRadiusPx",
        "sung.glow.spreadPx",
        "wipe.featherRatio",
        "wipe.highlight",
        "wipe.highlight.color",
        "wipe.highlight.widthRatio",
        "wipe.highlight.opacity",
        "wipe.highlight.blend",
        "verticalCenterRatio",
        "horizontalAlignment",
        "opacity",
        "scale",
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
        var f = Fields(root, "typography", "unsung", "sung", "wipe", "verticalCenterRatio", "horizontalAlignment", "opacity", "scale", "ruby");
        return new(Typography(f["typography"]), Appearance(f["unsung"], fullTemplate), SungAppearance(f["sung"], fullTemplate), Wipe(f["wipe"]),
            Number(f["verticalCenterRatio"]), Enum<StyleHorizontalAlignment>(f["horizontalAlignment"]), Float(f["opacity"]), Float(f["scale"]), Ruby(f["ruby"]));
    }
    internal static StyleRubyDto Ruby(JsonElement root)
    {
        var r = Fields(root, "relativeScale", "gapPx");
        return new(NullableNumber(r["relativeScale"]), NullableNumber(r["gapPx"]));
    }
    private static double? NullableNumber(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Number(value);
    internal static StyleTypographyDto Typography(JsonElement root)
    {
        var t = Fields(root, "fontFamilyName", "fallbackFamilyNames", "fontSizePx", "fontWeight", "isBold", "isItalic", "letterSpacingPx");
        return new(NullableString(t["fontFamilyName"]), Strings(t["fallbackFamilyNames"]), Float(t["fontSizePx"]), Integer(t["fontWeight"]),
            Boolean(t["isBold"]), Boolean(t["isItalic"]), Float(t["letterSpacingPx"]));
    }
    internal static StyleAppearanceDto Appearance(JsonElement root, bool fullTemplate = false)
    {
        var f = Fields(root, "fill", "strokes", "shadow", "glow");
        return new(Material(f["fill"], fullTemplate), Strokes(f["strokes"], fullTemplate), NullOr(f["shadow"], Shadow), NullOr(f["glow"], Glow));
    }
    internal static StyleSungAppearanceDto SungAppearance(JsonElement root, bool fullTemplate = false)
    {
        var f = Fields(root, "fill", "strokes", "shadow", "glow");
        return new(Material(f["fill"], fullTemplate), SungStrokes(f["strokes"], fullTemplate), SungShadow(f["shadow"]), SungGlow(f["glow"]));
    }
    internal static StyleWipeDto Wipe(JsonElement root)
    {
        var f = Fields(root, "featherRatio", "highlight"); return new(Number(f["featherRatio"]), NullOr(f["highlight"], Highlight));
    }
    internal static StyleWipeHighlightDto Highlight(JsonElement root)
    {
        var f = Fields(root, "color", "widthRatio", "opacity", "blend");
        return new(Color(f["color"]), Number(f["widthRatio"]), Float(f["opacity"]), Enum<StyleWipeHighlightBlend>(f["blend"]));
    }
    internal static T? NullOr<T>(JsonElement value, Func<JsonElement, T> read) where T : class => value.ValueKind == JsonValueKind.Null ? null : read(value);
    // 跟随只写 kind；显式取值必须同时写 value（阴影、辉光的 value 可为 null，描边为数组）。
    private static (StyleFollowKind Kind, JsonElement Value) Followable(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind)) throw new JsonException("已唱跟随项必须提供 kind。");
        StyleFollowKind value = Enum<StyleFollowKind>(kind);
        if (value == StyleFollowKind.Follow) { Fields(root, "kind"); return (value, default); }
        return (value, Fields(root, "kind", "value")["value"]);
    }
    internal static StyleSungStrokesDto SungStrokes(JsonElement root, bool fullTemplate = false)
    {
        var (kind, value) = Followable(root); return kind == StyleFollowKind.Follow ? new(kind) : new(kind, Strokes(value, fullTemplate));
    }
    internal static StyleSungShadowDto SungShadow(JsonElement root)
    {
        var (kind, value) = Followable(root); return kind == StyleFollowKind.Follow ? new(kind) : new(kind, NullOr(value, Shadow));
    }
    internal static StyleSungGlowDto SungGlow(JsonElement root)
    {
        var (kind, value) = Followable(root); return kind == StyleFollowKind.Follow ? new(kind) : new(kind, NullOr(value, Glow));
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
            "ruby.relativeScale" => new StyleRubyRelativeScaleChangeDto(NullableNumber(v)),
            "ruby.gapPx" => new StyleRubyGapChangeDto(NullableNumber(v)),
            "unsung.fill" => new StyleUnsungFillChangeDto(Material(v)),
            "sung.fill" => new StyleSungFillChangeDto(Material(v)),
            "unsung.strokes" => new StyleUnsungStrokesChangeDto(Strokes(v)),
            "sung.strokes" => new StyleSungStrokesChangeDto(SungStrokes(v)),
            "unsung.shadow" => new StyleUnsungShadowChangeDto(NullOr(v, Shadow)),
            "unsung.shadow.color" => new StyleUnsungShadowColorChangeDto(Color(v)),
            "unsung.shadow.gradient" => new StyleUnsungShadowGradientChangeDto(NullOr(v, Gradient)),
            "unsung.shadow.opacity" => new StyleUnsungShadowOpacityChangeDto(Float(v)),
            "unsung.shadow.angleDegrees" => new StyleUnsungShadowAngleChangeDto(Number(v)),
            "unsung.shadow.distancePx" => new StyleUnsungShadowDistanceChangeDto(Float(v)),
            "unsung.shadow.blurRadiusPx" => new StyleUnsungShadowBlurChangeDto(Float(v)),
            "unsung.shadow.spreadPx" => new StyleUnsungShadowSpreadChangeDto(Float(v)),
            "sung.shadow" => new StyleSungShadowChangeDto(SungShadow(v)),
            "sung.shadow.color" => new StyleSungShadowColorChangeDto(Color(v)),
            "sung.shadow.gradient" => new StyleSungShadowGradientChangeDto(NullOr(v, Gradient)),
            "sung.shadow.opacity" => new StyleSungShadowOpacityChangeDto(Float(v)),
            "sung.shadow.angleDegrees" => new StyleSungShadowAngleChangeDto(Number(v)),
            "sung.shadow.distancePx" => new StyleSungShadowDistanceChangeDto(Float(v)),
            "sung.shadow.blurRadiusPx" => new StyleSungShadowBlurChangeDto(Float(v)),
            "sung.shadow.spreadPx" => new StyleSungShadowSpreadChangeDto(Float(v)),
            "unsung.glow" => new StyleUnsungGlowChangeDto(NullOr(v, Glow)),
            "unsung.glow.color" => new StyleUnsungGlowColorChangeDto(Color(v)),
            "unsung.glow.opacity" => new StyleUnsungGlowOpacityChangeDto(Float(v)),
            "unsung.glow.blurRadiusPx" => new StyleUnsungGlowBlurChangeDto(Float(v)),
            "unsung.glow.spreadPx" => new StyleUnsungGlowSpreadChangeDto(Float(v)),
            "sung.glow" => new StyleSungGlowChangeDto(SungGlow(v)),
            "sung.glow.color" => new StyleSungGlowColorChangeDto(Color(v)),
            "sung.glow.opacity" => new StyleSungGlowOpacityChangeDto(Float(v)),
            "sung.glow.blurRadiusPx" => new StyleSungGlowBlurChangeDto(Float(v)),
            "sung.glow.spreadPx" => new StyleSungGlowSpreadChangeDto(Float(v)),
            "wipe.featherRatio" => new StyleWipeFeatherChangeDto(Number(v)),
            "wipe.highlight" => new StyleWipeHighlightChangeDto(NullOr(v, Highlight)),
            "wipe.highlight.color" => new StyleWipeHighlightColorChangeDto(Color(v)),
            "wipe.highlight.widthRatio" => new StyleWipeHighlightWidthChangeDto(Number(v)),
            "wipe.highlight.opacity" => new StyleWipeHighlightOpacityChangeDto(Float(v)),
            "wipe.highlight.blend" => new StyleWipeHighlightBlendChangeDto(Enum<StyleWipeHighlightBlend>(v)),
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
        var f = Fields(root, "material", "widthPx", "join"); return new(Material(f["material"], fullTemplate), Float(f["widthPx"]), Enum<StyleStrokeJoin>(f["join"]));
    }
    internal static StyleStrokeDto[] Strokes(JsonElement root, bool fullTemplate = false)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > StyleEditingLimits.MaxStrokes) throw new JsonException("描边必须是至多4层的数组。");
        return [.. root.EnumerateArray().Select(item => Stroke(item, fullTemplate))];
    }
    internal static StyleShadowDto Shadow(JsonElement root)
    {
        var f = Fields(root, "color", "gradient", "opacity", "angleDegrees", "distancePx", "blurRadiusPx", "spreadPx");
        return new(Color(f["color"]), Float(f["opacity"]), Number(f["angleDegrees"]), Float(f["distancePx"]), Float(f["blurRadiusPx"]),
            Float(f["spreadPx"]), NullOr(f["gradient"], Gradient));
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
            if (f["changes"].ValueKind != JsonValueKind.Array || f["changes"].GetArrayLength() is < 1 or > StyleEditingLimits.PropertyCount) throw new JsonException("changes 必须是非空闭合属性数组。");
            // Resource checks precede materialization of new arrays and names; old GET values have no such input limits.
            foreach (var item in f["changes"].EnumerateArray()) CheckNewValue(item);
            var changes = f["changes"].EnumerateArray().Select(Change).ToArray(); names = changes.Select(c => c.Property).ToArray();
            result = new PatchStylesCommandDto(targets, changes);
        }
        else
        {
            names = Strings(f["properties"]);
            if (names.Length is < 1 or > StyleEditingLimits.PropertyCount || names.Any(n => !Properties.Contains(n, StringComparer.Ordinal))) throw new JsonException("properties 必须包含已知属性。");
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
        if (property is "unsung.fill" or "sung.fill") MaterialInput(value);
        if (property == "unsung.strokes") StrokesInput(value);
        if (property == "sung.strokes" && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var strokes)) StrokesInput(strokes);
        if (property == "unsung.shadow") ShadowInput(value);
        if (property == "sung.shadow" && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var shadow)) ShadowInput(shadow);
        if (property is "unsung.shadow.gradient" or "sung.shadow.gradient") GradientInput(value);
    }
    private static void ShadowInput(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("gradient", out var gradient)) GradientInput(gradient);
    }
    private static void GradientInput(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("stops", out var stops) && stops.ValueKind == JsonValueKind.Array && stops.GetArrayLength() > StyleEditingLimits.MaxStops) throw new StyleCommandBudgetExceededException();
    }
    private static void StrokesInput(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return;
        foreach (var stroke in value.EnumerateArray())
            if (stroke.ValueKind == JsonValueKind.Object && stroke.TryGetProperty("material", out var material)) MaterialInput(material);
    }
    private static void MaterialInput(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("kind", out var kind)) return;
        if (kind.ValueKind == JsonValueKind.String && kind.GetString() == "package") throw new JsonException("包材质描述仅供读取。");
        if (value.TryGetProperty("gradient", out var gradient)) GradientInput(gradient);
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
