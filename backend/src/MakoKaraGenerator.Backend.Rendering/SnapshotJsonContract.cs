using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Backend.Rendering;

internal static class SnapshotJsonContract
{
    // This is the IPC source-member authority. Writable compatibility facades and
    // computed properties must not join the contract when Core evolves.
    private static readonly Dictionary<Type, string[]> Members = new()
    {
        [typeof(Composition)] = ["CanvasWidth", "CanvasHeight", "FrameRate", "BackgroundColor", "OutputDurationUs"],
        [typeof(LyricsDocument)] = ["PrimaryBranchId", "Branches", "SchemaVersion"],
        [typeof(LyricBranch)] = ["Id", "Name", "Role", "Language", "TimingMode", "StyleOverride", "Lines"],
        [typeof(LyricLine)] = ["PrimaryLineId", "RubyAnnotations", "Id", "Text", "Time", "TimingStatus", "StyleOverride", "Tokens"],
        [typeof(LyricToken)] = ["Id", "Text", "Time", "TimingStatus", "StyleOverride"],
        [typeof(RubyAnnotation)] = ["Id", "BaseTokenId", "Text", "Segments", "Language", "RelativeScale", "GapPx", "Alignment"],
        [typeof(RubyTextSegment)] = ["Id", "Text", "Time", "TimingStatus"],
        [typeof(LyricStyle)] = ["Typography", "Unsung", "Sung", "Wipe", "VerticalCenterRatio", "HorizontalAlignment", "Opacity", "Scale"],
        [typeof(LyricStyleOverride)] = ["Typography", "Unsung", "Sung", "Wipe", "VerticalCenterRatio", "HorizontalAlignment", "Opacity", "Scale"],
        [typeof(TextAppearance)] = ["Fill", "Strokes", "Shadow", "Glow"],
        [typeof(SungAppearance)] = ["Fill", "Strokes", "Shadow", "Glow"],
        [typeof(TextAppearanceOverride)] = ["Fill", "Strokes", "Shadow", "Glow"],
        [typeof(SungAppearanceOverride)] = ["Fill", "Strokes", "Shadow", "Glow"],
        [typeof(WipeSpec)] = ["FeatherRatio", "Highlight"],
        [typeof(WipeSpecOverride)] = ["FeatherRatio", "Highlight"],
        [typeof(WipeHighlightSpec)] = ["Color", "WidthRatio", "Opacity", "Blend"],
        [typeof(WipeHighlightOverride)] = ["Color", "WidthRatio", "Opacity", "Blend"],
        [typeof(TypographySpec)] = ["FontFamilyName", "FallbackFamilyNames", "FontSizePx", "FontWeight", "IsBold", "IsItalic", "LetterSpacingPx"],
        [typeof(TypographySpecOverride)] = ["FontFamilyName", "FallbackFamilyNames", "FontSizePx", "FontWeight", "IsBold", "IsItalic", "LetterSpacingPx"],
        [typeof(FillSpec)] = ["Material"],
        [typeof(FillSpecOverride)] = ["Material"],
        [typeof(StrokeSpec)] = ["Material", "WidthPx", "Join"],
        [typeof(ShadowSpec)] = ["Color", "Opacity", "AngleDegrees", "DistancePx", "BlurRadiusPx"],
        [typeof(ShadowSpecOverride)] = ["Color", "Opacity", "AngleDegrees", "DistancePx", "BlurRadiusPx"],
        [typeof(GlowSpec)] = ["Color", "Opacity", "BlurRadiusPx", "SpreadPx"],
        [typeof(GlowSpecOverride)] = ["Color", "Opacity", "BlurRadiusPx", "SpreadPx"],
        [typeof(MaterialSpec)] = [],
        [typeof(MaterialSpec.Solid)] = ["Color"],
        [typeof(MaterialSpec.Gradient)] = ["Value"],
        [typeof(MaterialSpec.PackageMaterial)] = ["PackageId", "Revision", "ExportId", "Parameters"],
        [typeof(GradientSpec)] = ["Kind", "AngleDegrees", "CenterX", "CenterY", "RadiusX", "RadiusY", "CoordinateSpace", "ColorSpace", "HuePath", "Stops"],
        [typeof(GradientStop)] = ["Offset", "Color"],
        [typeof(RgbaColor)] = ["Red", "Green", "Blue", "Alpha"],
        [typeof(TimeRange)] = ["StartUs", "EndUs"],
        [typeof(BackgroundSource)] = ["Kind", "Path", "Media", "Fit", "Playback"],
        [typeof(LineAnimation)] = ["DurationUs", "Easing", "StartOpacity", "StartScale", "StartOffsetPx"],
        [typeof(LyricPresentationDefinition)] = ["Nodes", "Programs", "LineOverrides"],
        [typeof(SceneContainerDefinition)] = ["Id", "Name", "ParentId", "Placement", "ReferenceRect", "Layout", "Content", "Attachment"],
        [typeof(ScenePlacement)] = ["Position", "Anchor", "RotationDegrees", "Scale"],
        [typeof(NormalizedPosition)] = ["X", "Y"],
        [typeof(SceneReferenceRect)] = ["WidthRatio", "HeightRatio"],
        [typeof(LyricContainerLayout)] = ["Kind", "DefaultPlacement", "SafeArea", "MaxWidthRatio", "SlotIds", "SlotAnimation"],
        [typeof(NormalizedSafeArea)] = ["Left", "Top", "Right", "Bottom"],
        [typeof(SlotAnimationSpec)] = ["RevealMode", "RevealProgress", "RevealDelayUs", "RevealLeadUs", "MaxVisibleLines", "ConflictPolicy", "RetentionPolicy", "EnterDurationUs", "ExitDurationUs", "EnterFrom", "Hold", "ExitTo", "ExitEasing", "Easing"],
        [typeof(SceneTransform)] = ["Opacity", "Scale", "OffsetXPx", "OffsetYPx", "RotationDegrees"],
        [typeof(SceneContent)] = [],
        [typeof(CountdownSequenceContent)] = ["MarkNodeIds", "Appearance", "Shape", "Animation"],
        [typeof(CountdownShapeSpec)] = ["Kind", "SvgXml", "SvgColorMode", "BarMinHeightRatio"],
        [typeof(CountdownAnimationSpec)] = ["Mode", "BlinkRatio", "BlinkCount", "FeatherRatio"],
        [typeof(TextMarkContent)] = ["Text", "AppearanceOverride"],
        [typeof(SceneTextAppearance)] = ["Typography", "Pending", "Elapsed", "Opacity"],
        [typeof(SceneTextAppearanceOverride)] = ["Typography", "Pending", "Elapsed", "Opacity"],
        [typeof(SceneAttachment)] = ["Mode"],
        [typeof(LyricDisplayProgram)] = ["Id", "BranchId", "RootContainerId", "DefaultContainerId", "AnimationPolicy", "Clips", "LeadIns", "LeadInGeneration"],
        [typeof(LyricDisplayClip)] = ["Id", "Name", "LineIds", "ContainerId", "ScheduleContext"],
        [typeof(LyricClipScheduleContext)] = ["LineIds", "IsDefaultSegment", "PreviousLineId", "NextLineId"],
        [typeof(LyricLeadIn)] = ["Id", "Name", "Enabled", "TargetLineId", "IndicatorContainerId", "DurationUs", "Origin"],
        [typeof(LeadInGenerationSettings)] = ["IncludeOpening", "IncludeLongGaps", "GapThresholdUs", "DurationUs", "IndicatorContainerId", "ExcludedLineIds"],
        [typeof(LyricLinePresentation)] = ["BranchId", "LineId", "LocalTransform", "EnterAnimation"],
        [typeof(LyricLocalTransform)] = ["OffsetXRatio", "OffsetYRatio", "RotationDegrees", "Scale"],
    };

