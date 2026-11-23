using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Persistence;

internal static partial class ProjectDtoMapper
{
    private static SafeAreaDto ToDto(NormalizedSafeArea area) => new()
    { Left = area.Left, Top = area.Top, Right = area.Right, Bottom = area.Bottom };
    private static NormalizedSafeArea MapSafeArea(SafeAreaDto? dto) => dto is null ? NormalizedSafeArea.Full
        : new(dto.Left, dto.Top, dto.Right, dto.Bottom);
    private static SlotAnimationDto? ToDto(SlotAnimationSpec? profile) => profile is null ? null : new()
    {
        RevealMode = profile.RevealMode.ToString(), RevealProgress = profile.RevealProgress,
        RevealDelayUs = profile.RevealDelayUs, RevealLeadUs = profile.RevealLeadUs, MaxVisibleLines = profile.MaxVisibleLines,
        ConflictPolicy = profile.ConflictPolicy.ToString(), RetentionPolicy = profile.RetentionPolicy.ToString(),
    };

    private static SlotAnimationSpec? MapSlotAnimation(SlotAnimationDto? dto) => dto is null ? null : new()
    {
        RevealMode = ParseBranchEnum<SlotRevealMode>(dto.RevealMode), RevealProgress = dto.RevealProgress,
        RevealDelayUs = dto.RevealDelayUs, RevealLeadUs = dto.RevealLeadUs, MaxVisibleLines = dto.MaxVisibleLines,
        ConflictPolicy = ParseBranchEnum<SlotConflictPolicy>(dto.ConflictPolicy),
        RetentionPolicy = ParseBranchEnum<SlotRetentionPolicy>(dto.RetentionPolicy),
    };
}
