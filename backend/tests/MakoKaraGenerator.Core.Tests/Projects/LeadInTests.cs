using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class LeadInTests
{
    private static readonly string[] GeneratedTargets = ["first", "chorus"];
    internal static Project Create(out string indicatorId)
    {
        LyricLine Line(string id, long start, long end, long singStart, long singEnd) => LyricLine.Create(id, "唱",
            new(start, end), TimingStatus.Imported, [LyricToken.Create(id + "-word", "唱", new(singStart, singEnd), TimingStatus.Imported)]);
        var project = Project.Create("前奏", lyrics: LyricsDocument.Create([
            Line("first",11_500_000,16_500_000,12_000_000,16_000_000),
            Line("second",17_000_000,21_000_000,17_500_000,20_000_000),
            Line("chorus",39_000_000,44_000_000,40_000_000,43_000_000)]));
        Assert.True(LeadInEditor.TryCreateCountdown(project, project.LyricPresentation.Programs[0].RootContainerId,
            "三点", 3, out indicatorId, out var result, out var report), report.ToSummary());
        return result;
    }
    private static LyricLeadIn Cue(string indicator, string line = "first") => new()
        { Id = "cue-" + line, TargetLineId = line, IndicatorContainerId = indicator };
    private static Project Add(Project project, LyricLeadIn cue)
    {
        Assert.True(LeadInEditor.TrySetLeadIn(project, project.LyricPresentation.Programs[0].Id, cue, out var result, out var report), report.ToSummary());
        return result;
    }

    [Fact]
    public void 提示内容前奏与显式关闭覆盖完整往返且不写入源歌词()
    {
        using TemporaryDirectory directory = new();
        var project = Create(out string indicator);
        var sourceLyrics = project.Lyrics;
        var group = project.LyricPresentation.FindNode(indicator)!;
        var sequence = (CountdownSequenceContent)group.Content!;
        var mark = project.LyricPresentation.FindNode(sequence.MarkNodeIds[0])!;
        Assert.True(SceneTreeEditor.TrySetNode(project, mark with { Content = new TextMarkContent { Text = "★",
            AppearanceOverride = new() { Pending = new() { Strokes = StrokeLayers.Empty }, Opacity = .5f,
                Typography = new() { FontSizePx = 48 }, PendingFill = OptionalValue.Set<FillSpecOverride?>(new() { Color = OptionalValue.Set<RgbaColor?>(new(255,0,0,255)) }) } } }, out project, out _));
        project = Add(project, Cue(indicator));
        Assert.Same(sourceLyrics, project.Lyrics);
        ProjectStore.Save(project, directory.Path);
        var reopened = ProjectStore.Load(directory.Path);
        Assert.False(reopened.Report.HasErrors, reopened.Report.ToSummary());
        Assert.Equal(project, reopened.Project); Assert.Equal(project.GetHashCode(), reopened.Project!.GetHashCode());
        string lyrics = ProjectFileFixture.ReadLyrics(directory.Path);
        Assert.DoesNotContain("leadIns", lyrics); Assert.DoesNotContain("TextMark", lyrics); Assert.DoesNotContain("★", lyrics);
        var sameTree = project.LyricPresentation with { Nodes = [.. project.LyricPresentation.Nodes.Select(node => node.Content is CountdownSequenceContent s
            ? node with { Content = s with { MarkNodeIds = [.. s.MarkNodeIds] } } : node)],
            Programs = [.. project.LyricPresentation.Programs.Select(p => p with { LeadIns = [.. p.LeadIns] })] };
        Assert.True(PresentationEditor.TrySetTree(project, sameTree, out var unchanged, out _)); Assert.Same(project, unchanged);
    }

    [Fact]
    public void 起唱锚点保留源状态及未定时前缀且全缺时使用行起点()
    {
        var project = Create(out _); var line = project.Lyrics.Lines[0];
        Assert.Equal(new LeadInAnchor(12_000_000, TimingStatus.Imported, false, false), LeadInTiming.Anchor(line));
        var prefixed = line with { Tokens = [LyricToken.Create("missing","未"), .. line.Tokens] };
        Assert.True(LeadInTiming.Anchor(prefixed)!.Value.HasMissingPrefix);
        Assert.Equal(line.Time, LeadInTiming.Occupancy(prefixed));
        Assert.Equal(new LeadInAnchor(11_500_000,TimingStatus.Imported,true,false), LeadInTiming.Anchor(line with { Tokens = [] }));
        Assert.Null(LeadInTiming.Range(line,13_000_000)); Assert.Equal(new TimeRange(9_000_000,12_000_000), LeadInTiming.Range(line,3_000_000));
    }

    [Fact]
    public void 等时步进使用整数微秒半开边界且大时间不溢出()
    {
        var range = new TimeRange(100,110);
        Assert.Equal(new long[] {100,103,106,110},Enumerable.Range(0,4).Select(index => LeadInTiming.StepBoundary(range,3,index)));
        Assert.Equal(0,LeadInTiming.ElapsedMarks(range,3,102)); Assert.Equal(1,LeadInTiming.ElapsedMarks(range,3,103));
        Assert.Equal(3,LeadInTiming.ElapsedMarks(range,3,110));
        var huge = new TimeRange(0,long.MaxValue);
        Assert.Equal(long.MaxValue,LeadInTiming.StepBoundary(huge,int.MaxValue,int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => LeadInTiming.StepBoundary(new(0,2),3,0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LeadInTiming.ElapsedMarks(new(0,2),3,-1));
    }

    [Fact]
    public void 自动分析开头与长间奏候选不写工程且重复应用无变化()
    {
        var project = Create(out string indicator); var program = project.LyricPresentation.Programs[0];
        var settings = new LeadInGenerationSettings { IndicatorContainerId = indicator };
        var analysis = LeadInGeneration.Analyze(project,program.Id,settings);
        Assert.False(analysis.Report.HasErrors,analysis.Report.ToSummary());
        Assert.Equal(GeneratedTargets,analysis.Candidates.Where(c => c.Status == LeadInCandidateStatus.New).Select(c => c.TargetLineId));
        Assert.All(analysis.Candidates.Where(c => c.Proposed is not null),c => Assert.Equal(TimingStatus.Imported,c.Anchor!.Value.TimingStatus));
        Assert.Empty(program.LeadIns);
        Assert.True(LeadInGeneration.TryApply(project,program.Id,settings,["first","chorus"],out var applied,out var report),report.ToSummary());
        Assert.Same(project.Lyrics,applied.Lyrics); Assert.Equal(project.LyricPresentation.Nodes.ToArray(),applied.LyricPresentation.Nodes.ToArray());
        Assert.True(LeadInGeneration.TryApply(applied,program.Id,settings,["first","chorus"],out var unchanged,out _)); Assert.Same(applied,unchanged);
        Assert.All(unchanged.LyricPresentation.Programs[0].LeadIns,c => Assert.Equal(LeadInOrigin.Generated,c.Origin));
    }

    [Fact]
    public void 手工修改禁用及明确排除受到保护且删除保留容器()
    {
        var project = Create(out string indicator); string programId = project.LyricPresentation.Programs[0].Id;
        var settings = new LeadInGenerationSettings { IndicatorContainerId = indicator };
        Assert.True(LeadInGeneration.TryApply(project,programId,settings,["first","chorus"],out project,out _));
        var generated = project.LyricPresentation.Programs[0].LeadIns[0];
        Assert.True(LeadInEditor.TrySetLeadIn(project,programId,generated,out var unchanged,out _)); Assert.Same(project,unchanged);
        Assert.True(LeadInEditor.TrySetLeadIn(project,programId,generated with { DurationUs = 2_000_000 },out project,out _));
        var manual = project.LyricPresentation.Programs[0].LeadIns.First(c => c.TargetLineId == "first");
        Assert.Equal(LeadInOrigin.Manual,manual.Origin);
        Assert.Equal(LeadInCandidateReason.ExistingManual,LeadInGeneration.Analyze(project,programId,settings).Candidates[0].Reason);
        Assert.True(LeadInEditor.TrySetLeadIn(project,programId,manual with { Enabled = false },out project,out _));
        Assert.Equal(LeadInCandidateReason.Disabled,LeadInGeneration.Analyze(project,programId,settings).Candidates[0].Reason);
        var chorus = project.LyricPresentation.Programs[0].LeadIns.First(c => c.TargetLineId == "chorus");
        Assert.True(LeadInEditor.TryDeleteLeadIn(project,programId,chorus.Id,true,out project,out _));
        Assert.NotNull(project.LyricPresentation.FindNode(indicator));
        settings = project.LyricPresentation.Programs[0].LeadInGeneration!;
        Assert.Equal(LeadInCandidateReason.Excluded,LeadInGeneration.Analyze(project,programId,settings).Candidates[2].Reason);
    }

    [Fact]
    public void 不足完整提示与未知中间时间均跳过而不改变源数据()
    {
        var project = Create(out string indicator);
        var shortFirst = project.Lyrics.Lines[0] with { Time = new(0,2_000_000), Tokens = [project.Lyrics.Lines[0].Tokens[0] with { Time = new(1_000_000,2_000_000) }] };
        var unknown = project.Lyrics.Lines[1] with { Time = null, TimingStatus = TimingStatus.Missing, Tokens = [] };
        project = project with { Lyrics = project.Lyrics with { Lines = [shortFirst, unknown, project.Lyrics.Lines[2]] } };
        var original = project;
        var analysis = LeadInGeneration.Analyze(project,project.LyricPresentation.Programs[0].Id,new() { IndicatorContainerId = indicator });
        Assert.False(analysis.Report.HasErrors);
        Assert.Equal(new[] {LeadInCandidateReason.TooShort,LeadInCandidateReason.MissingLineTime,LeadInCandidateReason.UnknownPrecedingTime},analysis.Candidates.Select(c => c.Reason));
        Assert.Same(original,project); Assert.Empty(project.LyricPresentation.Programs[0].LeadIns);
    }

    [Theory]
    [InlineData("missing-target")]
    [InlineData("missing-container")]
    [InlineData("negative-duration")]
    [InlineData("insufficient-duration")]
    [InlineData("singing-conflict")]
    public void 无效配置明确拒绝并保留原实例(string defect)
    {
        var project = Create(out string indicator);
        var cue = Cue(indicator) with { TargetLineId = defect == "missing-target" ? "absent" : defect == "singing-conflict" ? "second" : "first",
            IndicatorContainerId = defect == "missing-container" ? "absent" : indicator,
            DurationUs = defect == "negative-duration" ? -1 : defect == "insufficient-duration" ? 13_000_000 : 3_000_000 };
        Assert.False(LeadInEditor.TrySetLeadIn(project,project.LyricPresentation.Programs[0].Id,cue,out var unchanged,out var report));
        Assert.True(report.HasErrors); Assert.Same(project,unchanged);
    }

    [Fact]
    public void 容器复制重映射标记且不复制前奏绑定引用删除被拒绝()
    {
        var project = Create(out string indicator); project = Add(project,Cue(indicator));
        var original = (CountdownSequenceContent)project.LyricPresentation.FindNode(indicator)!.Content!;
        Assert.True(SceneTreeEditor.TryCopySubtree(project,indicator,"副本",out string copyId,out var copied,out var report),report.ToSummary());
        var sequence = (CountdownSequenceContent)copied.LyricPresentation.FindNode(copyId)!.Content!;
        Assert.Equal(3,sequence.MarkNodeIds.Length); Assert.Empty(sequence.MarkNodeIds.Intersect(original.MarkNodeIds));
        Assert.All(sequence.MarkNodeIds,id => Assert.Equal(copyId,copied.LyricPresentation.FindNode(id)!.ParentId));
        Assert.Single(copied.LyricPresentation.Programs[0].LeadIns);
        Assert.False(SceneTreeEditor.TryDeleteNode(project,indicator,out var unchanged,out var deletion)); Assert.Same(project,unchanged);
        Assert.Contains("cue-first",deletion.ToSummary());
        Assert.True(LeadInEditor.TryReorderMarks(project,indicator,original.MarkNodeIds.Reverse().ToArray(),out var reordered,out _));
        Assert.Equal(project.LyricPresentation.Nodes.Select(n => n.Placement),reordered.LyricPresentation.Nodes.Select(n => n.Placement));
    }

    [Fact]
    public void 嵌套小父组创建提示仍使用等距四十像素参考框()
    {
        var project=Create(out _); var root=project.LyricPresentation.Programs[0].RootContainerId;
        var parent=new SceneContainerDefinition { Id="small",ParentId=root,ReferenceRect=new(.25,.25) };
        Assert.True(SceneTreeEditor.TryAddNodes(project,[parent],out project,out _));
        Assert.True(LeadInEditor.TryCreateCountdown(project,parent.Id,"三点",3,out string group,out project,out var report),report.ToSummary());
        var geometry=SceneTreeGeometryResolver.Resolve(project.LyricPresentation.Nodes,project.Composition.CanvasWidth,project.Composition.CanvasHeight);
        Assert.Equal(120,geometry[group].Width,8); Assert.Equal(40,geometry[group].Height,8);
        var points=((CountdownSequenceContent)project.LyricPresentation.FindNode(group)!.Content!).MarkNodeIds;
        Assert.All(points,id => Assert.Equal(40,geometry[id].Width,8));
    }

    [Theory]
    [InlineData("UnknownContent","EqualDuration")]
    [InlineData("CountdownSequence","UnknownSteps")]
    public void 当前格式拒绝未知内容及步进方式(string kind,string stepMode)
    {
        using TemporaryDirectory directory = new();
        var project = Create(out string indicator); ProjectStore.Save(project,directory.Path);
        string path = Path.Combine(directory.Path,ProjectJson.ProjectFileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        var node = json["lyricPresentation"]!["nodes"]!.AsArray().First(n => n!["id"]!.GetValue<string>() == indicator)!;
        node["content"]!["kind"] = kind; node["content"]!["stepMode"] = stepMode;
        File.WriteAllText(path,json.ToJsonString());
        Assert.True(ProjectStore.Load(directory.Path).Report.HasErrors);
    }

    [Fact]
    public void 明确删除源行清理前奏与排除项而保留提示节点()
    {
        var project = Create(out string indicator); project = Add(project,Cue(indicator));
        var program = project.LyricPresentation.Programs[0];
        var tree = project.LyricPresentation with { Programs = [program with { LeadInGeneration = new() { IndicatorContainerId = indicator,ExcludedLineIds = ["first"] } }] };
        var lyrics = project.Lyrics with { Lines = project.Lyrics.Lines.RemoveAt(0) };
        var cleaned = tree.RemoveDeletedSources(lyrics);
        Assert.Empty(cleaned.Programs[0].LeadIns); Assert.Empty(cleaned.Programs[0].LeadInGeneration!.ExcludedLineIds);
        Assert.NotNull(cleaned.FindNode(indicator)); Assert.False(PresentationValidator.Validate(project with { Lyrics = lyrics,LyricPresentation = cleaned }).HasErrors);
    }

    [Fact]
    public void 前奏容器可配置矢量图形自定义SVG动画模式与首字上方附着并持久化()
    {
        using TemporaryDirectory directory = new();
        var project = Create(out string indicator);
        project = Add(project, Cue(indicator));
        var shape = new CountdownShapeSpec
        {
            Kind = CountdownShapeKind.CustomSvg,
            SvgXml = "<svg viewBox=\"0 0 24 24\"><circle cx=\"12\" cy=\"12\" r=\"10\"/></svg>",
            SvgColorMode = CountdownSvgColorMode.OriginalColor,
            BarMinHeightRatio = 0.6,
        };
        var animation = new CountdownAnimationSpec
        {
            Mode = CountdownAnimationMode.BlinkThenWipeFill,
            BlinkRatio = 0.4,
            BlinkCount = 3,
            FeatherRatio = 0.08,
        };
        Assert.True(LeadInEditor.TrySetCountdownPresentation(project, indicator, shape, animation, out project, out var report), report.ToSummary());
        var geometry = SceneTreeGeometryResolver.Resolve(project.LyricPresentation.Nodes, project.Composition.CanvasWidth, project.Composition.CanvasHeight);
        var previous = geometry[indicator];
        var parentId = project.LyricPresentation.FindNode(indicator)!.ParentId!;
        Assert.True(LeadInEditor.TrySetAttachment(project, indicator, SceneAttachmentMode.CueTargetAboveStart, previous, geometry[parentId], out project, out report), report.ToSummary());

        ProjectStore.Save(project, directory.Path);
        var loaded = ProjectStore.Load(directory.Path);
        Assert.False(loaded.Report.HasErrors, loaded.Report.ToSummary());
        var node = loaded.Project!.LyricPresentation.FindNode(indicator)!;
        var sequence = Assert.IsType<CountdownSequenceContent>(node.Content);
        Assert.Equal(shape, sequence.Shape);
        Assert.Equal(animation, sequence.Animation);
        Assert.Equal(SceneAttachmentMode.CueTargetAboveStart, node.Attachment?.Mode);
    }

    [Fact]
    public void 非法前奏图形SVG或动画参数及标记携带图形字段会被拒绝()
    {
        var project = Create(out string indicator);
        var sequence = Assert.IsType<CountdownSequenceContent>(project.LyricPresentation.FindNode(indicator)!.Content);
        string markId = sequence.MarkNodeIds[0];

        Assert.False(LeadInEditor.TrySetCountdownPresentation(project, indicator,
            new() { Kind = CountdownShapeKind.CustomSvg, SvgXml = "<svg><script>alert(1)</script></svg>" },
            CountdownAnimationSpec.Default, out _, out var scriptReport));
        Assert.True(scriptReport.HasErrors);

        Assert.False(LeadInEditor.TrySetCountdownPresentation(project, indicator,
            new() { Kind = CountdownShapeKind.CustomSvg, SvgXml = "<svg><foreignObject/></svg>" },
            CountdownAnimationSpec.Default, out _, out var foreignReport));
        Assert.True(foreignReport.HasErrors);

        Assert.False(LeadInEditor.TrySetCountdownPresentation(project, indicator,
            new() { Kind = CountdownShapeKind.CustomSvg, SvgXml = "<svg><image href=\"#x\"/></svg>" },
            CountdownAnimationSpec.Default, out _, out var imageReport));
        Assert.True(imageReport.HasErrors);

        Assert.False(LeadInEditor.TrySetCountdownPresentation(project, indicator,
            new() { Kind = CountdownShapeKind.CustomSvg, SvgXml = "<svg><rect width=\"10\" height=\"10\"/></svg>" + new string(' ', CountdownShapeSpec.MaximumSvgBytes) },
            CountdownAnimationSpec.Default, out _, out var oversizedReport));
        Assert.True(oversizedReport.HasErrors);

        Assert.False(LeadInEditor.TrySetCountdownPresentation(project, indicator,
            new() { Kind = CountdownShapeKind.SteppedBars, BarMinHeightRatio = 0.05 },
            CountdownAnimationSpec.Default, out _, out _));

        Assert.False(LeadInEditor.TrySetCountdownPresentation(project, indicator,
            CountdownShapeSpec.Default,
            new() { Mode = CountdownAnimationMode.BlinkThenErase, BlinkRatio = 0.9, BlinkCount = 2, FeatherRatio = 0.05 },
            out _, out _));

        Assert.False(LeadInEditor.TrySetCountdownPresentation(project, indicator,
            CountdownShapeSpec.Default,
            new() { Mode = CountdownAnimationMode.BlinkThenWipeFill, BlinkRatio = 0.3, BlinkCount = 0, FeatherRatio = 0.05 },
            out _, out _));

        Assert.False(LeadInEditor.TrySetCountdownPresentation(project, indicator,
            CountdownShapeSpec.Default,
            new() { Mode = CountdownAnimationMode.BlinkThenWipeFill, BlinkRatio = 0.3, BlinkCount = 2, FeatherRatio = 0.6 },
            out _, out _));

        using TemporaryDirectory directory = new();
        ProjectStore.Save(project, directory.Path);
        string path = Path.Combine(directory.Path, ProjectJson.ProjectFileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        var markNode = json["lyricPresentation"]!["nodes"]!.AsArray().First(n => n!["id"]!.GetValue<string>() == markId)!;
        markNode["content"]!["shape"] = new JsonObject { ["kind"] = "Circle" };
        File.WriteAllText(path, json.ToJsonString());
        Assert.True(ProjectStore.Load(directory.Path).Report.HasErrors);
    }
}
