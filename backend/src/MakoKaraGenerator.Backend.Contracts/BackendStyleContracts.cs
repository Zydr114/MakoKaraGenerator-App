using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>静态样式新输入和操作预算；不限制旧工程格式。</summary>
public static class StyleEditingLimits
{
    /// <summary>单次目标数。</summary>
    public const int MaxTargets = 1000;
    /// <summary>完整命令意图 JSON 字节数。</summary>
    public const int MaxIntentBytes = 64 * 1024;
    /// <summary>本次改变目标的新投影累计字节数。</summary>
    public const int MaxPreparedBytes = 16 * 1024 * 1024;
    /// <summary>新字体名称 UTF-16 长度。</summary>
    public const int MaxFontLength = 256;
    /// <summary>新回退链长度。</summary>
    public const int MaxFallbacks = 32;
    /// <summary>新渐变色标长度。</summary>
    public const int MaxStops = 256;
    /// <summary>一态描边层数上限。</summary>
    public const int MaxStrokes = 4;
    /// <summary>属性路径总数，即单次 patch／restore 的条目上限。</summary>
    public const int PropertyCount = 49;
}
/// <summary>稳定样式目标，派生类定义唯一合法 ID 组合。</summary>
[JsonConverter(typeof(StyleTargetJsonConverter))]
public abstract record StyleTargetDto(string Kind);
/// <summary>工程默认样式目标。</summary>
public sealed record ProjectStyleTargetDto() : StyleTargetDto("project");
/// <summary>来源样式目标。</summary>
public sealed record BranchStyleTargetDto(string BranchId) : StyleTargetDto("branch");
/// <summary>行样式目标。</summary>
public sealed record LineStyleTargetDto(string BranchId, string LineId) : StyleTargetDto("line");
/// <summary>元素样式目标。</summary>
public sealed record TokenStyleTargetDto(string BranchId, string LineId, string TokenId) : StyleTargetDto("token");
/// <summary>RGBA 完整颜色。</summary>
public sealed record StyleColorDto(byte Red, byte Green, byte Blue, byte Alpha);
/// <summary>完整渐变色标。</summary>
public sealed record StyleGradientStopDto(double Offset, StyleColorDto Color);
/// <summary>完整渐变；不排序或修补输入。</summary>
public sealed record StyleGradientDto(StyleGradientKind Kind, double AngleDegrees, double CenterX, double CenterY,
    double RadiusX, double RadiusY, StyleGradientCoordinateSpace CoordinateSpace, StyleGradientColorSpace ColorSpace,
    StyleGradientHuePath HuePath, StyleGradientStopDto[] Stops);
/// <summary>canonical 材质联合；包身份只读。</summary>
[JsonConverter(typeof(StyleMaterialJsonConverter))]
public abstract record StyleMaterialDto(string Kind);
/// <summary>完整纯色材质。</summary>
public sealed record SolidStyleMaterialDto(StyleColorDto Color) : StyleMaterialDto("solid");
/// <summary>完整渐变材质。</summary>
public sealed record GradientStyleMaterialDto(StyleGradientDto Gradient) : StyleMaterialDto("gradient");
/// <summary>只读包身份；不传输、执行或改写参数。</summary>
public sealed record PackageStyleMaterialDto(string PackageId, string Revision, string ExportId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParametersJson = null) : StyleMaterialDto("package");
/// <summary>完整描边层；由内向外排列，层序决定位置。</summary>
public sealed record StyleStrokeDto(StyleMaterialDto Material, float WidthPx, StyleStrokeJoin Join);
/// <summary>完整阴影。</summary>
public sealed record StyleShadowDto(StyleColorDto Color, float Opacity, double AngleDegrees, float DistancePx, float BlurRadiusPx,
    float SpreadPx, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] StyleGradientDto? Gradient);
