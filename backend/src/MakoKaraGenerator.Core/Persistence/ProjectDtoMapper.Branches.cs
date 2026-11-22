using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Persistence;

internal static partial class ProjectDtoMapper
{
    private static SafeAreaDto ToDto(NormalizedSafeArea area) => new()
    { Left = area.Left, Top = area.Top, Right = area.Right, Bottom = area.Bottom };
    private static NormalizedSafeArea MapSafeArea(SafeAreaDto? dto) => dto is null ? NormalizedSafeArea.Full
        : new(dto.Left, dto.Top, dto.Right, dto.Bottom);
    private static SceneTransformDto? ToDto(SceneTransform? transform) => transform is null ? null : new()
    {
        Opacity = transform.Opacity, Scale = transform.Scale, OffsetXPx = transform.OffsetXPx,
        OffsetYPx = transform.OffsetYPx, RotationDegrees = transform.RotationDegrees,
    };
    private static SceneTransform? MapTransform(SceneTransformDto? dto) => dto is null ? null : new()
    { Opacity = dto.Opacity, Scale = dto.Scale, OffsetXPx = dto.OffsetXPx, OffsetYPx = dto.OffsetYPx, RotationDegrees = dto.RotationDegrees };
    private static SlotAnimationDto? ToDto(SlotAnimationSpec? profile) => profile is null ? null : new()
    {
        RevealMode = profile.RevealMode.ToString(), RevealProgress = profile.RevealProgress,
        RevealDelayUs = profile.RevealDelayUs, RevealLeadUs = profile.RevealLeadUs, MaxVisibleLines = profile.MaxVisibleLines,
        ConflictPolicy = profile.ConflictPolicy.ToString(), RetentionPolicy = profile.RetentionPolicy.ToString(),
        EnterDurationUs = profile.EnterDurationUs, ExitDurationUs = profile.ExitDurationUs,
        Easing = profile.Easing.ToString(), ExitEasing = profile.ExitEasing.ToString(),
        EnterFrom = ToDto(profile.EnterFrom), Hold = ToDto(profile.Hold), ExitTo = ToDto(profile.ExitTo),

    };
    private static readonly SlotAnimationSpec DefaultSlotAnimation = new();

    private static SceneTransform MapRequiredTransform(
        SceneTransformDto? dto,
        SceneTransform fallback,
        string path,
        List<ProjectIssue> issues)
    {
        if (MapTransform(dto) is { } transform)
        {
            return transform;
        }

        DiagnosticList.Add(issues, DiagnosticCode.InvalidDisplayProgram, IssueSeverity.Error,
            "缺少槽位动画变换定义，已暂时使用默认值。", path);
        return fallback;
    }

    private static SlotAnimationSpec? MapSlotAnimation(SlotAnimationDto? dto, string path, List<ProjectIssue> issues) => dto is null ? null : new()
    {
        RevealMode = ParseBranchEnum<SlotRevealMode>(dto.RevealMode), RevealProgress = dto.RevealProgress,
        RevealDelayUs = dto.RevealDelayUs, RevealLeadUs = dto.RevealLeadUs, MaxVisibleLines = dto.MaxVisibleLines,
        ConflictPolicy = ParseBranchEnum<SlotConflictPolicy>(dto.ConflictPolicy),
        RetentionPolicy = ParseBranchEnum<SlotRetentionPolicy>(dto.RetentionPolicy),
        EnterDurationUs = dto.EnterDurationUs, ExitDurationUs = dto.ExitDurationUs,
        Easing = ParseBranchEnum<AnimationEasing>(dto.Easing), ExitEasing = ParseBranchEnum<AnimationEasing>(dto.ExitEasing),
        EnterFrom = MapRequiredTransform(dto.EnterFrom, DefaultSlotAnimation.EnterFrom, path + ".enterFrom", issues),
        Hold = MapRequiredTransform(dto.Hold, DefaultSlotAnimation.Hold, path + ".hold", issues),
        ExitTo = MapRequiredTransform(dto.ExitTo, DefaultSlotAnimation.ExitTo, path + ".exitTo", issues),
    };
}
