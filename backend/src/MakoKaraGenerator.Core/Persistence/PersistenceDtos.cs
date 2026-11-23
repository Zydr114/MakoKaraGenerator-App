namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// <c>project.json</c> 的磁盘表示。
/// </summary>
/// <remarks>
/// <para>
/// 持久化 DTO 与领域模型<b>刻意分离</b>，原因有三：
/// </para>
/// <list type="number">
///   <item>领域模型对非法数据是"可表示但不默认合法"的，而 DTO 需要能表达"字段缺失"
///         这种模型里不存在的状态，才能给出可读诊断（roadmap CORE-05）；</item>
///   <item><c>Project</c> 的 <c>required</c> 成员会让 <c>System.Text.Json</c> 在字段缺失时直接抛异常，
///         无法产生逐项报告；</item>
///   <item>schema 迁移（CORE-06）只需要改 DTO 与映射，不必触碰领域语义。</item>
/// </list>
/// <para>
/// 因此这里的属性全部是可空类型，字段名与 docs/development.md §6.2 的示例保持一致。
/// </para>
/// </remarks>
internal sealed class ProjectFileDto
{
    public string? ApplicationVersion { get; set; }

    public int? SchemaVersion { get; set; }

    /// <summary>不可变歌词修订的规范内容地址（schema v22 起）。</summary>
    public string? LyricsFile { get; set; }

    /// <summary>严格的固定效果闭包入口，schema23必填。</summary>
    public System.Text.Json.JsonElement? Effects { get; set; }

    /// <summary>每对象一个固定组合预设、输入映射和实例身份，schema27必填。</summary>
    public System.Text.Json.JsonElement? PresetApplications { get; set; }

    /// <summary>完整媒体描述，schema24必填。</summary>
    public System.Text.Json.JsonElement? Media { get; set; }

    /// <summary>明确音频分析状态及不可变修订引用。</summary>
    public System.Text.Json.JsonElement? AudioAnalysis { get; set; }

    public string? Name { get; set; }

    public CompositionDto? Composition { get; set; }

    /// <summary>歌词样式基线（schema v2 起）。</summary>
    public LyricStyleDto? LyricStyle { get; set; }

    /// <summary>基线样式来自哪个预设（schema v2 起）。仅作来源信息。</summary>
    public string? LyricStylePresetId { get; set; }

    /// <summary>音频文件路径（schema v3 起）。音频在工程目录内时是相对路径。</summary>
    public string? AudioPath { get; set; }

    /// <summary>背景源（schema v6 起）。旧文件没有它就是"只有背景色"。</summary>
    public BackgroundDto? Background { get; set; }

    public LyricPresentationDto? LyricPresentation { get; set; }
}

/// <summary>
/// 背景源的持久化形态（schema v6 起；v8 起带视频的播放方式）。
/// </summary>
/// <remarks>
/// 字段都是文本：<c>kind</c> 取 <c>color</c>/<c>image</c>/<c>video</c>，<c>fit</c> 取
/// <c>contain</c>/<c>cover</c>/<c>original</c>，<c>playback</c> 取 <c>loop</c>/<c>freeze</c>。
/// 用文本而不是数字是为了让文件可读、可手改；读到不认识的写法时回退到默认并报告，
/// 而不是让整个工程打不开。
/// </remarks>
internal sealed class BackgroundDto
{
    public string? Kind { get; set; }

    public string? Path { get; set; }

    public string? Fit { get; set; }

    /// <summary>视频短于音频时的时间策略（schema v8 起）。</summary>
    public string? Playback { get; set; }
}

/// <summary><c>project.json</c> 中的歌词样式基线（schema v31 起为三层结构）。</summary>
/// <remarks>
/// 字形与布局的旧字段保留原诊断；外观的材质、描边列表与已唱「跟随」判别对象必须完整，非法结构取消读取。
/// 源材质颜色严格采用 <c>#RRGGBBAA</c>。
/// </remarks>
internal sealed class LyricStyleDto
{
    /// <summary>规范字体对象（schema v16 起）。</summary>
    public TypographySpecDto? Typography { get; set; }
    /// <summary>注音布局（schema v32 起）。</summary>
    public RubyLayoutSpecDto? Ruby { get; set; }
    /// <summary>未唱外观（schema v31 起）。</summary>
    public TextAppearanceDto? Unsung { get; set; }
    /// <summary>已唱外观（schema v31 起）。</summary>
    public SungAppearanceDto? Sung { get; set; }
    /// <summary>扫字样式（schema v31 起）。</summary>
    public WipeSpecDto? Wipe { get; set; }
    public double? VerticalCenterRatio { get; set; }
    public string? HorizontalAlignment { get; set; }
    public float? Opacity { get; set; }
    public float? Scale { get; set; }
}

