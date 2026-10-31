using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

internal static class StaticSnapshotFixture
{
    internal const string Family = "Noto Sans CJK SC";

    internal static ProjectSnapshot Create()
    {
        Project project = LeadInFixture.Create(slots: true, crossClip: true);
        var gradient = new GradientSpec { AngleDegrees = 36, Stops = [new(0, new(255, 100, 30, 160)), new(1, new(90, 30, 255, 190))] };
        var primary = project.Lyrics.PrimaryBranch!;
        var first = primary.Lines[0] with
        {
            StyleOverride = new()
            {
                Typography = new() { FontSizePx = 38, FontFamilyName = OptionalValue.Set<string?>(Family) },
                UnsungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = new MaterialSpec.Solid(new(70, 170, 255, 190)) }),
                Shadow = OptionalValue.Set<ShadowSpecOverride?>(null),
            },
        };
        var tree = project.LyricPresentation;
        var program = tree.FindProgram("primary")!;
        var countdown = tree.Nodes.Single(node => node.Content is CountdownSequenceContent);
        program = program with
        {
            Clips = [.. program.Clips.Select(clip => clip with { ScheduleContext = new() { LineIds = clip.LineIds } })],
            LeadInGeneration = new() { IndicatorContainerId = countdown.Id, ExcludedLineIds = ["first"] },
        };
        project = project with
        {
            Name = "snapshot源 — 歌詞与場景",
            Composition = project.Composition with { FrameRate = FrameRate.NtscVideo, BackgroundColor = new(20, 35, 55, 160) },
            Lyrics = project.Lyrics.ReplaceBranch(primary with { Lines = primary.Lines.SetItem(0, first) }),
            LyricStylePresetId = "fixed-origin-only",
            EnterAnimation = new() { DurationUs = 150_000, StartOpacity = .5f, StartOffsetPx = 7 },
            LyricStyle = project.LyricStyle with
            {
                Typography = project.LyricStyle.Typography with { FontFamilyName = Family, FallbackFamilyNames = [Family] },
                UnsungFill = new() { Material = new MaterialSpec.Gradient(gradient) },
                SungFill = new() { Material = new MaterialSpec.Solid(new(255, 100, 70, 210)) },
                Stroke = new() { Material = new MaterialSpec.Gradient(gradient with { AngleDegrees = 90 }), WidthPx = 2 },
                Shadow = new() { Color = new(0, 0, 0, 100), AngleDegrees = 17, DistancePx = 3, BlurRadiusPx = 2 },
            },
            LyricPresentation = tree with { Programs = tree.Programs.Replace(tree.FindProgram("primary")!, program) },
        };
        return ProjectSnapshot.FromProject(project);
    }
}
