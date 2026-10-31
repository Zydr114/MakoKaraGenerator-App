using System.Collections.Immutable;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using static MakoKaraGenerator.Core.Diagnostics.DiagnosticList;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// 领域模型与持久化 DTO 之间的双向映射。
/// </summary>
/// <remarks>
/// <para>
/// 写入方向是无损的，包括非法数据：非法的时间区间会照原样写回
/// <c>startUs</c>/<c>endUs</c>，因此"读取 → 保存"不会静默修正用户的文件
/// （docs/development.md §19 禁止静默修正）。
/// </para>
/// <para>
/// 读取方向尽可能继续，并把每一处可疑之处记录为诊断：缺失字段使用默认值并说明默认值是什么，
/// 而不是抛异常或悄悄补 0。这正是 DTO 与领域模型分离的目的。
/// </para>
/// </remarks>
internal static partial class ProjectDtoMapper
{
    private const string FallbackProjectName = "未命名项目";

    // ---------------- 写入方向 ----------------

    internal static ProjectFileDto ToDto(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        return new ProjectFileDto
        {
            ApplicationVersion = ProductVersion.Current,
            SchemaVersion = project.SchemaVersion,
            Name = project.Name,
            Composition = ToDto(project.Composition),
            LyricStyle = ToDto(project.LyricStyle),
            LyricStylePresetId = project.LyricStylePresetId,
            AudioPath = project.AudioPath,
            Background = ToDto(project.Background),
            EnterAnimation = ToDto(project.EnterAnimation),
            LyricPresentation = ToDto(project.LyricPresentation),
        };
    }

    internal static LyricsFileDto ToDto(LyricsDocument lyrics)
    {
        ArgumentNullException.ThrowIfNull(lyrics);

        return new LyricsFileDto
        {
            SchemaVersion = lyrics.SchemaVersion,
            PrimaryBranchId = lyrics.PrimaryBranchId,
            Branches = [.. lyrics.Branches.Select(branch => new LyricBranchDto
            {
                Id = branch.Id, Name = branch.Name, Role = branch.Role.ToString(), Language = branch.Language,
                TimingMode = branch.TimingMode.ToString(),
                StyleOverride = ToDto(branch.StyleOverride),
                Lines = [.. branch.Lines.Select(ToDto)],
            })],
        };
    }

    private static CompositionDto ToDto(Composition composition) => new()
    {
        CanvasWidth = composition.CanvasWidth,
        CanvasHeight = composition.CanvasHeight,
        FrameRate = new FrameRateDto
        {
            Numerator = composition.FrameRate.Numerator,
            Denominator = composition.FrameRate.Denominator,
        },
        BackgroundColor = composition.BackgroundColor.ToHex(),
        OutputDurationUs = composition.OutputDurationUs,
    };

    private static LyricLineDto ToDto(LyricLine line) => new()
    {
        Id = line.Id,
        PrimaryLineId = line.PrimaryLineId,
        RubyAnnotations = line.RubyAnnotations.IsEmpty ? null : [.. line.RubyAnnotations.Select(ruby => new RubyAnnotationDto
        {
            Id = ruby.Id, BaseTokenId = ruby.BaseTokenId, Text = ruby.Text, Language = ruby.Language,
            RelativeScale = ruby.RelativeScale, GapPx = ruby.GapPx, Alignment = ruby.Alignment.ToString(),
            Segments = ruby.Segments.IsEmpty ? null : [.. ruby.Segments.Select(segment => new RubyTextSegmentDto
            {
                Id = segment.Id,
                Text = segment.Text,
                StartUs = segment.Time?.StartUs,
                EndUs = segment.Time?.EndUs,
                TimingState = segment.TimingStatus.ToString(),
            })],
        })],
        Text = line.Text,
        StartUs = line.Time?.StartUs,
        EndUs = line.Time?.EndUs,
        TimingState = line.TimingStatus.ToString(),
        StyleOverride = ToDto(line.StyleOverride),
        Tokens = line.Tokens.IsEmpty ? null : [.. line.Tokens.Select(ToDto)],
    };

    private static ScenePlacementDto? ToDto(ScenePlacement? placement) => placement is null ? null : new()
    {
        X = placement.Position.X, Y = placement.Position.Y, Anchor = placement.Anchor.ToString(),
        RotationDegrees = placement.RotationDegrees, Scale = placement.Scale,
    };

    private static LyricTokenDto ToDto(LyricToken token) => new()
    {
        Id = token.Id,
        Text = token.Text,
        StartUs = token.Time?.StartUs,
        EndUs = token.Time?.EndUs,
        TimingState = token.TimingStatus.ToString(),
        StyleOverride = ToDto(token.StyleOverride),
    };

    internal static LyricStyleDto ToDto(LyricStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);

