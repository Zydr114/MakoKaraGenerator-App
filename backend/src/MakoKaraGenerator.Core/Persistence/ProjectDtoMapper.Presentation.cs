using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Core.Persistence;

internal static partial class ProjectDtoMapper
{
    private static LyricPresentationDto ToDto(LyricPresentationDefinition tree) => new()
    {
        Nodes = [.. tree.Nodes.Select(node => new SceneContainerDto { Id = node.Id, Name = node.Name, ParentId = node.ParentId,
            Placement = ToDto(node.Placement), ReferenceWidthRatio = node.ReferenceRect.WidthRatio,
            ReferenceHeightRatio = node.ReferenceRect.HeightRatio, Layout = node.Layout is not { } layout ? null : new()
            { Kind = layout.Kind.ToString(), DefaultPlacement = ToDto(layout.DefaultPlacement), SafeArea = ToDto(layout.SafeArea),
                MaxWidthRatio = layout.MaxWidthRatio, SlotIds = [.. layout.SlotIds], SlotAnimation = ToDto(layout.SlotAnimation) },
            Content = ToDto(node.Content), Attachment = node.Attachment is null ? null : new() { Mode = node.Attachment.Mode.ToString() } })],
        Programs = [.. tree.Programs.Select(program => new LyricDisplayProgramDto { Id = program.Id, BranchId = program.BranchId,
            RootContainerId = program.RootContainerId, DefaultContainerId = program.DefaultContainerId,
            AnimationPolicy = program.AnimationPolicy?.ToString(), Clips = [.. program.Clips.Select(clip => new LyricDisplayClipDto
            { Id = clip.Id, Name = clip.Name, LineIds = [.. clip.LineIds], ContainerId = clip.ContainerId,
                ScheduleContext = clip.ScheduleContext is null ? null : new() { LineIds = [.. clip.ScheduleContext.LineIds], IsDefaultSegment = clip.ScheduleContext.IsDefaultSegment,
                    PreviousLineId = clip.ScheduleContext.PreviousLineId, NextLineId = clip.ScheduleContext.NextLineId } })],
            LeadIns = [.. program.LeadIns.Select(ToDto)], LeadInGeneration = ToDto(program.LeadInGeneration) })],
        LineOverrides = [.. tree.LineOverrides.Select(line => new LinePresentationDto { BranchId = line.BranchId, LineId = line.LineId,
            LocalTransform = line.LocalTransform is not { } local ? null : new() { OffsetXRatio = local.OffsetXRatio,
                OffsetYRatio = local.OffsetYRatio, RotationDegrees = local.RotationDegrees, Scale = local.Scale },
            EnterAnimation = ToDto(line.EnterAnimation) })],
    };

    internal static LyricPresentationDefinition MapPresentation(LyricPresentationDto? dto, List<ProjectIssue> issues)
    {
        if (dto?.Nodes is null || dto.Programs is null || dto.LineOverrides is null)
        {
            DiagnosticList.Add(issues, DiagnosticCode.InvalidDisplayProgram, IssueSeverity.Error,
                "缺少通用展示树、显示程序或行覆盖集合。", "project.lyricPresentation");
            return LyricPresentationDefinition.Empty;
        }
        return new()
        {
            Nodes = [.. dto.Nodes.Select((node, index) => new SceneContainerDefinition { Id = node.Id ?? string.Empty, Name = node.Name, ParentId = node.ParentId,
                Placement = MapRequiredPlacement(node.Placement, $"project.lyricPresentation.nodes[{index}].placement", issues),
                ReferenceRect = new(node.ReferenceWidthRatio, node.ReferenceHeightRatio), Layout = node.Layout is not { } layout ? null : new()
                { Kind = ParseBranchEnum<LyricContainerLayoutKind>(layout.Kind), DefaultPlacement = MapPlacement(layout.DefaultPlacement, "project.lyricPresentation.nodes.layout.defaultPlacement", issues),
                    SafeArea = MapSafeArea(layout.SafeArea), MaxWidthRatio = layout.MaxWidthRatio, SlotIds = layout.SlotIds is null ? [] : [.. layout.SlotIds],
                    SlotAnimation = MapSlotAnimation(layout.SlotAnimation, $"project.lyricPresentation.nodes[{index}].layout.slotAnimation", issues) },
                Content = MapContent(node.Content, $"project.lyricPresentation.nodes[{index}].content", issues),
                Attachment = node.Attachment is null ? null : new() { Mode = ParseBranchEnum<SceneAttachmentMode>(node.Attachment.Mode) } })],
            Programs = [.. dto.Programs.Select(program => new LyricDisplayProgram { Id = program.Id ?? string.Empty, BranchId = program.BranchId ?? string.Empty,
                RootContainerId = program.RootContainerId ?? string.Empty, DefaultContainerId = program.DefaultContainerId ?? string.Empty,
                AnimationPolicy = program.AnimationPolicy is null ? null : ParseBranchEnum<LyricBranchAnimationPolicy>(program.AnimationPolicy),
                Clips = program.Clips is null ? [] : [.. program.Clips.Select(clip => new LyricDisplayClip
                { Id = clip.Id ?? string.Empty, Name = clip.Name, LineIds = clip.LineIds is null ? [] : [.. clip.LineIds], ContainerId = clip.ContainerId ?? string.Empty,
                    ScheduleContext = clip.ScheduleContext is null ? null : new() { LineIds = clip.ScheduleContext.LineIds is null ? [] : [.. clip.ScheduleContext.LineIds],
                        IsDefaultSegment = clip.ScheduleContext.IsDefaultSegment, PreviousLineId = clip.ScheduleContext.PreviousLineId, NextLineId = clip.ScheduleContext.NextLineId } })],
                LeadIns = program.LeadIns is null ? [] : [.. program.LeadIns.Select(MapLeadIn)],
                LeadInGeneration = MapGeneration(program.LeadInGeneration) })],
            LineOverrides = [.. dto.LineOverrides.Select(line => new LyricLinePresentation { BranchId = line.BranchId ?? string.Empty, LineId = line.LineId ?? string.Empty,
                LocalTransform = line.LocalTransform is not { } local ? null : new() { OffsetXRatio = local.OffsetXRatio,
                    OffsetYRatio = local.OffsetYRatio, RotationDegrees = local.RotationDegrees, Scale = local.Scale },
                EnterAnimation = MapLineAnimation(line.EnterAnimation, "project.lyricPresentation.lineOverrides.enterAnimation", issues) })],
        };
    }
}
