using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.TestSupport;

internal static class GeneralDisplayFixture
{
    public const string DualId = "generic-dual", CenterId = "generic-center", TripleId = "generic-triple";
    public static Project Source()
    {
        var source = ExclusiveDisplayFixture.Create();
        var range = new TimeRange(10_000_000,12_000_000);
        var final = LyricLine.Create("line-5","終曲",range,TimingStatus.Edited,[LyricToken.Create("token-5","終曲",range,TimingStatus.Edited)]);
        var translated = source.Lyrics.Branches[1];
        var lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.SetItem(0,source.Lyrics.PrimaryBranch! with { Lines = [..source.Lyrics.Lines.Select((line,index) => index >= 3 ? line with { Time = new(line.Time!.Value.StartUs,12_000_000) } : line),final] })
            .SetItem(1,translated with { Lines = translated.Lines.Add(LyricLine.Create("translation-line-5","final translation") with { PrimaryLineId = final.Id }) }) };
        return source with { Lyrics = lyrics,Composition = source.Composition with { OutputDurationUs = 12_500_000 },LyricPresentation = LyricPresentationDefinition.Empty };
    }
    public static Project Create()
    {
        var project = Source(); var tree = project.LyricPresentation.WithDefaultPrograms(project.Lyrics); string root = tree.Programs[0].RootContainerId;
        var slots = Enumerable.Range(0,5).Select(index => new SceneContainerDefinition { Id = "generic-slot-"+index,
            ParentId = index < 2 ? DualId : TripleId, Placement = ScenePlacement.Create(new(index < 2 ? (index == 0 ? .28 : .72) : .5,
                index < 2 ? (index == 0 ? .3 : .7) : (.15 + .25 * (index-2)))) }).ToArray();
        var dual = new SceneContainerDefinition { Id = DualId,ParentId = root,Layout = new() { Kind = LyricContainerLayoutKind.SlotSequence,
            SlotIds = [..slots.Take(2).Select(s => s.Id)],SlotAnimation = new() { MaxVisibleLines = 2,RetentionPolicy = SlotRetentionPolicy.UntilLineEnd } } };
        var center = new SceneContainerDefinition { Id = CenterId,ParentId = root,Layout = new() { DefaultPlacement = ScenePlacement.Default } };
        var triple = new SceneContainerDefinition { Id = TripleId,ParentId = root,Layout = new() { Kind = LyricContainerLayoutKind.SlotSequence,
            SlotIds = [..slots.Skip(2).Select(s => s.Id)],SlotAnimation = new() { MaxVisibleLines = 3,RetentionPolicy = SlotRetentionPolicy.UntilLineEnd } } };
        var program = tree.Programs[0] with { DefaultContainerId = DualId,Clips = [new() { Id = "center-clip",ContainerId = CenterId,LineIds = ["line-2"] },
            new() { Id = "triple-clip",ContainerId = TripleId,LineIds = ["line-3","line-4","line-5"] }] };
        tree = tree with { Nodes = tree.Nodes.AddRange(new[] { dual,center,triple }.Concat(slots)),Programs = tree.Programs.SetItem(0,program) };
        if (!PresentationEditor.TrySetTree(project,tree,out var result,out var report)) throw new InvalidOperationException(report.ToSummary());
        return result;
    }
}
