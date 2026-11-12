using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using static MakoKaraGenerator.Core.Diagnostics.DiagnosticList;

namespace MakoKaraGenerator.Core.Validation;

/// <summary>
/// 项目校验器：把"数据是否可用"的判断集中在一处（roadmap CORE-05）。
/// </summary>
/// <remarks>
/// <para>
/// 覆盖 docs/development.md §6.3 列出的检查项中，在本阶段已有对应数据模型的那些：
/// schema 版本、标识重复、空文本、元素越出行范围、画布与帧率有效性。
/// 样式引用、脚本、字体、音频与背景资源的存在性检查要等到对应模型出现后再加入
/// （见 ADR-0005）。
/// </para>
/// <para>
/// 校验器<b>不修改</b>项目数据。它只报告问题：docs/development.md §19 禁止
/// "静默修正"，尤其是不得把逐行时间平均分配后当作精确逐字时间。
/// </para>
/// </remarks>
public static partial class ProjectValidator
{
    /// <summary>按指定上下文校验一个项目。</summary>
    /// <param name="project">待校验的项目。</param>
    /// <param name="context">校验发生的位置，影响部分检查的严重程度。</param>
    /// <returns>诊断报告；没有任何问题时为空报告。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    public static ValidationReport Validate(Project project, ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(project);

        List<ProjectIssue> issues = [];

        // 两份文件各有自己的版本线与当前版本，不能拿一个去量另一个。
        ValidateSchemaVersion(project.SchemaVersion, Project.CurrentSchemaVersion, "project.schemaVersion", issues);
        ValidateSchemaVersion(
            project.Lyrics.SchemaVersion,
            LyricsDocument.CurrentSchemaVersion,
            "lyrics.schemaVersion",
            issues);
        ValidateComposition(project.Composition, context, issues);
        ValidateStyle(project.LyricStyle, "lyricStyle", issues);
        ValidateLineAnimation(project.EnterAnimation, "enterAnimation", issues);
        ValidateLyrics(project.Lyrics, project.LyricPresentation, context, issues);
        ValidatePresentation(project, issues);
        ValidateEffects(project, issues);
        ValidatePresetApplications(project, issues);