    private static readonly string[] TypographyFacades = ["FontFamilyName", "FallbackFamilyNames", "FontSizePx", "FontWeight",
        "IsBold", "IsItalic", "LetterSpacingPx"];
    private static readonly string[] FillFacades = ["UnsungFill", "SungFill", "UnsungColor", "UnsungGradientSpec", "SungColor", "SungGradientSpec"];

    private static readonly Dictionary<Type, string[]> Facades = new()
    {
        [typeof(LyricsDocument)] = ["Language", "Lines"],
        [typeof(LyricStyle)] = [.. TypographyFacades, .. FillFacades, "Stroke", "Shadow", "Glow"],
        [typeof(LyricStyleOverride)] = [.. TypographyFacades, .. FillFacades, "UnsungStrokes", "Stroke", "Shadow", "Glow", "ShadowSpec"],
        [typeof(SceneTextAppearance)] = ["PendingFill", "ElapsedFill", "Stroke", "Shadow", "Glow"],
        [typeof(SceneTextAppearanceOverride)] = ["PendingFill", "ElapsedFill"],
        [typeof(FillSpec)] = ["Color", "Gradient"],
        [typeof(FillSpecOverride)] = ["Color", "Gradient"],
        [typeof(StrokeSpec)] = ["Color", "Gradient"],
    };