/// <summary>完整辉光。</summary>
public sealed record StyleGlowDto(StyleColorDto Color, float Opacity, float BlurRadiusPx, float SpreadPx);
/// <summary>一态的完整外观（未唱或待唱）；空描边表为无描边，null 阴影／辉光为关闭。</summary>
public sealed record StyleAppearanceDto(StyleMaterialDto Fill, StyleStrokeDto[] Strokes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] StyleShadowDto? Shadow,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] StyleGlowDto? Glow);
/// <summary>已唱一侧可跟随未唱的项是跟随，还是显式取值。</summary>
[JsonConverter(typeof(StyleEnumJsonConverter<StyleFollowKind>))]
public enum StyleFollowKind
{
    /// <summary>跟随未唱一侧；JSON 不写 value。</summary>
    Follow,
    /// <summary>显式取值；JSON 必须写 value。</summary>
    Set,
}
/// <summary>已唱描边：跟随未唱，或显式描边表（可为空表）。</summary>
[JsonConverter(typeof(StyleSungStrokesJsonConverter))]
public sealed record StyleSungStrokesDto(StyleFollowKind Kind, StyleStrokeDto[]? Value = null);
/// <summary>已唱阴影：跟随未唱，或显式阴影（null 为关闭）。</summary>
[JsonConverter(typeof(StyleSungShadowJsonConverter))]
public sealed record StyleSungShadowDto(StyleFollowKind Kind, StyleShadowDto? Value = null);
/// <summary>已唱辉光：跟随未唱，或显式辉光（null 为关闭）。</summary>
[JsonConverter(typeof(StyleSungGlowJsonConverter))]
public sealed record StyleSungGlowDto(StyleFollowKind Kind, StyleGlowDto? Value = null);
/// <summary>已唱或已过一侧的完整外观；填充必填，描边、阴影、辉光可跟随未唱。</summary>
public sealed record StyleSungAppearanceDto(StyleMaterialDto Fill, StyleSungStrokesDto Strokes, StyleSungShadowDto Shadow, StyleSungGlowDto Glow);
/// <summary>完整扫字线高亮。</summary>
public sealed record StyleWipeHighlightDto(StyleColorDto Color, double WidthRatio, float Opacity, StyleWipeHighlightBlend Blend);
/// <summary>完整扫字样式；两态共享，文字标记不使用。</summary>
public sealed record StyleWipeDto(double FeatherRatio,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] StyleWipeHighlightDto? Highlight);
/// <summary>完整排版；fontFamilyName 的 null 必须保留。</summary>
public sealed record StyleTypographyDto([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FontFamilyName,
    string[] FallbackFamilyNames, float FontSizePx, int FontWeight, bool IsBold, bool IsItalic, float LetterSpacingPx);
/// <summary>注音排版；null 使用歌词内对应注音的原始数值。</summary>
public sealed record StyleRubyDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? RelativeScale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? GapPx);
/// <summary>完整 canonical 静态样式：字形、未唱与已唱两态外观、扫字样式及整体布局。</summary>
public sealed record CanonicalStyleDto(StyleTypographyDto Typography, StyleAppearanceDto Unsung, StyleSungAppearanceDto Sung,
    StyleWipeDto Wipe, double VerticalCenterRatio, StyleHorizontalAlignment HorizontalAlignment, float Opacity, float Scale,
    StyleRubyDto Ruby);
