using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Persistence;

internal static partial class ProjectDtoMapper
{
    private static SceneContentDto? ToDto(SceneContent? content) => content switch
    {
        null => null,
        CountdownSequenceContent sequence => new() { Kind = "CountdownSequence", StepMode = "EqualDuration",
            MarkNodeIds = [.. sequence.MarkNodeIds], Appearance = ToDto(sequence.Appearance) },
        TextMarkContent mark => new() { Kind = "TextMark", Text = mark.Text, AppearanceOverride = ToDto(mark.AppearanceOverride) },
        _ => throw new ArgumentException("未知场景内容。", nameof(content)),
    };
    private static SceneContent? MapContent(SceneContentDto? dto, string path, List<ProjectIssue> issues)
    {
        if (dto is null) return null;
        if (string.Equals(dto.Kind, "CountdownSequence", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(dto.StepMode, "EqualDuration", StringComparison.OrdinalIgnoreCase)
                || dto.Text is not null || dto.AppearanceOverride is not null)
                DiagnosticList.Add(issues, DiagnosticCode.InvalidDisplayProgram, IssueSeverity.Error, "倒计时组的步进方式或内容字段无效。", path);
            return new CountdownSequenceContent { MarkNodeIds = dto.MarkNodeIds is null ? [] : [.. dto.MarkNodeIds], Appearance = MapAppearance(dto.Appearance, path + ".appearance", issues) };
        }
        if (string.Equals(dto.Kind, "TextMark", StringComparison.OrdinalIgnoreCase))
        {
            if (dto.MarkNodeIds is not null || dto.StepMode is not null || dto.Appearance is not null)
                DiagnosticList.Add(issues, DiagnosticCode.InvalidDisplayProgram, IssueSeverity.Error, "文字标记不能携带倒计时组配置。", path);
            var overlay = dto.AppearanceOverride;
            return new TextMarkContent { Text = dto.Text ?? string.Empty,
                AppearanceOverride = overlay is null ? null : new SceneTextAppearanceOverride
                { Typography = MapTypographyOverride(overlay.Typography, path + ".typography", issues),
                    PendingFill = MapOptionalFillOverride(overlay.PendingFill, path + ".pendingFill", issues),
                    ElapsedFill = MapOptionalFillOverride(overlay.ElapsedFill, path + ".elapsedFill", issues),
                    Stroke = MapOptionalStrokeOverride(overlay.Stroke, path + ".stroke", issues),
                    Shadow = MapOptionalShadowOverride(overlay.Shadow, path + ".shadow", issues), Opacity = overlay.Opacity } };
        }
        DiagnosticList.Add(issues, DiagnosticCode.InvalidDisplayProgram, IssueSeverity.Error, "未知场景内容类型：" + dto.Kind, path);
        return null;
    }
    private static SceneTextAppearanceDto ToDto(SceneTextAppearance appearance) => new()
    {
        Typography = ToDto(appearance.ToLyricStyle()).Typography, PendingFill = ToDto(appearance.PendingFill),
        ElapsedFill = ToDto(appearance.ElapsedFill), Stroke = ToDto(appearance.Stroke), Shadow = ToDto(appearance.Shadow), Opacity = appearance.Opacity,
    };
    private static SceneTextAppearance MapAppearance(SceneTextAppearanceDto? dto, string path, List<ProjectIssue> issues)
    {
        var basis = SceneTextAppearance.Default;
        if (dto is null) return basis;
        return new() { Typography = dto.Typography is null ? basis.Typography : MapTypographySpec(dto.Typography, basis.Typography, path + ".typography", issues),
            PendingFill = new FillSpec { Material = ReadMaterial(dto.PendingFill, path + ".pendingFill", issues) },
            ElapsedFill = new FillSpec { Material = ReadMaterial(dto.ElapsedFill, path + ".elapsedFill", issues) },
            Stroke = dto.Stroke is null ? null : MapStrokeSpec(dto.Stroke, path + ".stroke", issues),
            Shadow = dto.Shadow is null ? null : MapShadowSpec(dto.Shadow, path + ".shadow", issues),
            Opacity = MapRatio(dto.Opacity, basis.Opacity, path + ".opacity", issues) };
    }
    private static SceneTextAppearanceOverrideDto? ToDto(SceneTextAppearanceOverride? overlay)
    {
        if (overlay is null) return null;
        return new() { Typography = overlay.Typography is null ? null : ToDto(overlay.Typography),
            PendingFill = ToOptionalFillOverrideDto(overlay.PendingFill), ElapsedFill = ToOptionalFillOverrideDto(overlay.ElapsedFill),
            Stroke = ToOptionalStrokeDto(overlay.Stroke), Shadow = ToOptionalShadowDto(overlay.Shadow), Opacity = overlay.Opacity };
    }
    private static LyricLeadInDto ToDto(LyricLeadIn cue) => new() { Id = cue.Id, Name = cue.Name, Enabled = cue.Enabled,
        TargetLineId = cue.TargetLineId, IndicatorContainerId = cue.IndicatorContainerId, DurationUs = cue.DurationUs, Origin = cue.Origin.ToString() };
    private static LyricLeadIn MapLeadIn(LyricLeadInDto cue) => new() { Id = cue.Id ?? string.Empty, Name = cue.Name, Enabled = cue.Enabled,
        TargetLineId = cue.TargetLineId ?? string.Empty, IndicatorContainerId = cue.IndicatorContainerId ?? string.Empty,
        DurationUs = cue.DurationUs, Origin = ParseBranchEnum<LeadInOrigin>(cue.Origin) };
    private static LeadInGenerationDto? ToDto(LeadInGenerationSettings? settings) => settings is null ? null : new()
    { IncludeOpening = settings.IncludeOpening, IncludeLongGaps = settings.IncludeLongGaps, DurationUs = settings.DurationUs,
        GapThresholdUs = settings.GapThresholdUs, IndicatorContainerId = settings.IndicatorContainerId, ExcludedLineIds = [.. settings.ExcludedLineIds] };
    private static LeadInGenerationSettings? MapGeneration(LeadInGenerationDto? settings) => settings is null ? null : new()
    { IncludeOpening = settings.IncludeOpening, IncludeLongGaps = settings.IncludeLongGaps, DurationUs = settings.DurationUs,
        GapThresholdUs = settings.GapThresholdUs, IndicatorContainerId = settings.IndicatorContainerId,
        ExcludedLineIds = settings.ExcludedLineIds is null ? [] : [.. settings.ExcludedLineIds] };
}