/// <summary>注音布局的 JSON 形态。</summary>
internal sealed class RubyLayoutSpecDto
{
    public double? RelativeScale { get; set; }
    public double? GapPx { get; set; }
}

/// <summary>注音布局部分覆盖；缺失表示继承，null 表示使用歌词注音原值。</summary>
internal sealed class RubyLayoutSpecOverrideDto
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public System.Text.Json.JsonElement RelativeScale { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public System.Text.Json.JsonElement GapPx { get; set; }
}

/// <summary>一态的完整外观：填充材质、由内向外的描边列表、阴影与辉光。</summary>
internal sealed class TextAppearanceDto
{
    /// <summary>完整填充材质。</summary>
    public System.Text.Json.JsonElement Fill { get; set; }
    /// <summary>由内向外的描边列表；空表为无描边。</summary>
    public System.Text.Json.JsonElement Strokes { get; set; }
    public ShadowSpecDto? Shadow { get; set; }
    public GlowSpecDto? Glow { get; set; }
}

/// <summary>已唱外观：填充必填；描边、阴影、辉光为 <c>{"kind":"follow"}</c> 或 <c>{"kind":"set","value":…}</c>。</summary>
internal sealed class SungAppearanceDto
{
    public System.Text.Json.JsonElement Fill { get; set; }
    public System.Text.Json.JsonElement Strokes { get; set; }
    public System.Text.Json.JsonElement Shadow { get; set; }
    public System.Text.Json.JsonElement Glow { get; set; }
}

/// <summary>扫字样式的 JSON 形态。</summary>
internal sealed class WipeSpecDto
{
    public double? FeatherRatio { get; set; }
    public WipeHighlightDto? Highlight { get; set; }
}

/// <summary>扫字线高亮的 JSON 形态；完整值与部分覆盖同形。</summary>
internal sealed class WipeHighlightDto
{
    public string? Color { get; set; }
    public double? WidthRatio { get; set; }
    public float? Opacity { get; set; }
    public string? Blend { get; set; }
}

/// <summary>规范字体对象的 JSON 形态。</summary>
internal sealed class TypographySpecDto
{
    public string? FontFamilyName { get; set; }
    public List<string>? FallbackFamilyNames { get; set; }
    public float? FontSizePx { get; set; }
    public int? FontWeight { get; set; }
    public bool? IsBold { get; set; }
    public bool? IsItalic { get; set; }
    public float? LetterSpacingPx { get; set; }
}

/// <summary>完整渐变描述的 JSON 形态（schema v11 起）。</summary>
internal sealed class GradientSpecDto
{
    public string? Kind { get; set; }

    public double? AngleDegrees { get; set; }

    public double? CenterX { get; set; }

    public double? CenterY { get; set; }

    public double? RadiusX { get; set; }

    public double? RadiusY { get; set; }

    public string? CoordinateSpace { get; set; }

    public string? ColorSpace { get; set; }

    public string? HuePath { get; set; }

    public List<GradientStopDto>? Stops { get; set; }
}

/// <summary>渐变停止点的 JSON 形态。</summary>
internal sealed class GradientStopDto
{
    public double? Offset { get; set; }

    public string? Color { get; set; }
}

/// <summary>描边列表中一层的 JSON 形态（schema v31 起）；三个字段都必须提供。</summary>
internal sealed class StrokeLayerDto
{
    public System.Text.Json.JsonElement Material { get; set; }
    public float? WidthPx { get; set; }
    public string? Join { get; set; }
}

/// <summary>完整静态辉光的 JSON 形态。</summary>
internal sealed class GlowSpecDto
{
    public string? Color { get; set; }
    public float? Opacity { get; set; }
    public float? BlurRadiusPx { get; set; }
    public float? SpreadPx { get; set; }
}

/// <summary>静态辉光部分覆盖的 JSON 形态。</summary>
internal sealed class GlowSpecOverrideDto
{
    public string? Color { get; set; }
    public float? Opacity { get; set; }
    public float? BlurRadiusPx { get; set; }
    public float? SpreadPx { get; set; }
}

/// <summary>阴影部分覆盖的 JSON 形态。</summary>
internal sealed class ShadowSpecOverrideDto
{
    public string? Color { get; set; }
    public float? Opacity { get; set; }
    public double? AngleDegrees { get; set; }
    public float? DistancePx { get; set; }
    public float? BlurRadiusPx { get; set; }
}

/// <summary>方向阴影描述的 JSON 形态（schema v12 起）。</summary>
internal sealed class ShadowSpecDto
{
    public string? Color { get; set; }

    public float? Opacity { get; set; }

