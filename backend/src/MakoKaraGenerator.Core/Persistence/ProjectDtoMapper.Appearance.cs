using System.Buffers;
using System.Text.Json;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Projects;
using static MakoKaraGenerator.Core.Diagnostics.DiagnosticList;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Persistence;

// 两态外观、描边列表、「跟随未唱」判别对象与扫字样式的读写（schema v31 起，P3 设计 §10.5）。
// 结构性错误（材质、描边列表、跟随判别）取消读取；阴影、辉光与扫字样式的数值沿用原有诊断。
internal static partial class ProjectDtoMapper
{
    private static TextAppearanceDto ToDto(TextAppearance appearance) => new()
    {
        Fill = WriteMaterial(appearance.Fill.Material), Strokes = ToDto(appearance.Strokes),
        Shadow = ToDto(appearance.Shadow), Glow = ToDto(appearance.Glow),
    };

    private static SungAppearanceDto ToDto(SungAppearance appearance) => new()
    {
        Fill = WriteMaterial(appearance.Fill.Material),
        Strokes = ToFollowableDto(appearance.Strokes, strokes => ToDto(strokes ?? throw new ArgumentException("已唱描边列表不能为 null。"))),
        Shadow = ToFollowableDto(appearance.Shadow, shadow => JsonSerializer.SerializeToElement(ToDto(shadow), ProjectJson.Options)),
        Glow = ToFollowableDto(appearance.Glow, glow => JsonSerializer.SerializeToElement(ToDto(glow), ProjectJson.Options)),
    };

    private static JsonElement ToDto(StrokeLayers strokes) => JsonSerializer.SerializeToElement(
        strokes.Select(stroke => new StrokeLayerDto
        {
            Material = WriteMaterial(stroke.Material), WidthPx = stroke.WidthPx, Join = stroke.Join.ToString(),
        }).ToList(), ProjectJson.Options);

    private static WipeSpecDto ToDto(WipeSpec wipe) => new() { FeatherRatio = wipe.FeatherRatio, Highlight = ToDto(wipe.Highlight) };

    private static WipeHighlightDto? ToDto(WipeHighlightSpec? highlight) => highlight is null ? null : new()
    {
        Color = highlight.Color.ToHex(), WidthRatio = highlight.WidthRatio, Opacity = highlight.Opacity, Blend = highlight.Blend.ToString(),
    };

    private static TextAppearanceOverrideDto? ToDto(TextAppearanceOverride? overlay) => overlay is null || overlay.IsEmpty ? null : new()
    {
        Fill = ToOptionalFillOverrideDto(overlay.Fill), Strokes = overlay.Strokes is { } strokes ? ToDto(strokes) : default,
        Shadow = ToOptionalShadowDto(overlay.Shadow), Glow = ToOptionalGlowDto(overlay.Glow),
    };

    private static SungAppearanceOverrideDto? ToDto(SungAppearanceOverride? overlay) => overlay is null || overlay.IsEmpty ? null : new()
    {
        Fill = ToOptionalFillOverrideDto(overlay.Fill),
        Strokes = !overlay.Strokes.IsSet ? default : ToFollowableDto(overlay.Strokes.Value,
            strokes => ToDto(strokes ?? throw new ArgumentException("已唱描边覆盖不能为 null。"))),
        Shadow = !overlay.Shadow.IsSet ? default : ToFollowableDto(overlay.Shadow.Value,
            shadow => ToOptionalShadowDto(OptionalValue.Set<ShadowSpecOverride?>(shadow))),
        Glow = !overlay.Glow.IsSet ? default : ToFollowableDto(overlay.Glow.Value,
            glow => ToOptionalGlowDto(OptionalValue.Set<GlowSpecOverride?>(glow))),
    };

    private static WipeSpecOverrideDto? ToDto(WipeSpecOverride? overlay) => overlay is null || overlay.IsEmpty ? null : new()
    {
        FeatherRatio = overlay.FeatherRatio,
        Highlight = !overlay.Highlight.IsSet ? default : JsonSerializer.SerializeToElement(overlay.Highlight.Value is { } highlight
            ? new WipeHighlightDto
            {
                Color = highlight.Color?.ToHex(), WidthRatio = highlight.WidthRatio, Opacity = highlight.Opacity, Blend = highlight.Blend?.ToString(),
            } : null, ProjectJson.Options),
    };

    /// <summary>写出 <c>{"kind":"follow"}</c> 或 <c>{"kind":"set","value":…}</c>；设置值为 null 时保留 <c>"value": null</c>。</summary>
    private static JsonElement ToFollowableDto<T>(Followable<T> value, Func<T?, JsonElement> write)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", value.FollowsUnsung ? "follow" : "set");
            if (!value.FollowsUnsung)
            {
                writer.WritePropertyName("value");
                write(value.Value).WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return JsonSerializer.Deserialize<JsonElement>(buffer.WrittenSpan);
    }