    internal static JsonSerializerOptions CreateOptions(bool packageMaterials = false, bool strictEnums = false)
    {
        DefaultJsonTypeInfoResolver resolver = new();
        resolver.Modifiers.Add(ConfigureSource);
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 64,
            TypeInfoResolver = resolver,
        };
        if (packageMaterials) options.Converters.Add(new PackageMaterialSpecConverter());
        options.Converters.Add(new FrameRateConverter());
        options.Converters.Add(new MediaRevisionConverter());
        options.Converters.Add(new OptionalValueConverterFactory());
        options.Converters.Add(new FollowableConverterFactory());
        options.Converters.Add(new StrokeLayersConverter());
        options.Converters.Add(strictEnums ? new ExactEnumConverterFactory() : new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private sealed class ExactEnumConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(ExactEnumConverter<>).MakeGenericType(typeToConvert))!;
    }
    private sealed class ExactEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("枚举必须为规范字符串。");
            string? text = reader.GetString();
            if (!Enum.TryParse(text, out T value) || !Enum.IsDefined(value) || value.ToString() != text)
                throw new JsonException("枚举名称或大小写无效。");
            return value;
        }
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value)) throw new JsonException("未知枚举。");
            writer.WriteStringValue(value.ToString());
        }
    }

    private static void ConfigureSource(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;
        if (info.Type == typeof(MaterialSpec))
            info.PolymorphismOptions = Polymorphism(new(typeof(MaterialSpec.Solid), "solid"), new(typeof(MaterialSpec.Gradient), "gradient"));
        else if (info.Type == typeof(SceneContent))
            info.PolymorphismOptions = Polymorphism(new(typeof(CountdownSequenceContent), "countdownSequence"), new(typeof(TextMarkContent), "textMark"));

        if (Members.TryGetValue(info.Type, out string[]? members))
        {
            HashSet<string> allowed = members.Select(JsonNamingPolicy.CamelCase.ConvertName).ToHashSet(StringComparer.Ordinal);
            HashSet<string> classified = new(allowed, StringComparer.Ordinal);
            if (Facades.TryGetValue(info.Type, out string[]? facades))
                classified.UnionWith(facades.Select(JsonNamingPolicy.CamelCase.ConvertName));
            if (info.Properties.Any(property => property.Set is not null && !classified.Contains(property.Name)))
                throw new SnapshotContractException("SnapshotTypeUnsupported", "源类型增加了未登记的可写字段：" + info.Type.Name);
            foreach (JsonPropertyInfo property in info.Properties.ToArray())
                if (!allowed.Contains(property.Name)) info.Properties.Remove(property);
            if (!allowed.SetEquals(info.Properties.Select(property => property.Name)))
                throw new InvalidOperationException("快照源成员清单与领域类型不匹配：" + info.Type.Name);
        }
        else if (info.Type.Assembly == typeof(Project).Assembly)
            throw new SnapshotContractException("SnapshotTypeUnsupported", "快照含未登记的源类型：" + info.Type.Name);

        foreach (JsonPropertyInfo property in info.Properties) property.IsRequired = true;
    }

    private static JsonPolymorphismOptions Polymorphism(params JsonDerivedType[] types)
    {
        JsonPolymorphismOptions result = new()
        {
            TypeDiscriminatorPropertyName = "kind",
            UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
            IgnoreUnrecognizedTypeDiscriminators = false,
        };
        foreach (JsonDerivedType type in types) result.DerivedTypes.Add(type);
        return result;
    }

    internal static int ValidateSourceGraph(object source, JsonSerializerOptions options, int maximumValues, int maximumTextUnits, bool packageMaterials = false)
    {
        int values = 0;
        long textUnits = 0;
        Visit(source, 0);
        void Visit(object? value, int depth)
        {
            if (++values > maximumValues || depth > 60)
                throw new SnapshotContractException("SnapshotSizeExceeded", "快照源对象/嵌套深度超过上限。");
            if (value is null) return;
            if (value is string text)
            {
                textUnits += text.Length;
                if (textUnits > maximumTextUnits)
                    throw new SnapshotContractException("SnapshotSizeExceeded", "快照文本超过输入预算。");
                return;
            }
            if (value is JsonElement element)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (JsonProperty property in element.EnumerateObject()) Visit(property.Value, depth + 1);
                        break;
                    case JsonValueKind.Array:
                        foreach (JsonElement item in element.EnumerateArray()) Visit(item, depth + 1);
                        break;
                    case JsonValueKind.String:
                        textUnits += element.GetString()!.Length;
                        if (textUnits > maximumTextUnits)
                            throw new SnapshotContractException("SnapshotSizeExceeded", "快照文本超过输入预算。");
                        break;
                    case JsonValueKind.Number:
                        if (!element.TryGetDouble(out double scalar) || !double.IsFinite(scalar))
                            throw new SnapshotContractException("SnapshotInvalid", "快照源含非有限数值。");
                        break;
                    case JsonValueKind.Null:
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        break;
                    default:
                        throw new SnapshotContractException("SnapshotInvalid", "快照源含未定义JSON值。");
                }
                return;
            }
            Type type = value.GetType();
            if (type.IsEnum)
            {
                if (!Enum.IsDefined(type, value))
                    throw new SnapshotContractException("SnapshotInvalid", "快照源含非法枚举。");
                return;
            }
            if (value is float number && !float.IsFinite(number) || value is double real && !double.IsFinite(real))
                throw new SnapshotContractException("SnapshotInvalid", "快照源含非有限数值。");
            if (type.IsPrimitive || value is decimal) return;
            if (!packageMaterials && value is MaterialSpec.PackageMaterial)
                throw new SnapshotContractException("SnapshotEffectsUnsupported", "本能力尚不支持包材质。");
            if (packageMaterials && value is MaterialSpec.PackageMaterial material)
            {
                Visit(material.PackageId, depth + 1); Visit(material.Revision, depth + 1);
                Visit(material.ExportId, depth + 1); Visit(material.Parameters, depth + 1);
                return;
            }
            if (value is EffectParameterOverrides parameters)
            {
                if (parameters.Json.Length > maximumTextUnits)
                    throw new SnapshotContractException("SnapshotSizeExceeded", "快照参数超过输入预算。");
                using JsonDocument document = JsonDocument.Parse(parameters.Json);
                Visit(document.RootElement, depth + 1);
                return;
            }
            if (value is FrameRate rate)
            {
                if (rate.Numerator <= 0 || rate.Denominator <= 0)
                    throw new SnapshotContractException("SnapshotInvalid", "快照帧率必须有效。");
                return;
            }
            if (value is MediaFileRevision media)
            {
                Visit(media.Hash, depth + 1);
                Visit(media.OriginalName, depth + 1);
                return;
            }
            if (value is IEnumerable collection)
            {
                foreach (object? item in collection) Visit(item, depth + 1);
                return;
            }
            if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(OptionalValue<>)
                || type.GetGenericTypeDefinition() == typeof(Followable<>)))
            {
                Visit(type.GetProperty("Value")!.GetValue(value), depth + 1);
                return;
            }
            JsonTypeInfo info = options.GetTypeInfo(type);
            foreach (JsonPropertyInfo property in info.Properties)
                Visit(property.Get?.Invoke(value), depth + 1);
        }
        return values;
    }

    private sealed class PackageMaterialSpecConverter : JsonConverter<MaterialSpec>
    {
        private sealed record Solid(string Kind, RgbaColor Color);
        private sealed record Gradient(string Kind, GradientSpec Value);
        private sealed record Package(string Kind, string PackageId, string Revision, string ExportId, JsonElement Parameters);
        public override MaterialSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;
            return root.GetProperty("kind").GetString() switch
            {
                "solid" => new MaterialSpec.Solid((root.Deserialize<Solid>(options) ?? throw new JsonException()).Color),
                "gradient" => new MaterialSpec.Gradient((root.Deserialize<Gradient>(options) ?? throw new JsonException()).Value),
                "package" => ReadPackage(root.Deserialize<Package>(options) ?? throw new JsonException()),
                _ => throw new JsonException("快照材质类型无效。"),
            };
        }
        private static MaterialSpec.PackageMaterial ReadPackage(Package value) => new MaterialSpec.PackageMaterial(value.PackageId,
            value.Revision, value.ExportId, EffectParameterOverrides.Parse(value.Parameters.GetRawText()));
        public override void Write(Utf8JsonWriter writer, MaterialSpec value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case MaterialSpec.Solid solid: JsonSerializer.Serialize(writer, new Solid("solid", solid.Color), options); break;
                case MaterialSpec.Gradient gradient: JsonSerializer.Serialize(writer, new Gradient("gradient", gradient.Value), options); break;
                case MaterialSpec.PackageMaterial package:
                    using (JsonDocument parameters = JsonDocument.Parse(package.Parameters.Json))
                        JsonSerializer.Serialize(writer, new Package("package", package.PackageId, package.Revision, package.ExportId, parameters.RootElement), options);
                    break;
                default: throw new JsonException("快照材质类型无效。");
            }
        }
    }

    private sealed class FrameRateConverter : JsonConverter<FrameRate>
    {
        private sealed record Rate(int Numerator, int Denominator);
        public override FrameRate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Rate source = JsonSerializer.Deserialize<Rate>(ref reader, options) ?? throw new JsonException("帧率不能为空。");
            FrameRate rate = new(source.Numerator, source.Denominator);
            if (rate.Numerator != source.Numerator || rate.Denominator != source.Denominator)
                throw new JsonException("帧率必须使用规范约分值。");
            return rate;
        }
        public override void Write(Utf8JsonWriter writer, FrameRate value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, new Rate(value.Numerator, value.Denominator), options);
    }

    private sealed class MediaRevisionConverter : JsonConverter<MediaFileRevision>
    {
        private sealed record Revision(string Hash, long ByteLength, string OriginalName);
        public override MediaFileRevision Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Revision value = JsonSerializer.Deserialize<Revision>(ref reader, options) ?? throw new JsonException("媒体身份不能为空。");
            return new(value.Hash, value.ByteLength, value.OriginalName);
        }
        public override void Write(Utf8JsonWriter writer, MediaFileRevision value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, new Revision(value.Hash, value.ByteLength, value.OriginalName), options);
    }

    private sealed class OptionalValueConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType
            && typeToConvert.GetGenericTypeDefinition() == typeof(OptionalValue<>);
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(OptionalValueConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()))!;
    }

    private sealed class OptionalValueConverter<T> : JsonConverter<OptionalValue<T>>
    {
        private sealed record ValueSource(bool IsSet, T? Value);
        public override OptionalValue<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            ValueSource source = JsonSerializer.Deserialize<ValueSource>(ref reader, options) ?? throw new JsonException("三态覆盖不能为空。");
            if (!source.IsSet && !EqualityComparer<T?>.Default.Equals(source.Value, default))
                throw new JsonException("未设置覆盖不能携带值。");
            return source.IsSet ? OptionalValue.Set(source.Value) : default;
        }
        public override void Write(Utf8JsonWriter writer, OptionalValue<T> value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, new ValueSource(value.IsSet, value.Value), options);
    }

    private sealed class FollowableConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType
            && typeToConvert.GetGenericTypeDefinition() == typeof(Followable<>);
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(FollowableConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()))!;
    }

    /// <summary>「跟随未唱」与「设置」两种取值；跟随不能携带值。</summary>
    private sealed class FollowableConverter<T> : JsonConverter<Followable<T>>
    {
        private sealed record ValueSource(bool FollowsUnsung, T? Value);
        public override Followable<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            ValueSource source = JsonSerializer.Deserialize<ValueSource>(ref reader, options) ?? throw new JsonException("跟随值不能为空。");
            if (source.FollowsUnsung && !EqualityComparer<T?>.Default.Equals(source.Value, default))
                throw new JsonException("跟随未唱不能携带值。");
            return source.FollowsUnsung ? Followable.Follow<T>() : Followable.Set(source.Value);
        }
        public override void Write(Utf8JsonWriter writer, Followable<T> value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, new ValueSource(value.FollowsUnsung, value.Value), options);
    }

    /// <summary>描边列表按层数组读写，读取后仍要通过列表规则校验。</summary>
    private sealed class StrokeLayersConverter : JsonConverter<StrokeLayers>
    {
        public override StrokeLayers Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            StrokeSpec[] layers = JsonSerializer.Deserialize<StrokeSpec[]>(ref reader, options) ?? throw new JsonException("描边列表不能为空。");
            StrokeLayers result = StrokeLayers.Create(layers);
            return result.IsValid ? result : throw new JsonException("描边列表无效。");
        }
        public override void Write(Utf8JsonWriter writer, StrokeLayers value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value.AsImmutableArray(), options);
    }
}