/// <summary>单属性闭合 typed 联合，value 必须显式提供。</summary>
[JsonConverter(typeof(StyleChangeJsonConverter))]
public abstract record StyleChangeDto(string Property);
/// <summary>修改 typography.fontFamilyName；value 与缺失不同。</summary>
public sealed record StyleFontFamilyChangeDto(string? Value) : StyleChangeDto("typography.fontFamilyName");
/// <summary>修改 typography.fallbackFamilyNames。</summary>
public sealed record StyleFallbackFamiliesChangeDto(string[] Value) : StyleChangeDto("typography.fallbackFamilyNames");
/// <summary>修改 typography.fontSizePx。</summary>
public sealed record StyleFontSizeChangeDto(float Value) : StyleChangeDto("typography.fontSizePx");
/// <summary>修改 typography.fontWeight。</summary>
public sealed record StyleFontWeightChangeDto(int Value) : StyleChangeDto("typography.fontWeight");
/// <summary>修改 typography.isBold。</summary>
public sealed record StyleBoldChangeDto(bool Value) : StyleChangeDto("typography.isBold");
/// <summary>修改 typography.isItalic。</summary>
public sealed record StyleItalicChangeDto(bool Value) : StyleChangeDto("typography.isItalic");
/// <summary>修改 typography.letterSpacingPx。</summary>
public sealed record StyleLetterSpacingChangeDto(float Value) : StyleChangeDto("typography.letterSpacingPx");
/// <summary>设置注音相对字号；null 使用歌词的原始值。</summary>
public sealed record StyleRubyRelativeScaleChangeDto(double? Value) : StyleChangeDto("ruby.relativeScale");
/// <summary>设置注音与正文间距；null 使用歌词的原始值。</summary>
public sealed record StyleRubyGapChangeDto(double? Value) : StyleChangeDto("ruby.gapPx");
/// <summary>修改 unsung.fill。</summary>
public sealed record StyleUnsungFillChangeDto(StyleMaterialDto Value) : StyleChangeDto("unsung.fill");
/// <summary>修改 sung.fill。</summary>
public sealed record StyleSungFillChangeDto(StyleMaterialDto Value) : StyleChangeDto("sung.fill");
/// <summary>整表修改 unsung.strokes；空表为无描边。</summary>
public sealed record StyleUnsungStrokesChangeDto(StyleStrokeDto[] Value) : StyleChangeDto("unsung.strokes");
/// <summary>整表修改 sung.strokes，或改为跟随未唱。</summary>
public sealed record StyleSungStrokesChangeDto(StyleSungStrokesDto Value) : StyleChangeDto("sung.strokes");
/// <summary>修改完整 unsung.shadow；null 为关闭。</summary>
public sealed record StyleUnsungShadowChangeDto(StyleShadowDto? Value) : StyleChangeDto("unsung.shadow");
/// <summary>修改 unsung.shadow.color。</summary>
public sealed record StyleUnsungShadowColorChangeDto(StyleColorDto Value) : StyleChangeDto("unsung.shadow.color");
/// <summary>修改 unsung.shadow.gradient；null 为清除渐变。</summary>
public sealed record StyleUnsungShadowGradientChangeDto(StyleGradientDto? Value) : StyleChangeDto("unsung.shadow.gradient");
/// <summary>修改 unsung.shadow.opacity。</summary>
public sealed record StyleUnsungShadowOpacityChangeDto(float Value) : StyleChangeDto("unsung.shadow.opacity");
/// <summary>修改 unsung.shadow.angleDegrees。</summary>
public sealed record StyleUnsungShadowAngleChangeDto(double Value) : StyleChangeDto("unsung.shadow.angleDegrees");
/// <summary>修改 unsung.shadow.distancePx。</summary>
public sealed record StyleUnsungShadowDistanceChangeDto(float Value) : StyleChangeDto("unsung.shadow.distancePx");
/// <summary>修改 unsung.shadow.blurRadiusPx。</summary>
public sealed record StyleUnsungShadowBlurChangeDto(float Value) : StyleChangeDto("unsung.shadow.blurRadiusPx");
/// <summary>修改 unsung.shadow.spreadPx。</summary>
public sealed record StyleUnsungShadowSpreadChangeDto(float Value) : StyleChangeDto("unsung.shadow.spreadPx");
/// <summary>修改完整 sung.shadow：跟随、关闭或显式阴影。</summary>
public sealed record StyleSungShadowChangeDto(StyleSungShadowDto Value) : StyleChangeDto("sung.shadow");
/// <summary>修改 sung.shadow.color。</summary>
public sealed record StyleSungShadowColorChangeDto(StyleColorDto Value) : StyleChangeDto("sung.shadow.color");
/// <summary>修改 sung.shadow.gradient；null 为清除渐变。</summary>
public sealed record StyleSungShadowGradientChangeDto(StyleGradientDto? Value) : StyleChangeDto("sung.shadow.gradient");
/// <summary>修改 sung.shadow.opacity。</summary>
public sealed record StyleSungShadowOpacityChangeDto(float Value) : StyleChangeDto("sung.shadow.opacity");
/// <summary>修改 sung.shadow.angleDegrees。</summary>
public sealed record StyleSungShadowAngleChangeDto(double Value) : StyleChangeDto("sung.shadow.angleDegrees");
/// <summary>修改 sung.shadow.distancePx。</summary>
public sealed record StyleSungShadowDistanceChangeDto(float Value) : StyleChangeDto("sung.shadow.distancePx");
/// <summary>修改 sung.shadow.blurRadiusPx。</summary>
public sealed record StyleSungShadowBlurChangeDto(float Value) : StyleChangeDto("sung.shadow.blurRadiusPx");
/// <summary>修改 sung.shadow.spreadPx。</summary>
public sealed record StyleSungShadowSpreadChangeDto(float Value) : StyleChangeDto("sung.shadow.spreadPx");
/// <summary>修改完整 unsung.glow；null 为关闭。</summary>
public sealed record StyleUnsungGlowChangeDto(StyleGlowDto? Value) : StyleChangeDto("unsung.glow");
/// <summary>修改 unsung.glow.color。</summary>
public sealed record StyleUnsungGlowColorChangeDto(StyleColorDto Value) : StyleChangeDto("unsung.glow.color");
/// <summary>修改 unsung.glow.opacity。</summary>
public sealed record StyleUnsungGlowOpacityChangeDto(float Value) : StyleChangeDto("unsung.glow.opacity");
/// <summary>修改 unsung.glow.blurRadiusPx。</summary>
public sealed record StyleUnsungGlowBlurChangeDto(float Value) : StyleChangeDto("unsung.glow.blurRadiusPx");
/// <summary>修改 unsung.glow.spreadPx。</summary>
public sealed record StyleUnsungGlowSpreadChangeDto(float Value) : StyleChangeDto("unsung.glow.spreadPx");
/// <summary>修改完整 sung.glow：跟随、关闭或显式辉光。</summary>
public sealed record StyleSungGlowChangeDto(StyleSungGlowDto Value) : StyleChangeDto("sung.glow");
/// <summary>修改 sung.glow.color。</summary>
public sealed record StyleSungGlowColorChangeDto(StyleColorDto Value) : StyleChangeDto("sung.glow.color");
/// <summary>修改 sung.glow.opacity。</summary>
public sealed record StyleSungGlowOpacityChangeDto(float Value) : StyleChangeDto("sung.glow.opacity");
/// <summary>修改 sung.glow.blurRadiusPx。</summary>
public sealed record StyleSungGlowBlurChangeDto(float Value) : StyleChangeDto("sung.glow.blurRadiusPx");
/// <summary>修改 sung.glow.spreadPx。</summary>
public sealed record StyleSungGlowSpreadChangeDto(float Value) : StyleChangeDto("sung.glow.spreadPx");
/// <summary>修改 wipe.featherRatio。</summary>
public sealed record StyleWipeFeatherChangeDto(double Value) : StyleChangeDto("wipe.featherRatio");
/// <summary>修改完整 wipe.highlight；null 为关闭。</summary>
public sealed record StyleWipeHighlightChangeDto(StyleWipeHighlightDto? Value) : StyleChangeDto("wipe.highlight");
/// <summary>修改 wipe.highlight.color。</summary>
public sealed record StyleWipeHighlightColorChangeDto(StyleColorDto Value) : StyleChangeDto("wipe.highlight.color");
/// <summary>修改 wipe.highlight.widthRatio。</summary>
public sealed record StyleWipeHighlightWidthChangeDto(double Value) : StyleChangeDto("wipe.highlight.widthRatio");
/// <summary>修改 wipe.highlight.opacity。</summary>
public sealed record StyleWipeHighlightOpacityChangeDto(float Value) : StyleChangeDto("wipe.highlight.opacity");
/// <summary>修改 wipe.highlight.blend。</summary>
public sealed record StyleWipeHighlightBlendChangeDto(StyleWipeHighlightBlend Value) : StyleChangeDto("wipe.highlight.blend");
/// <summary>修改 verticalCenterRatio。</summary>
public sealed record StyleVerticalCenterChangeDto(double Value) : StyleChangeDto("verticalCenterRatio");
/// <summary>修改 horizontalAlignment。</summary>
public sealed record StyleAlignmentChangeDto(StyleHorizontalAlignment Value) : StyleChangeDto("horizontalAlignment");
/// <summary>修改 opacity。</summary>
public sealed record StyleOpacityChangeDto(float Value) : StyleChangeDto("opacity");
/// <summary>修改 scale。</summary>
public sealed record StyleScaleChangeDto(float Value) : StyleChangeDto("scale");
/// <summary>同种稳定目标的原子局部样式修改。</summary>
public sealed record PatchStylesCommandDto(StyleTargetDto[] Targets, StyleChangeDto[] Changes) : ProjectCommandDto("styles.patch");
/// <summary>同种稳定目标的原子指定字段恢复继承。</summary>
public sealed record RestoreStylesCommandDto(StyleTargetDto[] Targets, string[] Properties) : ProjectCommandDto("styles.restore");
/// <summary>有界样式反馈。</summary>
public sealed record StyleCommandResultDto(int ChangedTargetCount);
/// <summary>精确修订静态四层继承；不包含实际 preset/node/clip 上下文。</summary>
public sealed record ProjectStyleResponse(string ProjectSessionId, string ProjectRevision, StyleTargetDto Target,
    CanonicalStyleDto Baseline, StyleChangeDto[] Local, CanonicalStyleDto StaticHierarchy, bool HasPresetApplications,
    string Resolution = "staticHierarchy", string ProtocolVersion = BackendProtocol.Version);
