using System.Text.Json;
using System.Text.Json.Serialization;

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
internal sealed class SceneTextAppearanceDto
{
    public TypographySpecDto? Typography { get; set; }
    public JsonElement PendingFill { get; set; }
    public JsonElement ElapsedFill { get; set; }
    public StrokeSpecDto? Stroke { get; set; }
    public ShadowSpecDto? Shadow { get; set; }
    public GlowSpecDto? Glow { get; set; }
    public float? Opacity { get; set; }
}
internal sealed class SceneTextAppearanceOverrideDto
{
    public TypographySpecOverrideDto? Typography { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement PendingFill { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement ElapsedFill { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Stroke { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Shadow { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Glow { get; set; }
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
