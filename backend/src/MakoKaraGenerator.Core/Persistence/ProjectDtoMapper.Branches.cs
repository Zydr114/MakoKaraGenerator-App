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
    private static SlotAnimationSpec? MapSlotAnimation(SlotAnimationDto? dto) => dto is null ? null : new()
    {
        RevealMode = ParseBranchEnum<SlotRevealMode>(dto.RevealMode), RevealProgress = dto.RevealProgress,
        RevealDelayUs = dto.RevealDelayUs, RevealLeadUs = dto.RevealLeadUs, MaxVisibleLines = dto.MaxVisibleLines,
        ConflictPolicy = ParseBranchEnum<SlotConflictPolicy>(dto.ConflictPolicy),
        RetentionPolicy = ParseBranchEnum<SlotRetentionPolicy>(dto.RetentionPolicy),
        EnterDurationUs = dto.EnterDurationUs, ExitDurationUs = dto.ExitDurationUs,
        Easing = ParseBranchEnum<AnimationEasing>(dto.Easing), ExitEasing = ParseBranchEnum<AnimationEasing>(dto.ExitEasing),
        EnterFrom = MapTransform(dto.EnterFrom)!, Hold = MapTransform(dto.Hold)!, ExitTo = MapTransform(dto.ExitTo)!,

    };
}
