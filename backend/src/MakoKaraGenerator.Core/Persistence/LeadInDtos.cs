namespace MakoKaraGenerator.Core.Persistence;

internal sealed class SceneContentDto
{
    public string? Kind { get; set; }
    public List<string>? MarkNodeIds { get; set; }
    public string? StepMode { get; set; }
    public SceneTextAppearanceDto? Appearance { get; set; }
    public string? Text { get; set; }
    public SceneTextAppearanceOverrideDto? AppearanceOverride { get; set; }
}
internal sealed class SceneAttachmentDto { public string? Mode { get; set; } }
/// <summary>场景标记外观（schema v31 起）：字形、待唱外观、已过外观与不透明度。</summary>
internal sealed class SceneTextAppearanceDto
{
    public TypographySpecDto? Typography { get; set; }
    public TextAppearanceDto? Pending { get; set; }
    public SungAppearanceDto? Elapsed { get; set; }
    public float? Opacity { get; set; }
}
/// <summary>文字标记的部分外观覆盖（schema v31 起）。</summary>
internal sealed class SceneTextAppearanceOverrideDto
{
    public TypographySpecOverrideDto? Typography { get; set; }
    public TextAppearanceOverrideDto? Pending { get; set; }
    public SungAppearanceOverrideDto? Elapsed { get; set; }
    public float? Opacity { get; set; }
}
internal sealed class LyricLeadInDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;
    public string? TargetLineId { get; set; }
    public string? IndicatorContainerId { get; set; }
    public long DurationUs { get; set; }
    public string? Origin { get; set; }
}
internal sealed class LeadInGenerationDto
{
    public bool IncludeOpening { get; set; } = true;
    public bool IncludeLongGaps { get; set; } = true;
    public long GapThresholdUs { get; set; }
    public long DurationUs { get; set; }
    public string? IndicatorContainerId { get; set; }
    public List<string>? ExcludedLineIds { get; set; }
}
