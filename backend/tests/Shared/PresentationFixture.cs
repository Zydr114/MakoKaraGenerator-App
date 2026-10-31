using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.TestSupport;

internal static class PresentationFixture
{
    public static Project WithLinePresentation(this Project project, string lineId,
        ScenePlacement? placement = null, LineAnimation? animation = null)
    {
        string branchId = project.Lyrics.Branches.First(branch => branch.Lines.Any(line => line.Id == lineId)).Id;
        var branch = project.LyricPresentation.Resolve(branchId);
        var source = project.Lyrics.AllLines.First(line => line.Id == lineId);
        var sourceBranch = project.Lyrics.Branches.First(item => item.Id == branchId);
        var basis = branch.DefaultPlacement ?? LinePlacementEditor.ResolveDefault(LyricStyleResolver.Resolve(project.LyricStyle,
            lineOverride: source.StyleOverride, branchOverride: sourceBranch.StyleOverride).Style);
        if (placement is not null) branch = branch with { DefaultPlacement = basis with { Anchor = placement.Anchor } };
        var local = placement is null ? null : new LyricLocalTransform { OffsetXRatio = placement.Position.X - basis.Position.X,
            OffsetYRatio = placement.Position.Y - basis.Position.Y, RotationDegrees = placement.RotationDegrees - basis.RotationDegrees,
            Scale = placement.Scale / basis.Scale };
        var current = branch.FindLine(lineId) ?? new() { LineId = lineId };
        return project with { LyricPresentation = project.LyricPresentation.SetBranch(
            PresentationEditor.SetLine(branch, lineId, current with { LocalTransform = local, EnterAnimation = animation })) };
    }
}