    public double? AngleDegrees { get; set; }

    public float? DistancePx { get; set; }

    public float? BlurRadiusPx { get; set; }
}

/// <summary>
/// 分支、行级或元素级的样式覆盖。
/// </summary>
/// <remarks>键缺失表示不覆盖，规范对象只保存实际参与覆盖的成员（schema v31 起为三层结构）。</remarks>
internal sealed class LyricStyleOverrideDto
{
    /// <summary>规范字体部分覆盖（schema v13 起）。</summary>
    public TypographySpecOverrideDto? Typography { get; set; }
    /// <summary>注音布局部分覆盖。</summary>
    public RubyLayoutSpecOverrideDto? Ruby { get; set; }
    /// <summary>未唱外观部分覆盖。</summary>
    public TextAppearanceOverrideDto? Unsung { get; set; }
    /// <summary>已唱外观部分覆盖。</summary>
    public SungAppearanceOverrideDto? Sung { get; set; }
    /// <summary>扫字样式部分覆盖。</summary>
    public WipeSpecOverrideDto? Wipe { get; set; }
    public double? VerticalCenterRatio { get; set; }
    public string? HorizontalAlignment { get; set; }
    public float? Opacity { get; set; }
    public float? Scale { get; set; }
}

/// <summary>一态外观的部分覆盖：填充为完整材质，描边为整表，阴影与辉光为 <c>null</c>（关闭）或部分对象。</summary>
internal sealed class TextAppearanceOverrideDto
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public System.Text.Json.JsonElement Fill { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public System.Text.Json.JsonElement Strokes { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public System.Text.Json.JsonElement Shadow { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public System.Text.Json.JsonElement Glow { get; set; }
}

/// <summary>已唱外观的部分覆盖；描边、阴影、辉光写成「跟随」或「设置」判别对象。</summary>
internal sealed class SungAppearanceOverrideDto
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public System.Text.Json.JsonElement Fill { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public System.Text.Json.JsonElement Strokes { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public System.Text.Json.JsonElement Shadow { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public System.Text.Json.JsonElement Glow { get; set; }
}

/// <summary>扫字样式的部分覆盖；高亮为 <c>null</c>（关闭）或部分对象。</summary>
internal sealed class WipeSpecOverrideDto
{
    public double? FeatherRatio { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public System.Text.Json.JsonElement Highlight { get; set; }
}

/// <summary>规范字体部分覆盖的 JSON 形态。</summary>
internal sealed class TypographySpecOverrideDto
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public System.Text.Json.JsonElement FontFamilyName { get; set; }
    public List<string>? FallbackFamilyNames { get; set; }
    public float? FontSizePx { get; set; }
    public int? FontWeight { get; set; }
    public bool? IsBold { get; set; }
    public bool? IsItalic { get; set; }
    public float? LetterSpacingPx { get; set; }
}

/// <summary><c>project.json</c> 中的合成设置。</summary>
internal sealed class CompositionDto
{
    public int? CanvasWidth { get; set; }

    public int? CanvasHeight { get; set; }

    public FrameRateDto? FrameRate { get; set; }

    public string? BackgroundColor { get; set; }

    public long? OutputDurationUs { get; set; }
}

/// <summary>
/// 帧率的有理数表示。
/// </summary>
/// <remarks>
/// 刻意不用单个浮点数存储：29.97 与 23.976 无法用二进制浮点精确表达，
/// 会破坏帧号与微秒换算的可重复性（docs/development.md §5.1）。
/// </remarks>
internal sealed class FrameRateDto
{
    public int? Numerator { get; set; }

    public int? Denominator { get; set; }
}

/// <summary>不可变歌词修订的磁盘表示。</summary>
internal sealed class LyricsFileDto
{
    public int? SchemaVersion { get; set; }
    public string? PrimaryBranchId { get; set; }
    public List<LyricBranchDto>? Branches { get; set; }
}

internal sealed class LyricBranchDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Role { get; set; }
    public string? Language { get; set; }
    public string? TimingMode { get; set; }
    public LyricStyleOverrideDto? StyleOverride { get; set; }
    public List<LyricLineDto>? Lines { get; set; }
}

internal sealed class RubyAnnotationDto
{
    public string? Id { get; set; }
    public string? BaseTokenId { get; set; }
    public string? Text { get; set; }
    public string? Language { get; set; }
    public double RelativeScale { get; set; } = .5d;
    public double GapPx { get; set; } = 2d;
    public string? Alignment { get; set; }

    /// <summary>v17 起的有序注音片段；v16 缺失时由迁移器创建一个兼容片段。</summary>
    public List<RubyTextSegmentDto>? Segments { get; set; }
}

internal sealed class RubyTextSegmentDto
{
    public string? Id { get; set; }
    public string? Text { get; set; }
    public long? StartUs { get; set; }
    public long? EndUs { get; set; }
    public string? TimingState { get; set; }
}

/// <summary>一行歌词的磁盘表示。</summary>
internal sealed class LyricLineDto
{
    public string? PrimaryLineId { get; set; }
    public List<RubyAnnotationDto>? RubyAnnotations { get; set; }

    public string? Id { get; set; }

    public string? Text { get; set; }

    public long? StartUs { get; set; }

    public long? EndUs { get; set; }

    public string? TimingState { get; set; }

    /// <summary>行级样式覆盖（schema v2 起）。</summary>
    public LyricStyleOverrideDto? StyleOverride { get; set; }

    public List<LyricTokenDto>? Tokens { get; set; }
}

/// <summary>一个文本元素的磁盘表示。</summary>
internal sealed class LyricTokenDto
{
    public string? Id { get; set; }

    public string? Text { get; set; }

    public long? StartUs { get; set; }

    public long? EndUs { get; set; }

    public string? TimingState { get; set; }

    /// <summary>元素级样式覆盖（schema v2 起）。目前只有颜色会参与绘制。</summary>
    public LyricStyleOverrideDto? StyleOverride { get; set; }

    /// <summary>句中收尾提示覆写（lyrics schema v21 起）；null 表示按邻接语境自动推导。</summary>
    public bool? PhraseEndHint { get; set; }
}

/// <summary>归一化行位置的磁盘表示。</summary>
internal sealed class ScenePlacementDto
{
    public double X { get; set; }
    public double Y { get; set; }
    public string? Anchor { get; set; }
    public double RotationDegrees { get; set; }
    public double Scale { get; set; } = 1d;
}

internal sealed class SafeAreaDto
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Right { get; set; }
    public double Bottom { get; set; }
}
internal sealed class SlotAnimationDto
{
    public string? RevealMode { get; set; }
    public double RevealProgress { get; set; } = .5;
    public long RevealDelayUs { get; set; }
    public long RevealLeadUs { get; set; }
    public int MaxVisibleLines { get; set; } = 2;
    public string? ConflictPolicy { get; set; }
    public string? RetentionPolicy { get; set; }
}

internal sealed class LyricPresentationDto
{
    public List<SceneContainerDto>? Nodes { get; set; }
    public List<LyricDisplayProgramDto>? Programs { get; set; }
    public List<LinePresentationDto>? LineOverrides { get; set; }
}
internal sealed class SceneContainerDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? ParentId { get; set; }
    public ScenePlacementDto? Placement { get; set; }
    public double ReferenceWidthRatio { get; set; } = 1;
    public double ReferenceHeightRatio { get; set; } = 1;
    public LyricContainerLayoutDto? Layout { get; set; }
    public SceneContentDto? Content { get; set; }
    public SceneAttachmentDto? Attachment { get; set; }
}
internal sealed class LyricContainerLayoutDto
{
    public string? Kind { get; set; }
    public ScenePlacementDto? DefaultPlacement { get; set; }
    public SafeAreaDto? SafeArea { get; set; }
    public double MaxWidthRatio { get; set; } = .9;
    public List<string>? SlotIds { get; set; }
    public SlotAnimationDto? SlotAnimation { get; set; }
}
internal sealed class LyricDisplayProgramDto
{
    public string? Id { get; set; }
    public string? BranchId { get; set; }
    public string? RootContainerId { get; set; }
    public string? DefaultContainerId { get; set; }
    public string? AnimationPolicy { get; set; }
    public List<LyricDisplayClipDto>? Clips { get; set; }
    public List<LyricLeadInDto>? LeadIns { get; set; }
    public LeadInGenerationDto? LeadInGeneration { get; set; }
}
internal sealed class LyricDisplayClipDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public List<string>? LineIds { get; set; }
    public string? ContainerId { get; set; }
    public LyricClipScheduleContextDto? ScheduleContext { get; set; }
}
internal sealed class LyricClipScheduleContextDto
{
    public List<string>? LineIds { get; set; }
    public bool IsDefaultSegment { get; set; }
    public string? PreviousLineId { get; set; }
    public string? NextLineId { get; set; }
}
internal sealed class LinePresentationDto
{
    public string? BranchId { get; set; }
    public string? LineId { get; set; }
    public LyricLocalTransformDto? LocalTransform { get; set; }
}
internal sealed class LyricLocalTransformDto
{
    public double OffsetXRatio { get; set; }
    public double OffsetYRatio { get; set; }
    public double RotationDegrees { get; set; }
    public double Scale { get; set; } = 1;
}