    private static TextAppearance MapAppearance(TextAppearanceDto? dto, string path, List<ProjectIssue> issues,
        bool allowUnsupportedGradients = false)
    {
        if (dto is null) throw Error("StyleAppearanceMissing", path, "缺少完整外观。");
        return new()
        {
            Fill = new FillSpec { Material = ReadMaterial(dto.Fill, path + ".fill", issues, allowUnsupportedGradients) },
            Strokes = dto.Strokes.ValueKind == JsonValueKind.Undefined ? StrokeLayers.Empty
                : ReadStrokes(dto.Strokes, path + ".strokes", issues, allowUnsupportedGradients),
            Shadow = dto.Shadow is null ? null : MapShadowSpec(dto.Shadow, path + ".shadow", issues, allowUnsupportedGradients),
            Glow = dto.Glow is null ? null : MapGlowSpec(dto.Glow, path + ".glow", issues),
        };
    }

    private static SungAppearance MapSungAppearance(SungAppearanceDto? dto, string path, List<ProjectIssue> issues,
        bool allowUnsupportedGradients = false)
    {
        if (dto is null) throw Error("StyleAppearanceMissing", path, "缺少完整外观。");
        return new()
        {
            Fill = new FillSpec { Material = ReadMaterial(dto.Fill, path + ".fill", issues, allowUnsupportedGradients) },
            Strokes = ReadFollowable(dto.Strokes, path + ".strokes",
                value => ReadStrokes(value, path + ".strokes.value", issues, allowUnsupportedGradients)),
            Shadow = ReadFollowable<ShadowSpec?>(dto.Shadow, path + ".shadow", value => value.ValueKind == JsonValueKind.Null ? null
                : MapShadowSpec(JsonSerializer.Deserialize<ShadowSpecDto>(value, ProjectJson.Options), path + ".shadow.value", issues, allowUnsupportedGradients)),
            Glow = ReadFollowable<GlowSpec?>(dto.Glow, path + ".glow", value => value.ValueKind == JsonValueKind.Null ? null
                : MapGlowSpec(JsonSerializer.Deserialize<GlowSpecDto>(value, ProjectJson.Options)!, path + ".glow.value", issues)),
        };
    }

    /// <summary>读取由内向外的描边列表：每层必须完整给出材质、正宽度与拐角；层数与包材质位置按列表规则校验。</summary>
    private static StrokeLayers ReadStrokes(JsonElement value, string path, List<ProjectIssue> issues,
        bool allowUnsupportedGradients = false)
    {
        List<StrokeSpec> layers = [];
        foreach (JsonElement item in Array(value, path))
        {
            string layerPath = path + "." + layers.Count;
            Keys(item, layerPath, ["material", "widthPx", "join"], []);
            double width = Number(item.GetProperty("widthPx"), layerPath + ".widthPx");
            if (width <= 0d || !float.IsFinite((float)width)) throw Range(layerPath + ".widthPx");
            string join = RequiredText(item, "join", layerPath);
            if (!Enum.GetNames<StrokeJoin>().Contains(join, StringComparer.Ordinal)) throw Type(layerPath + ".join");
            layers.Add(new StrokeSpec
            {
                WidthPx = (float)width, Join = Enum.Parse<StrokeJoin>(join),
                Material = ReadMaterial(item.GetProperty("material"), layerPath + ".material", issues, allowUnsupportedGradients),
            });
        }
        StrokeLayers result = StrokeLayers.From(layers);
        return result.IsValid ? result : throw Error("StyleStrokesInvalid", path,
            $"描边列表至多 {StrokeLayers.MaxCount} 层，包材质只能用在第 0 层（最内层）。");
    }

    private static Followable<T> ReadFollowable<T>(JsonElement value, string path, Func<JsonElement, T?> read)
    {
        switch (FollowKind(value, path))
        {
            case "follow":
                Keys(value, path, ["kind"], []);
                return Followable.Follow<T>();
            default:
                Keys(value, path, ["kind", "value"], []);
                return Followable.Set(read(value.GetProperty("value")));
        }
    }

    private static OptionalValue<Followable<T>> ReadOptionalFollowable<T>(JsonElement value, string path,
        Func<JsonElement, OptionalValue<T>> read)
    {
        if (value.ValueKind == JsonValueKind.Undefined) return default;
        if (FollowKind(value, path) == "follow")
        {
            Keys(value, path, ["kind"], []);
            return OptionalValue.Set(Followable.Follow<T>());
        }
        Keys(value, path, ["kind", "value"], []);
        OptionalValue<T> inner = read(value.GetProperty("value"));
        return inner.IsSet ? OptionalValue.Set(Followable.Set(inner.Value)) : default;
    }

    private static string FollowKind(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Error("StyleFollowInvalid", path, "已唱一侧须写成 {\"kind\":\"follow\"} 或 {\"kind\":\"set\",\"value\":…}。");
        string kind = RequiredText(value, "kind", path);
        return kind is "follow" or "set" ? kind : throw Type(path + ".kind");
    }