/// <summary>canonical GradientKind 值。</summary>
[JsonConverter(typeof(StyleEnumJsonConverter<StyleGradientKind>))]
public enum StyleGradientKind
{
    /// <summary>Linear。</summary>
    Linear,
    /// <summary>Radial。</summary>
    Radial,
}
/// <summary>canonical GradientCoordinateSpace 值。</summary>
[JsonConverter(typeof(StyleEnumJsonConverter<StyleGradientCoordinateSpace>))]
public enum StyleGradientCoordinateSpace
{
    /// <summary>Canvas。</summary>
    Canvas,
    /// <summary>Line。</summary>
    Line,
    /// <summary>Glyph：按单字字框。</summary>
    Glyph,
}
/// <summary>canonical GradientColorSpace 值。</summary>
[JsonConverter(typeof(StyleEnumJsonConverter<StyleGradientColorSpace>))]
public enum StyleGradientColorSpace
{
    /// <summary>LinearRgb。</summary>
    LinearRgb,
    /// <summary>Hsv。</summary>
    Hsv,
    /// <summary>Oklch。</summary>
    Oklch,
}
/// <summary>canonical GradientHuePath 值。</summary>
[JsonConverter(typeof(StyleEnumJsonConverter<StyleGradientHuePath>))]
public enum StyleGradientHuePath
{
    /// <summary>Shorter。</summary>
    Shorter,
    /// <summary>Longer。</summary>
    Longer,
    /// <summary>Increasing。</summary>
    Increasing,
    /// <summary>Decreasing。</summary>
    Decreasing,
}
/// <summary>canonical StrokeJoin 值。</summary>
[JsonConverter(typeof(StyleEnumJsonConverter<StyleStrokeJoin>))]
public enum StyleStrokeJoin
{
    /// <summary>Miter。</summary>
    Miter,
    /// <summary>Round。</summary>
    Round,
    /// <summary>Bevel。</summary>
    Bevel,
}
/// <summary>扫字线高亮的混合方式。</summary>
[JsonConverter(typeof(StyleEnumJsonConverter<StyleWipeHighlightBlend>))]
public enum StyleWipeHighlightBlend
{
    /// <summary>普通叠加。</summary>
    Normal,
    /// <summary>滤色。</summary>
    Screen,
    /// <summary>相加。</summary>
    Add,
}
/// <summary>canonical HorizontalAlignment 值。</summary>
[JsonConverter(typeof(StyleEnumJsonConverter<StyleHorizontalAlignment>))]
public enum StyleHorizontalAlignment
{
    /// <summary>Left。</summary>
    Left,
    /// <summary>Center。</summary>
    Center,
    /// <summary>Right。</summary>
    Right,
}