        return new LyricStyleDto
        {
            Typography = new TypographySpecDto
            {
                FontFamilyName = style.Typography.FontFamilyName,
                FallbackFamilyNames = [.. style.Typography.FallbackFamilyNames],
                FontSizePx = style.Typography.FontSizePx,
                FontWeight = style.Typography.FontWeight,
                IsBold = style.Typography.IsBold,
                IsItalic = style.Typography.IsItalic,
                LetterSpacingPx = style.Typography.LetterSpacingPx,
            },
            UnsungFill = ToDto(style.UnsungFill),
            SungFill = ToDto(style.SungFill),
            Stroke = ToDto(style.Stroke),
            Shadow = ToDto(style.Shadow),
            VerticalCenterRatio = style.VerticalCenterRatio,
            HorizontalAlignment = style.HorizontalAlignment.ToString(),
            Opacity = style.Opacity,
            Scale = style.Scale,
        };
    }

    private static GradientSpecDto? ToDto(GradientSpec? gradient) => gradient is null
        ? null
        : new GradientSpecDto
        {
            Kind = gradient.Kind.ToString(),
            AngleDegrees = gradient.AngleDegrees,
            CenterX = gradient.CenterX,
            CenterY = gradient.CenterY,
            RadiusX = gradient.RadiusX,
            RadiusY = gradient.RadiusY,
            CoordinateSpace = gradient.CoordinateSpace.ToString(),
            ColorSpace = gradient.ColorSpace.ToString(),
            HuePath = gradient.HuePath.ToString(),
            Stops = [.. gradient.Stops.Select(stop => new GradientStopDto
            {
                Offset = stop.Offset,
                Color = stop.Color.ToHex(),
            })],
        };

    private static ShadowSpecDto? ToDto(ShadowSpec? shadow) => shadow is null
        ? null
        : new ShadowSpecDto
        {
            Color = shadow.Color.ToHex(),
            Opacity = shadow.Opacity,
            AngleDegrees = shadow.AngleDegrees,
            DistancePx = shadow.DistancePx,
            BlurRadiusPx = shadow.BlurRadiusPx,
        };

    /// <summary>把样式覆盖写成 DTO；空覆盖返回 <see langword="null"/>（整个字段省略）。</summary>
    private static LyricStyleOverrideDto? ToDto(LyricStyleOverride? styleOverride)
    {
        if (styleOverride is null || styleOverride.IsEmpty) return null;
        return new LyricStyleOverrideDto
        {
            Typography = styleOverride.Typography is { } typography ? ToDto(typography) : null,
            UnsungFill = ToOptionalFillOverrideDto(styleOverride.UnsungFill),
            SungFill = ToOptionalFillOverrideDto(styleOverride.SungFill),
            Stroke = ToOptionalStrokeDto(styleOverride.Stroke),
            Shadow = ToOptionalShadowDto(styleOverride.Shadow),
            VerticalCenterRatio = styleOverride.VerticalCenterRatio,
            HorizontalAlignment = styleOverride.HorizontalAlignment?.ToString(),
            Opacity = styleOverride.Opacity,
            Scale = styleOverride.Scale,
        };
    }

    private static TypographySpecOverrideDto? ToDto(TypographySpecOverride typography)
    {
        if (typography.IsEmpty) return null;
        return new TypographySpecOverrideDto
        {
            FontFamilyName = typography.FontFamilyName.IsSet
                ? System.Text.Json.JsonSerializer.SerializeToElement(typography.FontFamilyName.Value) : default,
            FallbackFamilyNames = typography.FallbackFamilyNames is { } fallbacks ? [.. fallbacks] : null,
            FontSizePx = typography.FontSizePx,
            FontWeight = typography.FontWeight,
            IsBold = typography.IsBold,
            IsItalic = typography.IsItalic,
            LetterSpacingPx = typography.LetterSpacingPx,
        };
    }

    private static System.Text.Json.JsonElement ToOptionalShadowDto(OptionalValue<ShadowSpecOverride?> value) =>
        value.IsSet ? System.Text.Json.JsonSerializer.SerializeToElement(value.Value is { } shadow
            ? new ShadowSpecOverrideDto
            {
                Color = shadow.Color?.ToHex(),
                Opacity = shadow.Opacity,
                AngleDegrees = shadow.AngleDegrees,
                DistancePx = shadow.DistancePx,
                BlurRadiusPx = shadow.BlurRadiusPx,
            } : null, ProjectJson.Options) : default;

    private static TypographySpecOverride? MapTypographyOverride(
        TypographySpecOverrideDto? dto,
        string location,
        List<ProjectIssue> issues)
    {
        if (dto is null) return null;
        OptionalValue<string?> family = default;
        if (dto.FontFamilyName.ValueKind == System.Text.Json.JsonValueKind.Null)
            family = OptionalValue.Set<string?>(null);
        else if (dto.FontFamilyName.ValueKind == System.Text.Json.JsonValueKind.String)
            family = OptionalValue.Set<string?>(dto.FontFamilyName.GetString());
        else if (dto.FontFamilyName.ValueKind != System.Text.Json.JsonValueKind.Undefined)
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error,
                "字体族覆盖必须是字符串或 null。", location + ".fontFamilyName");

        TypographySpecOverride result = new()
        {
            FontFamilyName = family,
            FallbackFamilyNames = MapFallbackFamilyNames(dto.FallbackFamilyNames, location + ".fallbackFamilyNames", issues),
            FontSizePx = dto.FontSizePx,
            FontWeight = dto.FontWeight,
            IsBold = dto.IsBold,
            IsItalic = dto.IsItalic,
            LetterSpacingPx = dto.LetterSpacingPx,
        };
        return result.IsEmpty ? null : result;
    }

    // ---------------- 读取方向 ----------------

    internal static string MapProjectName(string? name, List<ProjectIssue> issues)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        Add(
            issues,
            DiagnosticCode.MissingProjectName,
            IssueSeverity.Error,
            $"缺少项目名称，已暂时使用 '{FallbackProjectName}'。",
            "name");

        return FallbackProjectName;
    }

    /// <summary>把背景源写成 DTO。</summary>
    internal static BackgroundDto ToDto(BackgroundSource background)
    {
        ArgumentNullException.ThrowIfNull(background);

        return new BackgroundDto
        {
            Kind = ToText(background.Kind),
            Path = background.Path,
            Fit = ToText(background.Fit),
            Playback = ToText(background.Playback),
        };
    }

    /// <summary>读出背景源；不认识的写法回退到默认并报告。</summary>
    internal static BackgroundSource MapBackground(BackgroundDto? dto, List<ProjectIssue> issues)
    {
        if (dto is null)
        {
            return BackgroundSource.Color;
        }

        BackgroundKind kind = MapBackgroundKind(dto.Kind, issues);

        if (kind == BackgroundKind.Color)
        {
            return BackgroundSource.Color with { Fit = MapBackgroundFit(dto.Fit, issues) };
        }

        if (string.IsNullOrWhiteSpace(dto.Path))
        {
            Add(
                issues,
                DiagnosticCode.InvalidBackgroundSource,
                IssueSeverity.Warning,
                "背景源没有给出路径，已按「只有背景色」处理。",
                "background.path");

            return BackgroundSource.Color;
        }

        return new BackgroundSource
        {
            Kind = kind,
            Path = dto.Path,
            Fit = MapBackgroundFit(dto.Fit, issues),
            Playback = MapBackgroundPlayback(dto.Playback, issues),
        };
    }

    private static BackgroundKind MapBackgroundKind(string? text, List<ProjectIssue> issues)
    {
        if (text is null || text.Equals("color", StringComparison.OrdinalIgnoreCase))
        {
            return BackgroundKind.Color;
        }

        if (text.Equals("image", StringComparison.OrdinalIgnoreCase))
        {
            return BackgroundKind.Image;
        }

        if (text.Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            return BackgroundKind.Video;
        }

        Add(
            issues,
            DiagnosticCode.InvalidBackgroundSource,
            IssueSeverity.Warning,
            $"不认识的背景种类「{text}」，已按「只有背景色」处理。",
            "background.kind");

        return BackgroundKind.Color;
    }

    private static BackgroundFit MapBackgroundFit(string? text, List<ProjectIssue> issues)
    {
        if (text is null || text.Equals("contain", StringComparison.OrdinalIgnoreCase))
        {
            return BackgroundFit.Contain;
        }

        if (text.Equals("cover", StringComparison.OrdinalIgnoreCase))
        {
            return BackgroundFit.Cover;
        }

        if (text.Equals("original", StringComparison.OrdinalIgnoreCase))
        {
            return BackgroundFit.Original;
        }

        Add(
            issues,
            DiagnosticCode.InvalidBackgroundSource,
            IssueSeverity.Warning,
            $"不认识的背景适配方式「{text}」，已按「完整显示」处理。",
            "background.fit");

        return BackgroundFit.Contain;
    }

    private static BackgroundPlaybackMode MapBackgroundPlayback(string? text, List<ProjectIssue> issues)
    {
        if (text is null || text.Equals("loop", StringComparison.OrdinalIgnoreCase))
        {
            return BackgroundPlaybackMode.Loop;
        }

        if (text.Equals("freeze", StringComparison.OrdinalIgnoreCase))
        {
            return BackgroundPlaybackMode.FreezeLastFrame;
        }

        Add(
            issues,
            DiagnosticCode.InvalidBackgroundSource,
            IssueSeverity.Warning,
            $"不认识的背景播放方式「{text}」，已按「循环」处理。",
            "background.playback");

        return BackgroundPlaybackMode.Loop;
    }

    /// <summary>写出进入动画；没有动画时返回 <see langword="null"/>（不写空对象）。</summary>
    internal static LineAnimationDto? ToDto(LineAnimation? animation) => animation is null
        ? null
        : new LineAnimationDto
        {
            DurationUs = animation.DurationUs,
            Easing = ToText(animation.Easing),
            StartOpacity = animation.StartOpacity,
            StartScale = animation.StartScale,
            StartOffsetPx = animation.StartOffsetPx,
        };

    /// <summary>读出进入动画；缺省字段按默认值，不认识的缓动回退并报告。</summary>
    /// <param name="dto">持久化形态。</param>
    /// <param name="location">诊断位置。</param>
    /// <param name="issues">诊断收集器。</param>
    /// <returns>动画；没有动画时为 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="issues"/> 为 <see langword="null"/> 时抛出。</exception>
    internal static LineAnimation? MapLineAnimation(LineAnimationDto? dto, string location, List<ProjectIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        if (dto is null)
        {
            return null;
        }

        LineAnimation defaults = LineAnimation.Default;

        return new LineAnimation
        {
            DurationUs = dto.DurationUs ?? defaults.DurationUs,
            Easing = MapAnimationEasing(dto.Easing, location + ".easing", issues),
            StartOpacity = dto.StartOpacity ?? defaults.StartOpacity,
            StartScale = dto.StartScale ?? defaults.StartScale,
            StartOffsetPx = dto.StartOffsetPx ?? defaults.StartOffsetPx,
        };
    }

    private static AnimationEasing MapAnimationEasing(string? text, string location, List<ProjectIssue> issues)
    {
        if (text is null || text.Equals("easeOut", StringComparison.OrdinalIgnoreCase))
        {
            return AnimationEasing.EaseOut;
        }

        if (text.Equals("linear", StringComparison.OrdinalIgnoreCase))
        {
            return AnimationEasing.Linear;
        }

        if (text.Equals("easeIn", StringComparison.OrdinalIgnoreCase))
        {
            return AnimationEasing.EaseIn;
        }

        if (text.Equals("easeInOut", StringComparison.OrdinalIgnoreCase))
        {
            return AnimationEasing.EaseInOut;
        }

        Add(
            issues,
            DiagnosticCode.InvalidLineAnimation,
            IssueSeverity.Warning,
            $"不认识的缓动方式「{text}」，已按「缓出」处理。",
            location);

        return AnimationEasing.EaseOut;
    }

    private static string ToText(AnimationEasing easing) => easing switch
    {
        AnimationEasing.Linear => "linear",
        AnimationEasing.EaseIn => "easeIn",
        AnimationEasing.EaseInOut => "easeInOut",
        _ => "easeOut",
    };

    private static string ToText(BackgroundKind kind) => kind switch
    {
        BackgroundKind.Image => "image",
        BackgroundKind.Video => "video",
        _ => "color",
    };

    private static string ToText(BackgroundPlaybackMode playback) =>
        playback == BackgroundPlaybackMode.FreezeLastFrame ? "freeze" : "loop";

    private static string ToText(BackgroundFit fit) => fit switch
    {
        BackgroundFit.Cover => "cover",
        BackgroundFit.Original => "original",
        _ => "contain",
    };

    internal static Composition MapComposition(CompositionDto? dto, List<ProjectIssue> issues)
    {
        Composition defaults = new();

        if (dto is null)
        {
            Add(
                issues,
                DiagnosticCode.MissingComposition,
                IssueSeverity.Error,
                "缺少 composition 对象，已使用默认画布与帧率；请补齐后再导出。",
                "composition");

            return defaults;
        }

        return new Composition
        {
            CanvasWidth = MapCanvasSize(dto.CanvasWidth, defaults.CanvasWidth, "canvasWidth", issues),
            CanvasHeight = MapCanvasSize(dto.CanvasHeight, defaults.CanvasHeight, "canvasHeight", issues),
            FrameRate = MapFrameRate(dto.FrameRate, defaults.FrameRate, issues),
            BackgroundColor = MapBackgroundColor(dto.BackgroundColor, defaults.BackgroundColor, issues),
            OutputDurationUs = dto.OutputDurationUs,
        };
    }

    internal static LyricsDocument MapLyrics(LyricsFileDto dto, int schemaVersion, List<ProjectIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new LyricsDocument
        {
            SchemaVersion = schemaVersion,
            PrimaryBranchId = dto.PrimaryBranchId ?? string.Empty,
            Branches = dto.Branches is null ? [] : [.. dto.Branches.Select((branch, branchIndex) => new LyricBranch
            {
                Id = branch.Id ?? string.Empty, Name = branch.Name ?? string.Empty, Language = branch.Language,
                Role = ParseBranchEnum<LyricBranchRole>(branch.Role),
                TimingMode = ParseBranchEnum<LyricBranchTimingMode>(branch.TimingMode),
                StyleOverride = MapStyleOverride(branch.StyleOverride, $"lyrics.branches[{branchIndex}].styleOverride", issues),
                Lines = branch.Lines is null ? [] : [.. branch.Lines.Select((line, index) => MapLine(line, index, issues, $"lyrics.branches[{branchIndex}].lines"))],
            })],
        };
    }

    private static T ParseBranchEnum<T>(string? text) where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: true, out T result) ? result : (T)Enum.ToObject(typeof(T), -1);

    private static int MapCanvasSize(int? value, int fallback, string fieldName, List<ProjectIssue> issues)
    {
        if (value is int size)
        {
            return size;
        }

        Add(
            issues,
            DiagnosticCode.InvalidCanvasSize,
            IssueSeverity.Error,
            $"缺少 {fieldName}，已暂时使用 {fallback}。",
            "composition." + fieldName);

        return fallback;
    }

    private static FrameRate MapFrameRate(FrameRateDto? dto, FrameRate fallback, List<ProjectIssue> issues)
    {
        if (dto?.Numerator is int numerator && dto.Denominator is int denominator)
        {
            if (numerator > 0 && denominator > 0)
            {
                return new FrameRate(numerator, denominator);
            }

            Add(
                issues,
                DiagnosticCode.InvalidFrameRateDefinition,
                IssueSeverity.Error,
                $"帧率的分子与分母必须为正数，当前为 {numerator}/{denominator}；" +
                $"已暂时使用 {fallback}。",
                "composition.frameRate");
        }
        else
        {
            Add(
                issues,
                DiagnosticCode.InvalidFrameRateDefinition,
                IssueSeverity.Error,
                $"缺少帧率的 numerator/denominator，已暂时使用 {fallback}。",
                "composition.frameRate");
        }

        return fallback;
    }

    private static RgbaColor MapBackgroundColor(string? text, RgbaColor fallback, List<ProjectIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            // 背景色缺失不阻断：docs/development.md §6.2 的示例未包含该字段，默认不透明黑是安全的。
            return fallback;
        }

        if (RgbaColor.TryParse(text, out RgbaColor color))
        {
            return color;
        }

        Add(
            issues,
            DiagnosticCode.InvalidBackgroundColor,
            IssueSeverity.Error,
            $"无法解析背景色 '{text}'，期望 '#RRGGBB' 或 '#RRGGBBAA'；已暂时使用 {fallback.ToHex()}。",
            "composition.backgroundColor");

        return fallback;
    }

    /// <summary>读取样式基线；缺失或非法的字段退回默认值并报诊断。</summary>
    internal static LyricStyle MapLyricStyle(LyricStyleDto? dto, List<ProjectIssue> issues,
        bool allowUnsupportedGradients = false)
    {
        LyricStyle fallback = LyricStyle.Default;
        if (dto is null)
        {
            return fallback;
        }

        TypographySpec typography = dto.Typography is { } typographyDto
            ? MapTypographySpec(typographyDto, fallback.Typography, "lyricStyle.typography", issues)
            : MissingStyleObject(fallback.Typography, "lyricStyle.typography", issues);
        FillSpec unsung = new() { Material = ReadMaterial(dto.UnsungFill, "lyricStyle.unsungFill", issues, allowUnsupportedGradients) };
        FillSpec sung = new() { Material = ReadMaterial(dto.SungFill, "lyricStyle.sungFill", issues, allowUnsupportedGradients) };

        double ratio = dto.VerticalCenterRatio ?? fallback.VerticalCenterRatio;
        if (ratio is < 0d or > 1d)
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error,
                "纵向中心比例必须落在 0 到 1 之间。", "lyricStyle.verticalCenterRatio");
            ratio = fallback.VerticalCenterRatio;
        }

        return new LyricStyle
        {
            Typography = typography,
            UnsungFill = unsung,
            SungFill = sung,
            Stroke = dto.Stroke is null ? null : MapStrokeSpec(dto.Stroke, "lyricStyle.stroke", issues, allowUnsupportedGradients),
            Shadow = dto.Shadow is null ? null : MapShadowSpec(dto.Shadow, "lyricStyle.shadow", issues),
            VerticalCenterRatio = ratio,
            HorizontalAlignment = MapAlignment(dto.HorizontalAlignment, fallback.HorizontalAlignment,
                "lyricStyle.horizontalAlignment", issues),
            Opacity = MapRatio(dto.Opacity, fallback.Opacity, "lyricStyle.opacity", issues),
            Scale = MapPositive(dto.Scale, fallback.Scale, "lyricStyle.scale", issues),
        };
    }

    private static T MissingStyleObject<T>(T fallback, string location, List<ProjectIssue> issues)
    {
        return fallback;
    }

    private static TypographySpec MapTypographySpec(
        TypographySpecDto dto,
        TypographySpec fallback,
        string location,
        List<ProjectIssue> issues)
    {
        return new TypographySpec
        {
            FontFamilyName = dto.FontFamilyName,
            FallbackFamilyNames = MapFallbackFamilyNames(
                    dto.FallbackFamilyNames, location + ".fallbackFamilyNames", issues)
                ?? fallback.FallbackFamilyNames,
            FontSizePx = MapPositive(dto.FontSizePx, fallback.FontSizePx, location + ".fontSizePx", issues),
            FontWeight = MapFontWeight(dto.FontWeight, fallback.FontWeight, location + ".fontWeight", issues),
            IsBold = dto.IsBold ?? fallback.IsBold,
            IsItalic = dto.IsItalic ?? fallback.IsItalic,
            LetterSpacingPx = MapFinite(dto.LetterSpacingPx, fallback.LetterSpacingPx, location + ".letterSpacingPx", issues),
        };
    }

    /// <summary>读取样式覆盖；没有任何覆盖时返回 <see langword="null"/>。</summary>
    internal static LyricStyleOverride? MapStyleOverride(
        LyricStyleOverrideDto? dto,
        string location,
        List<ProjectIssue> issues)
    {
        if (dto is null) return null;
        LyricStyleOverride result = new()
        {
            Typography = MapTypographyOverride(dto.Typography, location + ".typography", issues),
            UnsungFill = MapOptionalFillOverride(dto.UnsungFill, location + ".unsungFill", issues),
            SungFill = MapOptionalFillOverride(dto.SungFill, location + ".sungFill", issues),
            Stroke = MapOptionalStrokeOverride(dto.Stroke, location + ".stroke", issues),
            Shadow = MapOptionalShadowOverride(dto.Shadow, location + ".shadow", issues),
            VerticalCenterRatio = dto.VerticalCenterRatio,
            HorizontalAlignment = MapOptionalAlignment(dto.HorizontalAlignment, location + ".horizontalAlignment", issues),
            Opacity = dto.Opacity,
            Scale = dto.Scale,
        };
        return result.IsEmpty ? null : result;
    }




    private static OptionalValue<ShadowSpecOverride?> MapOptionalShadowOverride(
        System.Text.Json.JsonElement value, string location, List<ProjectIssue> issues)
    {
        if (value.ValueKind == System.Text.Json.JsonValueKind.Undefined) return default;
        if (value.ValueKind == System.Text.Json.JsonValueKind.Null) return OptionalValue.Set<ShadowSpecOverride?>(null);
        try
        {
            ShadowSpecOverrideDto dto = System.Text.Json.JsonSerializer.Deserialize<ShadowSpecOverrideDto>(value, ProjectJson.Options)!;
            return OptionalValue.Set<ShadowSpecOverride?>(new ShadowSpecOverride
            {
                Color = MapOptionalStyleColor(dto.Color, location + ".color", issues),
                Opacity = dto.Opacity,
                AngleDegrees = dto.AngleDegrees,
                DistancePx = dto.DistancePx,
                BlurRadiusPx = dto.BlurRadiusPx,
            });
        }
        catch (System.Text.Json.JsonException exception)
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error,
                "阴影覆盖不是有效对象：" + exception.Message, location);
            return default;
        }
    }

    private static GradientSpec? MapGradientSpec(
        GradientSpecDto? dto,
        string location,
        List<ProjectIssue> issues,
        bool allowUnsupportedGradients = false)
    {
        if (dto is null)
        {
            return null;
        }

        if (!TryMapEnum(dto.Kind, GradientKind.Linear, location + ".kind", issues, out GradientKind kind)
            || !TryMapEnum(dto.CoordinateSpace, GradientCoordinateSpace.Line, location + ".coordinateSpace", issues, out GradientCoordinateSpace coordinateSpace)
            || !TryMapEnum(dto.ColorSpace, GradientColorSpace.LinearRgb, location + ".colorSpace", issues, out GradientColorSpace colorSpace)
            || !TryMapEnum(dto.HuePath, GradientHuePath.Shorter, location + ".huePath", issues, out GradientHuePath huePath))
        {
            return null;
        }

        if (dto.AngleDegrees is { } angle && !double.IsFinite(angle))
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "渐变方向必须是有限数。", location + ".angleDegrees");
            return null;
        }

        double centerX = dto.CenterX ?? 0.5d;
        double centerY = dto.CenterY ?? 0.5d;
        double radiusX = dto.RadiusX ?? 0.5d;
        double radiusY = dto.RadiusY ?? 0.5d;
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY)
            || !double.IsFinite(radiusX) || !double.IsFinite(radiusY))
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error,
                "径向渐变几何参数必须是有限数。", location);
            return null;
        }

        if (dto.Stops is null)
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "渐变至少需要两个停止点。", location + ".stops");
            return null;
        }

        List<GradientStop> stops = [];

        for (int index = 0; index < dto.Stops.Count; index++)
        {
            GradientStopDto stop = dto.Stops[index];
            string stopLocation = $"{location}.stops[{index}]";

            if (stop.Offset is not { } offset || !double.IsFinite(offset) || offset is < 0d or > 1d)
            {
                Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "渐变停止点位置必须在 0 到 1 之间。", stopLocation + ".offset");
                return null;
            }

            if (string.IsNullOrWhiteSpace(stop.Color) || !RgbaColor.TryParse(stop.Color, out RgbaColor color))
            {
                Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "渐变停止点颜色无效。", stopLocation + ".color");
                return null;
            }

            stops.Add(new GradientStop(offset, color));
        }

        try
        {
            GradientSpec gradient = GradientSpec.Create(
                stops,
                kind,
                dto.AngleDegrees ?? 0d,
                coordinateSpace,
                colorSpace,
                huePath,
                centerX,
                centerY,
                radiusX,
                radiusY);
            if (!allowUnsupportedGradients && !GradientSupport.IsSupported(gradient))
            {
                Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error,
                    $"渐变组合当前不可用：{GradientSupport.UnsupportedReason(gradient)}", location);
                return null;
            }

            return gradient;
        }
        catch (ArgumentException exception)
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, $"渐变描述无效：{exception.Message}", location);
            return null;
        }
    }

    private static System.Collections.Immutable.ImmutableArray<string>? MapFallbackFamilyNames(
        List<string>? names,
        string location,
        List<ProjectIssue> issues)
    {
        if (names is null)
        {
            return null;
        }

        if (names.Any(string.IsNullOrWhiteSpace))
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error,
                "字体回退族名不能是空白。", location);
        }

        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error,
                "字体回退族名不能重复。", location);
        }

        return [.. names];
    }

    private static ShadowSpec? MapShadowSpec(
        ShadowSpecDto? dto,
        string location,
        List<ProjectIssue> issues)
    {
        if (dto is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(dto.Color) || !RgbaColor.TryParse(dto.Color, out RgbaColor color))
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "阴影颜色无效。", location + ".color");
            return null;
        }

        ShadowSpec result = new()
        {
            Color = color,
            Opacity = dto.Opacity ?? 1f,
            AngleDegrees = dto.AngleDegrees ?? 45d,
            DistancePx = dto.DistancePx ?? 4f,
            BlurRadiusPx = dto.BlurRadiusPx ?? 8f,
        };

        if (!result.IsValid)
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "阴影参数无效。", location);
            return null;
        }

        return result;
    }

    private static bool TryMapEnum<TEnum>(
        string? value,
        TEnum fallback,
        string location,
        List<ProjectIssue> issues,
        out TEnum result)
        where TEnum : struct, Enum
    {
        if (value is null)
        {
            result = fallback;
            return true;
        }

        if (Enum.TryParse(value, ignoreCase: true, out result) && Enum.IsDefined(result))
        {
            return true;
        }

        Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, $"渐变枚举值「{value}」无法识别。", location);
        result = fallback;
        return false;
    }

    private static RgbaColor? MapOptionalStyleColor(string? text, string location, List<ProjectIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (RgbaColor.TryParse(text, out RgbaColor color))
        {
            return color;
        }

        Add(
            issues,
            DiagnosticCode.InvalidStyleValue,
            IssueSeverity.Error,
            $"无法解析样式颜色 '{text}'，期望 '#RRGGBB' 或 '#RRGGBBAA'；该覆盖已被忽略。",
            location);

        return null;
    }

    private static int MapFontWeight(int? value, int fallback, string location, List<ProjectIssue> issues)
    {
        if (value is null || value is >= 100 and <= 900) return value ?? fallback;
        Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error,
            $"{location} 必须在 100 到 900 之间；已暂时使用 {fallback}。", location);
        return fallback;
    }

    private static float MapFinite(float? value, float fallback, string location, List<ProjectIssue> issues)
    {
        if (value is null || float.IsFinite(value.Value)) return value ?? fallback;
        Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error,
            $"{location} 必须是有限数；已暂时使用 {fallback}。", location);
        return fallback;
    }

    private static float MapRatio(float? value, float fallback, string location, List<ProjectIssue> issues)
    {
        if (value is null || value is >= 0f and <= 1f) return value ?? fallback;
        Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, $"{location} 必须在 0 到 1 之间；已暂时使用 {fallback}。", location);
        return fallback;
    }

    /// <summary>读水平对齐方式；不认识的写法回退到兜底值并报出来。</summary>
    /// <remarks>
    /// 枚举在 JSON 里是字符串，所以"文件里写了个不认识的词"是可能的。
    /// 与其它字段一致：<b>不猜</b>——回退到默认的居中，并明确告诉用户文件里写的是什么。
    /// </remarks>
    private static LyricHorizontalAlignment MapAlignment(
        string? value,
        LyricHorizontalAlignment fallback,
        string location,
        List<ProjectIssue> issues)
    {
        if (value is null)
        {
            return fallback;
        }

        if (Enum.TryParse(value, ignoreCase: true, out LyricHorizontalAlignment parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        Add(
            issues,
            DiagnosticCode.InvalidStyleValue,
            IssueSeverity.Error,
            $"{location} 不是已知的对齐方式（读到「{value}」）；已暂时使用 {fallback}。",
            location);

        return fallback;
    }

    private static LyricHorizontalAlignment? MapOptionalAlignment(
        string? value,
        string location,
        List<ProjectIssue> issues) =>
        value is null ? null : MapAlignment(value, LyricHorizontalAlignment.Center, location, issues);

    private static float MapPositive(float? value, float fallback, string location, List<ProjectIssue> issues)
    {
        if (value is null || value > 0f) return value ?? fallback;
        Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, $"{location} 必须为正数；已暂时使用 {fallback}。", location);
        return fallback;
    }

    private static LyricLine MapLine(LyricLineDto dto, int index, List<ProjectIssue> issues, string lineRoot)
    {
        string path = $"{lineRoot}[{index}]";
        string id = dto.Id ?? string.Empty;

        if (string.IsNullOrWhiteSpace(id))
        {
            id = $"line-{index + 1:D3}";

            Add(
                issues,
                DiagnosticCode.MissingIdentifier,
                IssueSeverity.Error,
                $"缺少行标识，已暂时使用 '{id}'。",
                path + ".id");
        }

        string text = dto.Text ?? string.Empty;

        if (dto.Text is null)
        {
            Add(
                issues,
                DiagnosticCode.MissingText,
                IssueSeverity.Error,
                "缺少行文本（text），已暂时使用空字符串。",
                path + ".text");
        }

        TimeRange? time = MapTimeRange(dto.StartUs, dto.EndUs, path, issues);

        return new LyricLine
        {
            Id = id,
            PrimaryLineId = dto.PrimaryLineId,
            RubyAnnotations = dto.RubyAnnotations is null ? [] : [.. dto.RubyAnnotations.Select((ruby, rubyIndex) => MapRuby(ruby, path, rubyIndex, issues))],
            Text = text,
            Time = time,
            TimingStatus = MapTimingStatus(dto.TimingState, time is not null, path, issues),
            StyleOverride = MapStyleOverride(dto.StyleOverride, path + ".styleOverride", issues),
            Tokens = dto.Tokens is null
                ? []
                : [.. dto.Tokens.Select((token, tokenIndex) => MapToken(token, path, tokenIndex, issues))],
        };
    }

    private static RubyAnnotation MapRuby(RubyAnnotationDto dto, string linePath, int index, List<ProjectIssue> issues)
    {
        string path = $"{linePath}.rubyAnnotations[{index}]";
        string id = dto.Id ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = $"ruby-{index + 1:D3}";
            Add(issues, DiagnosticCode.MissingIdentifier, IssueSeverity.Error,
                $"缺少注音标识，已暂时使用 '{id}'。", path + ".id");
        }

        string text = dto.Text ?? string.Empty;
        if (dto.Text is null)
            Add(issues, DiagnosticCode.MissingText, IssueSeverity.Error,
                "缺少注音文本（text），已暂时使用空字符串。", path + ".text");

        ImmutableArray<RubyTextSegment> segments = dto.Segments is null
            ? (string.IsNullOrEmpty(text) ? [] : [RubyTextSegment.Create(RubyTextSegment.CompatibilityId(id), text)])
            : [.. dto.Segments.Select((segment, segmentIndex) => MapRubySegment(segment, id, path, segmentIndex, issues))];

        RubyAnnotation result = new()
        {
            Id = id,
            BaseTokenId = dto.BaseTokenId ?? string.Empty,
            Text = text,
            Segments = segments,
            Language = dto.Language,
            RelativeScale = dto.RelativeScale,
            GapPx = dto.GapPx,
            Alignment = ParseBranchEnum<RubyAlignment>(dto.Alignment),
        };

        if (dto.Segments is not null
            && !string.Equals(text, string.Concat(segments.Select(segment => segment.Text)), StringComparison.Ordinal))
        {
            Add(issues, DiagnosticCode.InvalidRubyAnnotation, IssueSeverity.Error,
                "注音 text 必须等于 segments 文本拼接结果；保留原 text 并报告不一致。", path + ".text");
        }

        return result;
    }

    private static RubyTextSegment MapRubySegment(
        RubyTextSegmentDto dto,
        string rubyId,
        string rubyPath,
        int index,
        List<ProjectIssue> issues)
    {
        string path = $"{rubyPath}.segments[{index}]";
        string id = dto.Id ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            string candidate = $"{rubyId}-segment-{index + 1}";
            id = candidate.Length <= 118
                ? candidate
                : $"{RubyTextSegment.CompatibilityId(rubyId)}-{index + 1}";
            Add(issues, DiagnosticCode.MissingIdentifier, IssueSeverity.Error,
                $"缺少注音片段标识，已暂时使用 '{id}'。", path + ".id");
        }

        string text = dto.Text ?? string.Empty;
        if (dto.Text is null)
            Add(issues, DiagnosticCode.MissingText, IssueSeverity.Error,
                "缺少注音片段文本（text），已暂时使用空字符串。", path + ".text");

        TimeRange? time = MapTimeRange(dto.StartUs, dto.EndUs, path, issues);
        TimingStatus status = MapTimingStatus(dto.TimingState, time is not null, path, issues);
        return new RubyTextSegment { Id = id, Text = text, Time = time, TimingStatus = status };
    }

    private static ScenePlacement? MapPlacement(ScenePlacementDto? dto, string path, List<ProjectIssue> issues)
    {
        if (dto is null) return null;
        bool validAnchor = Enum.TryParse(dto.Anchor, ignoreCase: true, out ScenePlacementAnchor anchor)
            && Enum.IsDefined(anchor);
        ScenePlacement placement = new()
        {
            Position = new(dto.X, dto.Y), Anchor = validAnchor ? anchor : (ScenePlacementAnchor)(-1),
            RotationDegrees = dto.RotationDegrees, Scale = dto.Scale,
        };
        if (!placement.IsValid)
            Add(issues, DiagnosticCode.InvalidLinePlacement, IssueSeverity.Error,
                "行位置必须使用有效锚点、0–1 坐标、有限角度和正缩放。", path);
        return placement;
    }

    private static LyricToken MapToken(
        LyricTokenDto dto,
        string linePath,
        int index,
        List<ProjectIssue> issues)
    {
        string path = $"{linePath}.tokens[{index}]";
        string id = dto.Id ?? string.Empty;

        if (string.IsNullOrWhiteSpace(id))
        {
            id = $"token-{index + 1:D3}";

            Add(
                issues,
                DiagnosticCode.MissingIdentifier,
                IssueSeverity.Error,
                $"缺少元素标识，已暂时使用 '{id}'。",
                path + ".id");
        }

        string text = dto.Text ?? string.Empty;

        if (dto.Text is null)
        {
            Add(
                issues,
                DiagnosticCode.MissingText,
                IssueSeverity.Error,
                "缺少元素文本（text），已暂时使用空字符串。",
                path + ".text");
        }

        TimeRange? time = MapTimeRange(dto.StartUs, dto.EndUs, path, issues);

        return new LyricToken
        {
            Id = id,
            Text = text,
            Time = time,
            TimingStatus = MapTimingStatus(dto.TimingState, time is not null, path, issues),
            StyleOverride = MapStyleOverride(dto.StyleOverride, path + ".styleOverride", issues),
        };
    }

    private static TimeRange? MapTimeRange(
        long? startUs,
        long? endUs,
        string path,
        List<ProjectIssue> issues)
    {
        bool hasStart = startUs.HasValue;
        bool hasEnd = endUs.HasValue;

        if (!hasStart && !hasEnd)
        {
            return null;
        }

        if (hasStart != hasEnd)
        {
            Add(
                issues,
                DiagnosticCode.IncompleteTimeRange,
                IssueSeverity.Error,
                $"时间区间不完整：startUs 与 endUs 必须同时出现（startUs={Describe(startUs)}，endUs={Describe(endUs)}）。",
                path);

            return null;
        }

        // 注意：这里刻意不调用 TimeRange.Create。非法区间（例如终点不大于起点）
        // 必须能被读进内存并由校验器报告，而不是让读取直接失败。
        return new TimeRange(startUs!.Value, endUs!.Value);
    }

    private static TimingStatus MapTimingStatus(
        string? state,
        bool hasTime,
        string path,
        List<ProjectIssue> issues)
    {
        TimingStatus fallback = hasTime ? TimingStatus.Imported : TimingStatus.Missing;

        if (string.IsNullOrWhiteSpace(state))
        {
            if (hasTime)
            {
                Add(
                    issues,
                    DiagnosticCode.MissingTimingState,
                    IssueSeverity.Warning,
                    "有时间数据但缺少 timingState；按最保守的'导入'处理，请用户确认后再导出。",
                    path + ".timingState");
            }

            return fallback;
        }

        // Enum.TryParse 会把 "3" 这类数字文本也解析成功，因此先排除数字形态，
        // 避免非法文件被静默接受而看不出问题。
        if (!char.IsAsciiDigit(state.TrimStart()[0])
            && Enum.TryParse(state, ignoreCase: true, out TimingStatus parsed))
        {
            return parsed;
        }

        Add(
            issues,
            DiagnosticCode.UnknownTimingState,
            IssueSeverity.Error,
            $"未知的 timingState '{state}'；已按'{fallback.ToDisplayName()}'处理。",
            path + ".timingState");

        return fallback;
    }

    private static string Describe(long? value) =>
        value.HasValue
            ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "<缺失>";
}