    private static WipeSpec MapWipe(WipeSpecDto? dto, string path, List<ProjectIssue> issues)
    {
        if (dto is null) return WipeSpec.Default;
        WipeSpec result = new()
        {
            FeatherRatio = dto.FeatherRatio ?? WipeSpec.Default.FeatherRatio,
            Highlight = dto.Highlight is null ? null : MapHighlight(dto.Highlight, path + ".highlight", issues),
        };
        if (result.IsValid) return result;
        Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "扫字样式参数无效。", path);
        return WipeSpec.Default;
    }

    private static WipeHighlightSpec? MapHighlight(WipeHighlightDto dto, string path, List<ProjectIssue> issues)
    {
        if (dto.Color is null || !RgbaColor.TryParse(dto.Color, out RgbaColor color))
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "扫字线高亮颜色无效。", path + ".color");
            return null;
        }
        WipeHighlightSpec basis = new();
        if (!TryMapBlend(dto.Blend, path + ".blend", issues, out WipeHighlightBlend? blend)) return null;
        WipeHighlightSpec result = new()
        {
            Color = color, WidthRatio = dto.WidthRatio ?? basis.WidthRatio, Opacity = dto.Opacity ?? basis.Opacity, Blend = blend ?? basis.Blend,
        };
        if (result.IsValid) return result;
        Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "扫字线高亮参数无效。", path);
        return null;
    }

    private static bool TryMapBlend(string? text, string path, List<ProjectIssue> issues, out WipeHighlightBlend? blend)
    {
        blend = null;
        if (text is null) return true;
        if (Enum.GetNames<WipeHighlightBlend>().Contains(text, StringComparer.Ordinal))
        {
            blend = Enum.Parse<WipeHighlightBlend>(text);
            return true;
        }
        Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, $"扫字线高亮混合方式「{text}」无法识别。", path);
        return false;
    }

    private static TextAppearanceOverride? MapAppearanceOverride(TextAppearanceOverrideDto? dto, string path, List<ProjectIssue> issues)
    {
        if (dto is null) return null;
        TextAppearanceOverride result = new()
        {
            Fill = MapOptionalFillOverride(dto.Fill, path + ".fill", issues),
            Strokes = dto.Strokes.ValueKind == JsonValueKind.Undefined ? null : ReadStrokes(dto.Strokes, path + ".strokes", issues),
            Shadow = MapOptionalShadowOverride(dto.Shadow, path + ".shadow", issues),
            Glow = MapOptionalGlowOverride(dto.Glow, path + ".glow", issues),
        };
        return result.IsEmpty ? null : result;
    }

    private static SungAppearanceOverride? MapSungAppearanceOverride(SungAppearanceOverrideDto? dto, string path, List<ProjectIssue> issues)
    {
        if (dto is null) return null;
        SungAppearanceOverride result = new()
        {
            Fill = MapOptionalFillOverride(dto.Fill, path + ".fill", issues),
            Strokes = ReadOptionalFollowable(dto.Strokes, path + ".strokes",
                value => OptionalValue.Set(ReadStrokes(value, path + ".strokes.value", issues))),
            Shadow = ReadOptionalFollowable(dto.Shadow, path + ".shadow",
                value => MapOptionalShadowOverride(value, path + ".shadow.value", issues)),
            Glow = ReadOptionalFollowable(dto.Glow, path + ".glow",
                value => MapOptionalGlowOverride(value, path + ".glow.value", issues)),
        };
        return result.IsEmpty ? null : result;
    }

    private static WipeSpecOverride? MapWipeOverride(WipeSpecOverrideDto? dto, string path, List<ProjectIssue> issues)
    {
        if (dto is null) return null;
        WipeSpecOverride result = new()
        {
            FeatherRatio = dto.FeatherRatio, Highlight = MapOptionalHighlightOverride(dto.Highlight, path + ".highlight", issues),
        };
        if (!result.IsValid) Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "扫字样式覆盖参数无效。", path);
        return result.IsEmpty ? null : result;
    }

    private static OptionalValue<WipeHighlightOverride?> MapOptionalHighlightOverride(JsonElement value, string path, List<ProjectIssue> issues)
    {
        if (value.ValueKind == JsonValueKind.Undefined) return default;
        if (value.ValueKind == JsonValueKind.Null) return OptionalValue.Set<WipeHighlightOverride?>(null);
        try
        {
            WipeHighlightDto dto = JsonSerializer.Deserialize<WipeHighlightDto>(value, ProjectJson.Options)!;
            if (!TryMapBlend(dto.Blend, path + ".blend", issues, out WipeHighlightBlend? blend)) return default;
            return OptionalValue.Set<WipeHighlightOverride?>(new WipeHighlightOverride
            {
                Color = MapOptionalStyleColor(dto.Color, path + ".color", issues), WidthRatio = dto.WidthRatio, Opacity = dto.Opacity, Blend = blend,
            });
        }
        catch (JsonException exception)
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "扫字线高亮覆盖不是有效对象：" + exception.Message, path);
            return default;
        }
    }
}
