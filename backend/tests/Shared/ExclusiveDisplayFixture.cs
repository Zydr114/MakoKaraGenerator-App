using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.TestSupport;

internal static class ExclusiveDisplayFixture
{
    public const string CenterId = "chorus-container";
    public const string ClipId = "chorus-clip";
    public const long StartUs = 4_000_000;
    public const long EndUs = 6_000_000;

    public static Project Create()
    {
        var lines = Enumerable.Range(0, 5).Select(index =>
        {
            string id = "line-" + index;
            var range = new TimeRange(index * 2_000_000L, (index + 1) * 2_000_000L);
            string text = index == 2 ? "特別" : "歌詞";
            return LyricLine.Create(id, text, range, TimingStatus.Edited,
                [LyricToken.Create(id + "-token", text, range, TimingStatus.Edited)]);
        }).ToArray();
        lines[2] = lines[2] with { StyleOverride = new() { UnsungColor = RgbaColor.Parse("#FFD080"), SungColor = RgbaColor.Parse("#FF8040") },
            RubyAnnotations = [RubyAnnotation.Create("center-ruby", "line-2-token", "とくべつ")] };
        var lyrics = LyricsDocument.Create(lines);
        var translation = LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
            [.. lines.Select(line => LyricLine.Create("translation-" + line.Id, "translation") with { PrimaryLineId = line.Id })],
            "en", LyricBranchTimingMode.FollowPrimary) with { StyleOverride = new() { FontSizePx = 14 } };
        var project = Project.Create("独占案例", Composition.Create(640, 360, FrameRate.Fps30, RgbaColor.Parse("#000000"), 10_000_000),
            lyrics with { Branches = lyrics.Branches.Add(translation) },
            lyricStyle: LyricStyle.Default with { FontSizePx = 32, UnsungColor = RgbaColor.White, SungColor = RgbaColor.White });
        var tree = LyricPresentationDefinition.FromBranches([
            new() { BranchId = "primary", SlotSequenceProfile = SlotSequenceAnimationProfile.Default with { EnterDurationUs = 0, ExitDurationUs = 0 } },
            new() { BranchId = "translation", DefaultPlacement = ScenePlacement.Create(new(.5, .86)), SafeArea = NormalizedSafeArea.Translation }]);
        var program = tree.FindProgram("primary")!;
        tree = tree with { Nodes = tree.Nodes.Add(new() { Id = CenterId, ParentId = program.RootContainerId, Layout = new() }),
            Programs = tree.Programs.SetItem(0, program with { Clips = [new() { Id = ClipId, LineIds = ["line-2"], ContainerId = CenterId }] }),
            LineOverrides = [new() { BranchId = "primary", LineId = "line-2", EnterAnimation = new() { DurationUs = 400_000, StartOffsetPx = 10 } }] };
        return project with { LyricPresentation = tree };
    }
}
