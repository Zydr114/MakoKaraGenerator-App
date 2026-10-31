using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class LeadInSamplingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 准备起点行起点与归零边界保持身份且动画仅入场一次(bool slots)
    {
        var project = LeadInFixture.Create(slots);
        var tree = project.LyricPresentation;
        project = project with { LyricPresentation=tree with { LineOverrides=tree.LineOverrides.Add(new()
            { BranchId="primary",LineId="first",EnterAnimation=new() { DurationUs=4_000_000,Easing=AnimationEasing.Linear,StartOpacity=0,StartOffsetPx=0 } }) } };
        var snapshot = ProjectSnapshot.FromProject(project);
        Assert.DoesNotContain(LyricSceneSampler.SampleFrame(snapshot,8_999_999).Lines,l => l.Branch.Id=="primary");
        var start = LyricSceneSampler.SampleFrame(snapshot,9_000_000);
        var ready = start.Lines.Single(l => l.Branch.Id=="primary");
        Assert.Equal(SceneDisplayPhase.Preparation,ready.DisplayPhase); Assert.Equal(0,ready.Transform.Opacity);
        Assert.Single(start.LeadIns); Assert.Equal(0,start.LeadIns[0].ElapsedMarks);
        var before = LyricSceneSampler.SampleFrame(snapshot,11_999_999);
        var singing = LyricSceneSampler.SampleFrame(snapshot,12_000_000);
        var after = singing.Lines.Single(l => l.Branch.Id=="primary");
        Assert.Equal(ready.BindingId,after.BindingId); Assert.Equal(ready.ParentId,after.ParentId);
        Assert.Equal(SceneDisplayPhase.Performance,after.DisplayPhase); Assert.Empty(singing.LeadIns);
        Assert.Equal(.75,after.Transform.Opacity,6);
        Assert.All(before.Lines.Single(l => l.Branch.Id=="primary").Frame.Tokens,t => Assert.Equal(0,t.Progress));
        Assert.Equal(0,before.Lines.Single(l => l.Branch.Id=="primary").Frame.LineProgress);
        Assert.Equal(2,before.LeadIns[0].ElapsedMarks);
        Assert.Equal(SceneDisplayPhase.Preparation,before.Lines.Single(l => l.Branch.Id=="translation").DisplayPhase);
        AssertSameScene(singing, LyricSceneSampler.SampleFrame(snapshot,12_000_000));
    }

    [Fact]
    public void 长间奏目标等到前奏才揭示且沿用第三槽位不复活旧句()
    {
        var project = LeadInFixture.Create(true); var snapshot = ProjectSnapshot.FromProject(project);
        var before = LyricSceneSampler.SampleFrame(snapshot,36_999_999);
        Assert.DoesNotContain(before.Lines,l => l.Frame.Line!.Id=="chorus");
        var ready = LyricSceneSampler.SampleFrame(snapshot,37_000_000);
        var line = ready.Lines.Single(l => l.Branch.Id=="primary");
        Assert.Equal("chorus",line.Frame.Line!.Id);
        Assert.Equal(project.LyricPresentation.FindNode(line.ContainerId)!.Layout!.SlotIds[2],line.ParentId);
        var after = LyricSceneSampler.SampleFrame(snapshot,40_000_000);
        Assert.Contains(after.Lines,l => l.BindingId==line.BindingId);
        Assert.DoesNotContain(after.Lines,l => l.Frame.Line!.Id is "first" or "second");
        Assert.Equal(project.Lyrics, snapshot.Lyrics);
    }

    [Fact]
    public void 未来居中片段准备装载与行起点切换不改变身份恢复槽位从首槽()
    {
        var project = LeadInFixture.Create(true,true); var snapshot = ProjectSnapshot.FromProject(project);
        var ready = LyricSceneSampler.SampleFrame(snapshot,38_000_000).Lines.Single(l => l.Branch.Id=="primary");
        var atLine = LyricSceneSampler.SampleFrame(snapshot,39_000_000).Lines.Single(l => l.Branch.Id=="primary");
        var atSing = LyricSceneSampler.SampleFrame(snapshot,40_000_000).Lines.Single(l => l.Branch.Id=="primary");
        Assert.Equal("center",ready.ContainerId); Assert.Equal("chorus-clip",ready.DisplayClipId);
        Assert.Equal(ready.BindingId,atLine.BindingId); Assert.Equal(ready.BindingId,atSing.BindingId);
        var restored = LyricSceneSampler.SampleFrame(snapshot,46_500_000).Lines.First(l => l.Frame.Line!.Id=="tail-a");
        Assert.Equal(project.LyricPresentation.FindNode(restored.ContainerId)!.Layout!.SlotIds[0],restored.ParentId);
        Assert.Equal(39_000_000,project.Lyrics.Lines[2].Time!.Value.StartUs);
    }

    [Fact]
    public void 关闭前奏完全恢复原采样且随机倒退无播放历史依赖()
    {
        var project = LeadInFixture.Create(true); var tree = project.LyricPresentation; var program=tree.FindProgram("primary")!;
        var disabled=project with { LyricPresentation=tree with { Programs=tree.Programs.Replace(program,program with
            { LeadIns=[.. program.LeadIns.Select(c => c with { Enabled=false })] }) } };
        var removed=disabled with { LyricPresentation=disabled.LyricPresentation with
            { Programs=disabled.LyricPresentation.Programs.Replace(disabled.LyricPresentation.FindProgram("primary")!,program with { LeadIns=[] }) } };
        var snapshot=ProjectSnapshot.FromProject(project);
        foreach (long time in new long[] {40_100_000,10_000_000,38_000_000,9_000_000,44_000_000,10_000_000})
        {
            AssertSameScene(LyricSceneSampler.SampleFrame(ProjectSnapshot.FromProject(removed),time),
                LyricSceneSampler.SampleFrame(ProjectSnapshot.FromProject(disabled),time));
            var a=LyricSceneSampler.SampleFrame(snapshot,time); var b=LyricSceneSampler.SampleFrame(snapshot,time);
            AssertSameScene(a,b);
        }
        Assert.False(PresentationValidator.Validate(project).HasErrors);
    }

    private static void AssertSameScene(SceneFrame expected, SceneFrame actual)
    {
        Assert.Equal(expected.LeadIns.ToArray(), actual.LeadIns.ToArray());
        Assert.Equal(expected.Lines.Length, actual.Lines.Length);
        for (int i = 0; i < expected.Lines.Length; i++)
        {
            var a = expected.Lines[i]; var b = actual.Lines[i];
            Assert.Equal(a.Branch.Id, b.Branch.Id);
            Assert.Equal(a.Frame.Line!.Id, b.Frame.Line!.Id);
            Assert.Equal(a.BindingId, b.BindingId); Assert.Equal(a.ParentId, b.ParentId);
            Assert.Equal(a.ContainerId, b.ContainerId); Assert.Equal(a.Transform, b.Transform);
            Assert.Equal(a.EnterProgress, b.EnterProgress); Assert.Equal(a.ExitProgress, b.ExitProgress);
            Assert.Equal(a.DisplayPhase, b.DisplayPhase); Assert.Equal(a.CueId, b.CueId);
            Assert.Equal(a.Frame.LineProgress, b.Frame.LineProgress);
            Assert.Equal(a.Frame.LineProgressSource, b.Frame.LineProgressSource);
            Assert.Equal(a.Frame.CurrentTokenIndex, b.Frame.CurrentTokenIndex);
            Assert.Equal(a.Frame.TokensMatchLineText, b.Frame.TokensMatchLineText);
            Assert.Equal(a.Frame.Tokens.ToArray(), b.Frame.Tokens.ToArray());
        }
    }
}
