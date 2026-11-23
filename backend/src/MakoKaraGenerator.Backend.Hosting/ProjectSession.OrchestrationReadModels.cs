using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal Task<BackendOperationResult<ProjectOrchestrationResponse>> ReadOrchestrationAsync(
        string? expectedRevision, CancellationToken cancellationToken) => ReadModelAsync(expectedRevision, () =>
        {
            return BuildOrchestration(Workspace.Project, RevisionText);
        }, cancellationToken, "orchestration");

    private BackendOperationResult<ProjectOrchestrationResponse> BuildOrchestration(Project project, string revision)
    {
            // Branch edits may legitimately add a source without materializing its default
            // program yet. The read model exposes that deterministic implicit program while
            // keeping the projection read-only; writers materialize it only when a command
            // actually changes the program.
            var presentation = project.LyricPresentation.WithDefaultPrograms(project.Lyrics);
            ReadModelBudget budget = new();
            budget.Text(ProjectSessionId, revision);
            ProjectOrchestrationNodeDto[] nodes = [.. presentation.Nodes.Select(node => ProjectNode(node, budget))];
            ProjectOrchestrationProgramDto[] programs = [.. presentation.Programs.Select(program =>
            {
                string? recognized = BasicLayoutOperation.Recognize(presentation, program.DefaultContainerId)?.ToString();
                budget.Item(); budget.Text(program.Id, program.BranchId, program.RootContainerId, program.DefaultContainerId, recognized);
                ProjectOrchestrationClipDto[] clips = [.. program.Clips.Select(clip => ProjectClip(clip, budget))];
                return new ProjectOrchestrationProgramDto(program.Id, program.BranchId, program.RootContainerId,
                    program.DefaultContainerId, program.AnimationPolicy?.ToString(),
                    recognized, clips, [.. program.LeadIns.Select(cue => ProjectLeadIn(project, program, cue, budget))],
                    ProjectGenerationSettings(program.LeadInGeneration, budget));
            })];
            budget.Item();
            return BoundedReadModel(new ProjectOrchestrationResponse(ProjectSessionId, revision, nodes, programs));
    }

    private static ProjectOrchestrationNodeDto ProjectNode(SceneContainerDefinition node, ReadModelBudget budget)
    {
        budget.Item(); budget.Text(node.Id, node.Name, node.ParentId);
        ProjectOrchestrationLayoutDto? layout = null;
        if (node.Layout is { } value)
        {
            budget.Item(value.SlotIds.Length); foreach (var id in value.SlotIds) budget.Text(id);
            if (value.SlotAnimation is { } animation)
            {
                budget.Item();
                budget.Text(animation.RevealMode.ToString(), animation.ConflictPolicy.ToString(), animation.RetentionPolicy.ToString());
            }
            layout = new(value.Kind.ToString(), value.DefaultPlacement is null ? null : Placement(value.DefaultPlacement),
                new(value.SafeArea.Left, value.SafeArea.Top, value.SafeArea.Right, value.SafeArea.Bottom), value.MaxWidthRatio,
                value.SlotIds.ToArray(), value.SlotAnimation is null ? null : ProjectSlotAnimation(value.SlotAnimation));
        }
        ProjectOrchestrationContentDto? content = null;
        if (node.Content is CountdownSequenceContent sequence)
        {
            budget.Item(sequence.MarkNodeIds.Length + 2);
            foreach (string id in sequence.MarkNodeIds) budget.Text(id);
            budget.Text(node.Attachment?.Mode.ToString(), sequence.Shape.Kind.ToString(), sequence.Shape.SvgXml,
                sequence.Shape.SvgColorMode.ToString(), sequence.Animation.Mode.ToString());
            content = new(sequence.MarkNodeIds.ToArray(), null, node.Attachment?.Mode.ToString(),
                ProjectSceneAppearance(sequence.Appearance, budget),
                Shape: new(sequence.Shape.Kind.ToString(), sequence.Shape.BarMinHeightRatio, sequence.Shape.SvgXml, sequence.Shape.SvgColorMode.ToString()),
                Animation: new(sequence.Animation.Mode.ToString(), sequence.Animation.BlinkRatio, sequence.Animation.BlinkCount, sequence.Animation.FeatherRatio));
        }
        else if (node.Content is TextMarkContent mark) { budget.Text(mark.Text); content = new(null, mark.Text, null, null, ProjectLocalStyle(mark.AppearanceOverride?.ToStyleOverride(), budget)); }
        return new(node.Id, node.Name, node.ParentId, Placement(node.Placement),
            new(node.ReferenceRect.WidthRatio, node.ReferenceRect.HeightRatio), layout,
            node.Content switch { CountdownSequenceContent => "countdownSequence", TextMarkContent => "textMark", null => null, _ => "unknown" }, content);
    }

    private static ProjectOrchestrationLeadInDto ProjectLeadIn(Project project, LyricDisplayProgram program, LyricLeadIn cue, ReadModelBudget budget)
    {
        budget.Item(); budget.Text(cue.Id, cue.Name, cue.TargetLineId, cue.IndicatorContainerId);
        var line = project.Lyrics.Branches.FirstOrDefault(branch => branch.Id == program.BranchId)?.Lines.FirstOrDefault(line => line.Id == cue.TargetLineId);
        var range = line is null ? null : LeadInTiming.Range(line, cue.DurationUs);
        return new(cue.Id, cue.Name, cue.Enabled, cue.TargetLineId, cue.IndicatorContainerId,
            cue.DurationUs.ToString(System.Globalization.CultureInfo.InvariantCulture), cue.Origin.ToString(),
            range?.StartUs.ToString(System.Globalization.CultureInfo.InvariantCulture), range?.EndUs.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static ProjectOrchestrationPlacementDto Placement(ScenePlacement value) =>
        new(value.Position.X, value.Position.Y, value.Anchor.ToString(), value.RotationDegrees, value.Scale);

    private static ProjectOrchestrationClipDto ProjectClip(LyricDisplayClip clip, ReadModelBudget budget)
    {
        budget.Item(clip.LineIds.Length + 1); budget.Text(clip.Id, clip.Name, clip.ContainerId);
        foreach (string lineId in clip.LineIds) budget.Text(lineId);
        ProjectOrchestrationClipScheduleContextDto? context = null;
        if (clip.ScheduleContext is { } value)
        {
            context = new(value.LineIds.ToArray(), value.IsDefaultSegment, value.PreviousLineId, value.NextLineId);
            budget.Item(value.LineIds.Length + 1); budget.Text(value.PreviousLineId, value.NextLineId);
            foreach (string lineId in value.LineIds) budget.Text(lineId);
        }
        return new(clip.Id, clip.Name, clip.LineIds.ToArray(), clip.ContainerId, context);
    }
}