        return ValidationReport.From(issues);
    }

    private static void ValidateSchemaVersion(
        int schemaVersion,
        int supportedVersion,
        string location,
        List<ProjectIssue> issues)
    {
        if (schemaVersion != supportedVersion)
        {
            Add(
                issues,
                DiagnosticCode.UnsupportedSchemaVersion,
                IssueSeverity.Error,
                $"schema 版本 {schemaVersion} 不受支持，当前支持的版本为 {supportedVersion}。",
                location);
        }
    }

    private static void ValidateComposition(
        Composition composition,
        ValidationContext context,
        List<ProjectIssue> issues)
    {
        if (composition.CanvasWidth <= 0)
        {
            Add(
                issues,
                DiagnosticCode.InvalidCanvasSize,
                IssueSeverity.Error,
                $"画布宽度必须为正数，当前为 {composition.CanvasWidth}。",
                "composition.canvasWidth");
        }

        if (composition.CanvasHeight <= 0)
        {
            Add(
                issues,
                DiagnosticCode.InvalidCanvasSize,
                IssueSeverity.Error,
                $"画布高度必须为正数，当前为 {composition.CanvasHeight}。",
                "composition.canvasHeight");
        }

        long? outputDurationUs = composition.OutputDurationUs;

        if (outputDurationUs is null)
        {
            Add(
                issues,
                DiagnosticCode.MissingOutputDuration,
                context == ValidationContext.Export ? IssueSeverity.Error : IssueSeverity.Info,
                "输出时长尚未确定；导出前必须确定时长。",
                "composition.outputDurationUs");
        }
        else if (outputDurationUs <= 0)
        {
            Add(
                issues,
                DiagnosticCode.InvalidOutputDuration,
                IssueSeverity.Error,
                $"输出时长必须为正数，当前为 {outputDurationUs} 微秒。",
                "composition.outputDurationUs");
        }
    }

    private static void ValidateLyrics(LyricsDocument lyrics, LyricPresentationDefinition presentation, ValidationContext context, List<ProjectIssue> issues)
    {
        if (lyrics.PrimaryBranch is not { Role: LyricBranchRole.Primary }
            || lyrics.Branches.Count(entry => entry.Role == LyricBranchRole.Primary) != 1)
            Add(issues, DiagnosticCode.InvalidLyricBranch, IssueSeverity.Error,
                "文档必须明确指定唯一的主歌词分支。", "lyrics.primaryBranchId");
        HashSet<string> identifiers = new(StringComparer.Ordinal);
        HashSet<string> primaryIds = lyrics.Lines.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        for (int branchIndex = 0; branchIndex < lyrics.Branches.Length; branchIndex++)
        {
            LyricBranch branch = lyrics.Branches[branchIndex];
            string path = $"lyrics.branches[{branchIndex}]";
            ValidateIdentifier(branch.Id, identifiers, path + ".id", issues);
            if (!branch.IsValid)
                Add(issues, DiagnosticCode.InvalidLyricBranch, IssueSeverity.Error,
                    "分支名称、角色、时间策略或布局／动画参数无效。", path);
            ValidateStyleOverride(branch.StyleOverride, path + ".styleOverride", isToken: false, issues);
            for (int lineIndex = 0; lineIndex < branch.Lines.Length; lineIndex++)
            {
                LyricLine line = branch.Lines[lineIndex];
                ValidateIdentifier(line.Id, identifiers, path + ".lines.id", issues);
                foreach (LyricToken token in line.Tokens)
                    ValidateIdentifier(token.Id, identifiers, path + ".tokens.id", issues);
                if (line.PrimaryLineId is { } linkedId && (branch.Id == lyrics.PrimaryBranchId || !primaryIds.Contains(linkedId)))
                    Add(issues, DiagnosticCode.MissingLyricAssociation, IssueSeverity.Warning,
                        $"关联主歌词行 '{linkedId}' 不存在，关联行不会跟随显示。", path + ".lines.primaryLineId");
                HashSet<string> bases = new(StringComparer.Ordinal);
                for (int rubyIndex = 0; rubyIndex < line.RubyAnnotations.Length; rubyIndex++)
                {
                    RubyAnnotation ruby = line.RubyAnnotations[rubyIndex];
                    ValidateIdentifier(ruby.Id, identifiers, path + ".rubyAnnotations.id", issues);
                    for (int segmentIndex = 0; segmentIndex < ruby.Segments.Length; segmentIndex++)
                    {
                        RubyTextSegment segment = ruby.Segments[segmentIndex];
                        string segmentPath = path + $".lines[{lineIndex}].rubyAnnotations[{rubyIndex}].segments[{segmentIndex}]";
                        ValidateIdentifier(segment.Id, identifiers, segmentPath + ".id", issues);
                        if (!segment.IsValid)
                            Add(issues, DiagnosticCode.InvalidRubyAnnotation, IssueSeverity.Error,
                                "注音片段标识、文本或时间状态无效。", segmentPath);
                        // Missing is a supported token fallback, including for export; raw invalid times still need diagnostics.
                        if (segment.Time is not null || !segment.IsTimeStatusConsistent)
                            ValidateTime(segment.Time, segment.IsTimeStatusConsistent, context, segmentPath, issues);
                        if (segment.Time is { IsValid: true } segmentTime)
                        {
                            LyricToken? baseToken = line.Tokens.FirstOrDefault(token => token.Id == ruby.BaseTokenId);
                            if (line.Time is { IsValid: true } lineTime && (segmentTime.StartUs < lineTime.StartUs || segmentTime.EndUs > lineTime.EndUs)
                                || baseToken?.Time is { IsValid: true } tokenTime && (segmentTime.StartUs < tokenTime.StartUs || segmentTime.EndUs > tokenTime.EndUs))
                                Add(issues, DiagnosticCode.RubySegmentOutsideParent, IssueSeverity.Warning,
                                    $"注音片段 '{segment.Id}' 的独立时间超出基础元素或行；时间保留，显示仍遵守行窗口。", segmentPath);
                        }
                    }
                    if (!ruby.IsValid || !bases.Add(ruby.BaseTokenId))
                        Add(issues, DiagnosticCode.InvalidRubyAnnotation, IssueSeverity.Error,
                            "注音参数、片段文本拼接或同一基础字活动注音无效。", path + ".rubyAnnotations");
                    if (!line.Tokens.Any(token => token.Id == ruby.BaseTokenId))
                        Add(issues, DiagnosticCode.MissingLyricAssociation, IssueSeverity.Warning,
                            $"注音基础字 '{ruby.BaseTokenId}' 不存在。", path + ".rubyAnnotations.baseTokenId");
                }
            }
            ValidateBranchLyrics(new LyricsDocument { Lines = branch.Lines }, context, issues,
                followPrimary: branch.TimingMode == LyricBranchTimingMode.FollowPrimary,
                staticBranch: presentation.Resolve(branch.Id).PolicyFor(branch) == LyricBranchAnimationPolicy.Static, path + ".lines");
        }
    }

    private static void ValidateBranchLyrics(LyricsDocument lyrics, ValidationContext context, List<ProjectIssue> issues,
        bool followPrimary, bool staticBranch, string lineRoot)
    {
        if (lyrics.Lines.IsEmpty)
        {
            Add(
                issues,
                DiagnosticCode.EmptyLyrics,
                context == ValidationContext.Export ? IssueSeverity.Warning : IssueSeverity.Info,
                "歌词文档没有任何行。",
                lineRoot);

            return;
        }

        HashSet<string> identifiers = new(StringComparer.Ordinal);
        int unconfirmedCount = 0;
        bool previousLineStarted = false;
        long previousLineStartUs = 0;

        for (int lineIndex = 0; lineIndex < lyrics.Lines.Length; lineIndex++)
        {
            LyricLine line = lyrics.Lines[lineIndex];
            string linePath = $"{lineRoot}[{lineIndex}]";


            ValidateText(line.Text, context, linePath + ".text", issues);
            if (!(followPrimary && line.PrimaryLineId is not null && line.Time is null))
                ValidateTime(line.Time, line.IsTimeStatusConsistent, context, linePath, issues);
            ValidateStyleOverride(line.StyleOverride, linePath + ".styleOverride", isToken: false, issues);
            if (line.TimingStatus.IsUnconfirmed() && line.Time is not null)
            {
                // 已有时间但尚未被用户确认，需要计入导出前提示（docs/development.md §5.4）。
                unconfirmedCount++;
            }

            if (line.Time is { IsValid: true } lineTime)
            {
                if (previousLineStarted && lineTime.StartUs < previousLineStartUs)
                {
                    Add(
                        issues,
                        DiagnosticCode.LinesOutOfOrder,
                        IssueSeverity.Warning,
                        $"歌词行起始时间（{lineTime.StartUs}）早于上一行（{previousLineStartUs}）。",
                        linePath + ".startUs");
                }

                previousLineStarted = true;
                previousLineStartUs = lineTime.StartUs;
            }

            if (!staticBranch || !line.Tokens.IsEmpty)
                ValidateTokens(line, linePath, context, identifiers, ref unconfirmedCount, issues, staticBranch);
        }

        if (unconfirmedCount > 0)
        {
            Add(
                issues,
                DiagnosticCode.UnconfirmedTiming,
                context == ValidationContext.Export ? IssueSeverity.Warning : IssueSeverity.Info,
                $"有 {unconfirmedCount} 处时间仍是导入或估算值，尚未被用户确认。",
                lineRoot);
        }
    }

    /// <summary>校验一份样式（基线与行级覆盖都要求字号与纵向位置有效）。</summary>
    /// <summary>校验一条进入动画。</summary>
    /// <remarks>
    /// 时长与起始值都只是数字，但越界会让画面出现"永远半透明"或"整行飞出画布"这类怪状态，
    /// 且用户在界面上不容易看出是自己填错了，所以在打开/导入时就报出来。
    /// </remarks>
    private static void ValidateLineAnimation(LineAnimation? animation, string location, List<ProjectIssue> issues)
    {
        if (animation is null)
        {
            return;
        }

        if (!Enum.IsDefined(animation.Easing))
            Add(issues, DiagnosticCode.InvalidLineAnimation, IssueSeverity.Error, "进入动画缓动无效。", location + ".easing");

        if (animation.DurationUs < 0L || animation.DurationUs > MaxAnimationDurationUs)
        {
            Add(
                issues,
                DiagnosticCode.InvalidLineAnimation,
                IssueSeverity.Error,
                "进入动画时长必须在 0 到 10 秒之间。",
                location + ".durationUs");
        }

        if (!float.IsFinite(animation.StartOpacity) || animation.StartOpacity is < 0f or > 1f)
        {
            Add(
                issues,
                DiagnosticCode.InvalidLineAnimation,
                IssueSeverity.Error,
                "进入动画的起始不透明度必须在 0 到 1 之间。",
                location + ".startOpacity");
        }

        if (!float.IsFinite(animation.StartScale) || animation.StartScale <= 0f || animation.StartScale > MaxAnimationScale)
        {
            Add(
                issues,
                DiagnosticCode.InvalidLineAnimation,
                IssueSeverity.Error,
                "进入动画的起始缩放必须大于 0 且不超过 4。",
                location + ".startScale");
        }

        if (!float.IsFinite(animation.StartOffsetPx) || Math.Abs(animation.StartOffsetPx) > MaxAnimationOffsetPx)
        {
            Add(
                issues,
                DiagnosticCode.InvalidLineAnimation,
                IssueSeverity.Error,
                "进入动画的起始纵向偏移不得超过 10000 像素。",
                location + ".startOffsetPx");
        }
    }

    /// <summary>进入动画时长上限：10 秒。</summary>
    private const long MaxAnimationDurationUs = 10_000_000L;

    /// <summary>起始缩放上限。</summary>
    private const float MaxAnimationScale = 4f;

    /// <summary>起始纵向偏移的绝对值上限（像素）。</summary>
    private const float MaxAnimationOffsetPx = 10_000f;

    private static void ValidateStyle(LyricStyle style, string location, List<ProjectIssue> issues)
    {
        if (style.Typography is not { IsValid: true })
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "字体排版参数无效。", location + ".typography");
        ValidateAppearance(style.Unsung, location + ".unsung", issues);
        ValidateSungAppearance(style.Sung, location + ".sung", issues);
        if (style.Wipe is not { IsValid: true })
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "扫字样式无效：羽化比例须在 0 到 2 之间，高亮宽度须为正。", location + ".wipe");
        if (!double.IsFinite(style.VerticalCenterRatio) || style.VerticalCenterRatio is < 0 or > 1)
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "纵向中心比例必须是 0 到 1 之间的有限数。", location + ".verticalCenterRatio");
        if (!Enum.IsDefined(style.HorizontalAlignment))
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "水平对齐方式无效。", location + ".horizontalAlignment");
        if (!float.IsFinite(style.Opacity) || style.Opacity is < 0 or > 1)
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "不透明度必须是 0 到 1 之间的有限数。", location + ".opacity");
        if (!float.IsFinite(style.Scale) || style.Scale <= 0)
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "缩放必须是正的有限数。", location + ".scale");
    }

    /// <summary>
    /// 校验一份样式覆盖。
    /// </summary>
    /// <param name="styleOverride">覆盖。</param>
    /// <param name="location">诊断位置。</param>
    /// <param name="isToken">是否为元素级覆盖。</param>
    /// <param name="issues">诊断收集器。</param>
    /// <remarks>
    /// 元素级覆盖支持颜色族（含渐变）、描边、阴影与不透明度——这些能按元素裁切绘制；
    /// 字号、字体、纵向位置、对齐与缩放属于整行排版与整体变换，元素级套上去会改变整行度量
    /// （docs/development.md §9.4）。支持范围只由 <c>TokenStyleSupport</c> 判定，这里不重复列。
    /// 这里刻意报出来而不是默默忽略，否则用户会以为设置生效了。
    /// </remarks>
    private static void ValidateStyleOverride(
        LyricStyleOverride? styleOverride,
        string location,
        bool isToken,
        List<ProjectIssue> issues)
    {
        if (styleOverride is null || styleOverride.IsEmpty)
        {
            return;
        }

        if (styleOverride.Unsung is { } unsung)
        {
            ValidateFillOverride(unsung.Fill, location + ".unsung.fill", issues);
            if (unsung.Strokes is { } strokes) ValidateStrokes(strokes, location + ".unsung.strokes", issues);
            if (unsung.Shadow is { IsSet: true, Value: { IsValid: false } })
                Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "阴影覆盖描述无效。", location + ".unsung.shadow");
            if (unsung.Glow is { IsSet: true, Value: { IsValid: false } })
                Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "辉光覆盖描述无效。", location + ".unsung.glow");
        }

        if (styleOverride.Sung is { } sung)
        {
            ValidateFillOverride(sung.Fill, location + ".sung.fill", issues);
            if (sung.Strokes is { IsSet: true, Value.FollowsUnsung: false } strokes)
            {
                if (strokes.Value.Value is { } layers) ValidateStrokes(layers, location + ".sung.strokes", issues);
                else Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "已唱描边覆盖不能为空。", location + ".sung.strokes");
            }
            if (sung.Shadow is { IsSet: true, Value: { FollowsUnsung: false, Value: { IsValid: false } } })
                Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "阴影覆盖描述无效。", location + ".sung.shadow");
            if (sung.Glow is { IsSet: true, Value: { FollowsUnsung: false, Value: { IsValid: false } } })
                Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "辉光覆盖描述无效。", location + ".sung.glow");
        }

        if (styleOverride.Wipe is { IsValid: false })
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "扫字样式覆盖无效。", location + ".wipe");

        if (styleOverride.FontSizePx is { } fontSize && (!float.IsFinite(fontSize) || fontSize <= 0f))
        {
            Add(
                issues,
                DiagnosticCode.InvalidStyleValue,
                IssueSeverity.Error,
                $"字号覆盖必须为正，当前为 {fontSize}。",
                location + ".fontSizePx");
        }
        if (styleOverride.FontWeight is { } weight && weight is < 100 or > 900)
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "字重覆盖必须在 100 到 900 之间。", location + ".fontWeight");
        if (styleOverride.LetterSpacingPx is { } spacing && !float.IsFinite(spacing))
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "字距覆盖必须是有限数。", location + ".letterSpacingPx");

        if (styleOverride.VerticalCenterRatio is { } ratio && (!double.IsFinite(ratio) || ratio is < 0d or > 1d))
        {
            Add(
                issues,
                DiagnosticCode.InvalidStyleValue,
                IssueSeverity.Error,
                $"纵向中心比例覆盖必须落在 0 到 1 之间，当前为 {ratio}。",
                location + ".verticalCenterRatio");
        }

        if (styleOverride.Opacity is { } opacity && (!float.IsFinite(opacity) || opacity is < 0f or > 1f))
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "不透明度覆盖必须在 0 到 1 之间。", location + ".opacity");
        if (styleOverride.Scale is { } scale && (!float.IsFinite(scale) || scale <= 0f))
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "缩放覆盖必须为正数。", location + ".scale");

        if (!isToken)
        {
            return;
        }

        // 清单只有一份（TokenStyleSupport）：校验器不再自己列一遍属性名，
        // 否则界面允许、渲染忽略、校验器却放行这类组合迟早会出现。
        List<string> unsupported =
            [.. TokenStyleSupport.UnsupportedSet(styleOverride).Select(LyricStylePropertyExtensions.ToJsonName)];

        if (unsupported.Count > 0)
        {
            Add(
                issues,
                DiagnosticCode.UnsupportedTokenStyleOverride,
                IssueSeverity.Warning,
                "元素级样式覆盖支持颜色族（含渐变）与描边、阴影、不透明度——这些能按元素裁切绘制；"
                + $"以下属性属于整行排版与整体变换，元素级会被忽略：{string.Join('、', unsupported)}。"
                + "整行只整形一次，逐元素改字号/字体/位置/缩放会改变整行度量。",
                location);
        }
    }

    private static void ValidateAppearance(TextAppearance appearance, string location, List<ProjectIssue> issues)
    {
        ValidateFill(appearance.Fill, location + ".fill", issues);
        ValidateStrokes(appearance.Strokes, location + ".strokes", issues);
        if (appearance.Shadow is { IsValid: false })
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "阴影描述无效。", location + ".shadow");
        if (appearance.Glow is { IsValid: false })
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "辉光描述无效。", location + ".glow");
    }

    private static void ValidateSungAppearance(SungAppearance appearance, string location, List<ProjectIssue> issues)
    {
        ValidateFill(appearance.Fill, location + ".fill", issues);
        if (!appearance.Strokes.FollowsUnsung)
        {
            if (appearance.Strokes.Value is { } strokes) ValidateStrokes(strokes, location + ".strokes", issues);
            else Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "已唱描边不能为空。", location + ".strokes");
        }
        if (appearance.Shadow is { FollowsUnsung: false, Value.IsValid: false })
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "阴影描述无效。", location + ".shadow");
        if (appearance.Glow is { FollowsUnsung: false, Value.IsValid: false })
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "辉光描述无效。", location + ".glow");
    }

    private static void ValidateFill(FillSpec fill, string location, List<ProjectIssue> issues)
    {
        if (fill is not { IsValid: true })
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "填充无效。", location);
        else
            ValidateGradientSupport(fill.Gradient, location + ".gradient", issues);
    }

    private static void ValidateStrokes(StrokeLayers strokes, string location, List<ProjectIssue> issues)
    {
        if (!strokes.IsValid)
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error,
                $"描边列表无效：至多 {StrokeLayers.MaxCount} 层，每层宽度须为正，包材质只能用在最内层。", location);
            return;
        }
        for (int index = 0; index < strokes.Count; index++)
            ValidateGradientSupport(strokes[index].Gradient, $"{location}.{index}.material.gradient", issues);
    }

    private static void ValidateFillOverride(OptionalValue<FillSpecOverride?> value,
        string location, List<ProjectIssue> issues)
    {
        if (!value.IsSet) return;
        if (value.Value is not { } fill || fill.Material is { IsSet: true, Value: null }
            || fill.Material is { IsSet: true, Value.IsValid: false })
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error,
                "填充覆盖无效；填充对象和材质不能设置为空。", location);
        else if (fill.Gradient is { IsSet: true, Value: { } gradient })
            ValidateGradientSupport(gradient, location + ".gradient", issues);
    }

    private static void ValidateGradientSupport(
        GradientSpec? gradient,
        string location,
        List<ProjectIssue> issues)
    {
        string? reason = GradientSupport.UnsupportedReason(gradient);
        if (reason is null)
        {
            return;
        }

        Add(
            issues,
            DiagnosticCode.InvalidStyleValue,
            IssueSeverity.Error,
            $"渐变组合当前不可用：{reason}",
            location);
    }

    private static void ValidateTokens(
        LyricLine line,
        string linePath,
        ValidationContext context,
        HashSet<string> identifiers,
        ref int unconfirmedCount,
        List<ProjectIssue> issues, bool staticBranch)
    {
        if (line.Tokens.IsEmpty)
        {
            Add(
                issues,
                DiagnosticCode.UntokenizedLine,
                IssueSeverity.Info,
                $"歌词行 '{line.Id}' 尚未拆分为文本元素。",
                linePath + ".tokens");

            return;
        }

        string concatenatedText = string.Empty;
        TimeRange? previousTokenTime = null;

        for (int tokenIndex = 0; tokenIndex < line.Tokens.Length; tokenIndex++)
        {
            LyricToken token = line.Tokens[tokenIndex];
            string tokenPath = $"{linePath}.tokens[{tokenIndex}]";


            ValidateText(token.Text, context, tokenPath + ".text", issues);
            if (!staticBranch || token.Time is not null)
                ValidateTime(token.Time, token.IsTimeStatusConsistent, context, tokenPath, issues);
            ValidateStyleOverride(token.StyleOverride, tokenPath + ".styleOverride", isToken: true, issues);

            concatenatedText += token.Text;

            if (token.TimingStatus.IsUnconfirmed() && token.Time is not null)
            {
                unconfirmedCount++;
            }

            if (token.Time is not { IsValid: true } tokenTime)
            {
                continue;
            }

            if (line.Time is { IsValid: true } lineTime
                && (tokenTime.StartUs < lineTime.StartUs || tokenTime.EndUs > lineTime.EndUs))
            {
                Add(
                    issues,
                    DiagnosticCode.ElementOutsideLine,
                    IssueSeverity.Warning,
                    $"元素 '{token.Id}' 的时间 {tokenTime} 超出所属行 '{line.Id}' 的时间 {lineTime}。",
                    tokenPath);
            }

            if (previousTokenTime is { IsValid: true } previous && previous.EndUs > tokenTime.StartUs)
            {
                Add(
                    issues,
                    DiagnosticCode.OverlappingElements,
                    IssueSeverity.Warning,
                    $"元素 '{token.Id}' 的起点（{tokenTime.StartUs}）早于前一元素的终点（{previous.EndUs}），时间重叠。",
                    tokenPath + ".startUs");
            }

            previousTokenTime = tokenTime;
        }

        if (!string.Equals(concatenatedText, line.Text, StringComparison.Ordinal))
        {
            Add(
                issues,
                DiagnosticCode.TokenTextMismatch,
                IssueSeverity.Warning,
                $"元素文本拼接结果 '{concatenatedText}' 与行文本 '{line.Text}' 不一致。",
                linePath + ".text");
        }
    }

    /// <summary>
    /// 校验标识唯一性。
    /// </summary>
    /// <remarks>
    /// 行与元素<b>共用同一个命名空间</b>：文档内所有标识必须互不相同，而不只是同一层级内唯一。
    /// 理由：选择、撤销栈与缓存键都会按标识引用对象，跨层级重名会让这些引用变得歧义。
    /// 标识由程序生成（<c>line-001</c> / <c>token-001</c> 风格），碰撞本身就意味着缺陷，
    /// 所以这里选择报告而不是宽容接受。
    /// </remarks>
    private static void ValidateIdentifier(
        string id,
        HashSet<string> seen,
        string location,
        List<ProjectIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            Add(issues, DiagnosticCode.MissingIdentifier, IssueSeverity.Error, "标识不能为空。", location);
            return;
        }

        if (!seen.Add(id))
        {
            Add(
                issues,
                DiagnosticCode.DuplicateIdentifier,
                IssueSeverity.Error,
                $"标识 '{id}' 重复。",
                location);
        }
    }

    private static void ValidateText(
        string text,
        ValidationContext context,
        string location,
        List<ProjectIssue> issues)
    {
        if (text.Length != 0)
        {
            return;
        }

        Add(
            issues,
            DiagnosticCode.EmptyText,
            context == ValidationContext.Export ? IssueSeverity.Error : IssueSeverity.Warning,
            "文本为空。",
            location);
    }

    private static void ValidateTime(
        TimeRange? time,
        bool isTimeStatusConsistent,
        ValidationContext context,
        string path,
        List<ProjectIssue> issues)
    {
        if (time is { } range)
        {
            if (!range.IsValid)
            {
                Add(
                    issues,
                    DiagnosticCode.InvalidTimeRange,
                    IssueSeverity.Error,
                    $"时间区间 {range} 非法：起点必须非负，终点必须大于起点。",
                    path);
            }
        }
        else
        {
            Add(
                issues,
                DiagnosticCode.MissingTime,
                context == ValidationContext.Export ? IssueSeverity.Error : IssueSeverity.Info,
                "时间缺失。",
                path);
        }

        if (!isTimeStatusConsistent)
        {
            Add(
                issues,
                DiagnosticCode.TimeStatusMismatch,
                IssueSeverity.Error,
                "时间数据与时间状态不相符。",
                path);
        }
    }
}
