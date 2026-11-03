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
/// <summary>完整 canonical 填充。</summary>
public sealed record StyleFillDto(StyleMaterialDto Material);
/// <summary>完整描边，不使用平面颜色访问外观。</summary>
public sealed record StyleStrokeDto(StyleMaterialDto Material, float WidthPx, StyleStrokeJoin Join, StyleStrokePosition Position);
/// <summary>完整阴影。</summary>
public sealed record StyleShadowDto(StyleColorDto Color, float Opacity, double AngleDegrees, float DistancePx, float BlurRadiusPx);
/// <summary>完整辉光。</summary>
public sealed record StyleGlowDto(StyleColorDto Color, float Opacity, float BlurRadiusPx, float SpreadPx);
/// <summary>完整排版；fontFamilyName 的 null 必须保留。</summary>
public sealed record StyleTypographyDto([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FontFamilyName,
    string[] FallbackFamilyNames, float FontSizePx, int FontWeight, bool IsBold, bool IsItalic, float LetterSpacingPx);
/// <summary>完整 canonical 静态样式；null 描边/阴影必须保留。</summary>
public sealed record CanonicalStyleDto(StyleTypographyDto Typography, StyleFillDto UnsungFill, StyleFillDto SungFill,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] StyleStrokeDto? Stroke,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] StyleShadowDto? Shadow,
    double VerticalCenterRatio, StyleHorizontalAlignment HorizontalAlignment, float Opacity, float Scale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] StyleGlowDto? Glow = null);
/// <summary>单属性闭合 typed 联合，value 必须显式提供。</summary>
[JsonConverter(typeof(StyleChangeJsonConverter))]
public abstract record StyleChangeDto(string Property);
/// <summary>修改完整辉光。</summary>
public sealed record StyleGlowChangeDto(StyleGlowDto? Value) : StyleChangeDto("glow");
/// <summary>修改辉光颜色。</summary>
public sealed record StyleGlowColorChangeDto(StyleColorDto Value) : StyleChangeDto("glow.color");
/// <summary>修改辉光透明度。</summary>
public sealed record StyleGlowOpacityChangeDto(float Value) : StyleChangeDto("glow.opacity");
/// <summary>修改辉光模糊。</summary>
public sealed record StyleGlowBlurChangeDto(float Value) : StyleChangeDto("glow.blurRadiusPx");
/// <summary>修改辉光扩散。</summary>
public sealed record StyleGlowSpreadChangeDto(float Value) : StyleChangeDto("glow.spreadPx");
/// <summary>修改 typography.fontFamilyName；value 与缺失不同。</summary>
public sealed record StyleFontFamilyChangeDto(string? Value) : StyleChangeDto("typography.fontFamilyName");
/// <summary>修改 typography.fallbackFamilyNames；value 与缺失不同。</summary>
public sealed record StyleFallbackFamiliesChangeDto(string[] Value) : StyleChangeDto("typography.fallbackFamilyNames");
/// <summary>修改 typography.fontSizePx；value 与缺失不同。</summary>
public sealed record StyleFontSizeChangeDto(float Value) : StyleChangeDto("typography.fontSizePx");
/// <summary>修改 typography.fontWeight；value 与缺失不同。</summary>
public sealed record StyleFontWeightChangeDto(int Value) : StyleChangeDto("typography.fontWeight");
/// <summary>修改 typography.isBold；value 与缺失不同。</summary>
public sealed record StyleBoldChangeDto(bool Value) : StyleChangeDto("typography.isBold");
/// <summary>修改 typography.isItalic；value 与缺失不同。</summary>
public sealed record StyleItalicChangeDto(bool Value) : StyleChangeDto("typography.isItalic");
/// <summary>修改 typography.letterSpacingPx；value 与缺失不同。</summary>
public sealed record StyleLetterSpacingChangeDto(float Value) : StyleChangeDto("typography.letterSpacingPx");
/// <summary>修改 unsungFill.material；value 与缺失不同。</summary>
public sealed record StyleUnsungMaterialChangeDto(StyleMaterialDto Value) : StyleChangeDto("unsungFill.material");
/// <summary>修改 sungFill.material；value 与缺失不同。</summary>
public sealed record StyleSungMaterialChangeDto(StyleMaterialDto Value) : StyleChangeDto("sungFill.material");
/// <summary>修改 stroke；value 与缺失不同。</summary>
public sealed record StyleStrokeChangeDto(StyleStrokeDto? Value) : StyleChangeDto("stroke");
/// <summary>修改 stroke.material；value 与缺失不同。</summary>
public sealed record StyleStrokeMaterialChangeDto(StyleMaterialDto Value) : StyleChangeDto("stroke.material");
/// <summary>修改 stroke.widthPx；value 与缺失不同。</summary>
public sealed record StyleStrokeWidthChangeDto(float Value) : StyleChangeDto("stroke.widthPx");
/// <summary>修改 stroke.join；value 与缺失不同。</summary>
public sealed record StyleStrokeJoinChangeDto(StyleStrokeJoin Value) : StyleChangeDto("stroke.join");
/// <summary>修改 stroke.position；value 与缺失不同。</summary>
public sealed record StyleStrokePositionChangeDto(StyleStrokePosition Value) : StyleChangeDto("stroke.position");
/// <summary>修改 shadow；value 与缺失不同。</summary>
public sealed record StyleShadowChangeDto(StyleShadowDto? Value) : StyleChangeDto("shadow");
/// <summary>修改 shadow.color；value 与缺失不同。</summary>
public sealed record StyleShadowColorChangeDto(StyleColorDto Value) : StyleChangeDto("shadow.color");
/// <summary>修改 shadow.opacity；value 与缺失不同。</summary>
public sealed record StyleShadowOpacityChangeDto(float Value) : StyleChangeDto("shadow.opacity");
/// <summary>修改 shadow.angleDegrees；value 与缺失不同。</summary>
public sealed record StyleShadowAngleChangeDto(double Value) : StyleChangeDto("shadow.angleDegrees");
/// <summary>修改 shadow.distancePx；value 与缺失不同。</summary>
public sealed record StyleShadowDistanceChangeDto(float Value) : StyleChangeDto("shadow.distancePx");
/// <summary>修改 shadow.blurRadiusPx；value 与缺失不同。</summary>
public sealed record StyleShadowBlurChangeDto(float Value) : StyleChangeDto("shadow.blurRadiusPx");
/// <summary>修改 verticalCenterRatio；value 与缺失不同。</summary>
public sealed record StyleVerticalCenterChangeDto(double Value) : StyleChangeDto("verticalCenterRatio");
/// <summary>修改 horizontalAlignment；value 与缺失不同。</summary>
public sealed record StyleAlignmentChangeDto(StyleHorizontalAlignment Value) : StyleChangeDto("horizontalAlignment");
/// <summary>修改 opacity；value 与缺失不同。</summary>
public sealed record StyleOpacityChangeDto(float Value) : StyleChangeDto("opacity");
/// <summary>修改 scale；value 与缺失不同。</summary>
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
    /// <summary>Glyph。</summary>
    Glyph,
    /// <summary>Progress。</summary>
    Progress,
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
/// <summary>canonical StrokePosition 值。</summary>
[JsonConverter(typeof(StyleEnumJsonConverter<StyleStrokePosition>))]
public enum StyleStrokePosition
{
    /// <summary>Outside。</summary>
    Outside,
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
