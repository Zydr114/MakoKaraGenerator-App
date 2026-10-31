using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.TestSupport;

internal static class LeadInFixture
{
    public static Project Create(bool slots = false, bool crossClip = false)
    {
        LyricLine Line(string id, long start, long end, long singStart, long singEnd) => LyricLine.Create(id,"歌詞",
            new(start,end),TimingStatus.Edited,[LyricToken.Create(id+"-a","歌",new(singStart,singStart+1_000_000),TimingStatus.Edited),
                LyricToken.Create(id+"-b","詞",new(singStart+1_000_000,singEnd),TimingStatus.Edited)]);
        var lines = new[] { Line("first",11_500_000,16_500_000,12_000_000,16_000_000),
            Line("second",17_000_000,21_000_000,17_500_000,20_000_000),
            Line("chorus",39_000_000,44_000_000,40_000_000,43_000_000),
            Line("tail-a",46_000_000,49_000_000,46_500_000,48_500_000),
            Line("tail-b",50_000_000,53_000_000,50_500_000,52_500_000),
            Line("tail-c",54_000_000,57_000_000,54_500_000,56_500_000) };
        lines[0] = lines[0] with { RubyAnnotations = [RubyAnnotation.Create("first-ruby","first-a","か")] };
        var lyrics = LyricsDocument.Create(lines);
        var translation = LyricBranch.Create("translation","翻译",LyricBranchRole.Translation,
            [.. lines.Select(line => LyricLine.Create("translation-"+line.Id,"Translation") with { PrimaryLineId = line.Id })],
            "en",LyricBranchTimingMode.FollowPrimary);
        var project = Project.Create("前奏完整案例",Composition.Create(640,360,FrameRate.Fps30,RgbaColor.Black,57_000_000),
            lyrics with { Branches = lyrics.Branches.Add(translation) },lyricStyle:LyricStyle.Default with
            { FontFamilyName="Noto Sans CJK SC",FontSizePx=32,UnsungColor=RgbaColor.White,SungColor=new(255,0,0,255) });
        var profile = SlotSequenceAnimationProfile.Default with { MaxVisibleLines=3,EnterDurationUs=0,ExitDurationUs=0,
            Slots=[.. SlotSequenceAnimationProfile.Default.Slots,SlotSequenceSlot.Create("third",ScenePlacement.Create(new(.5,.5)))] };
        project = PresentationEditor.SetBranch(project,new() { BranchId="primary",SlotSequenceProfile=slots?profile:null });
        project = PresentationEditor.SetBranch(project,new() { BranchId="translation",DefaultPlacement=ScenePlacement.Create(new(.5,.9)) });
        var tree = project.LyricPresentation; var program = tree.FindProgram("primary")!;
        if (crossClip)
            tree = tree with { Nodes=tree.Nodes.Add(new() { Id="center",ParentId=program.RootContainerId,Layout=new() }),
                Programs=tree.Programs.Replace(program,program with { Clips=[new() { Id="chorus-clip",LineIds=["chorus"],ContainerId="center" }] }) };
        project = project with { LyricPresentation=tree };
        if (!LeadInEditor.TryCreateCountdown(project,program.RootContainerId,"三点",3,out string id,out project,out var report))
            throw new InvalidOperationException(report.ToSummary());
        foreach (string target in new[] {"first","chorus"})
            if (!LeadInEditor.TrySetLeadIn(project,program.Id,new() { Id="cue-"+target,TargetLineId=target,IndicatorContainerId=id },out project,out report))
                throw new InvalidOperationException(report.ToSummary());
        return project;
    }
}
