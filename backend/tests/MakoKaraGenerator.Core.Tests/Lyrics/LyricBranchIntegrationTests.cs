using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class LyricBranchIntegrationTests
{
    [Fact]
    public void 现行工程保存显式分支注音和完整动态配置并结构往返()
    {
        using TemporaryDirectory directory = new();
        Project project = Example();
        ProjectStore.Save(project, directory.Path);
        var json = JsonNode.Parse(ProjectFileFixture.ReadLyrics(directory.Path))!;
        Assert.Null(json["lines"]);
        Assert.Null(json["language"]);
        Assert.Equal("primary", json["primaryBranchId"]!.GetValue<string>());
        Assert.Equal(2, json["branches"]!.AsArray().Count);
        Assert.Equal("base", json["branches"]![0]!["lines"]![0]!["rubyAnnotations"]![0]!["baseTokenId"]!.GetValue<string>());
        var reopened = ProjectStore.Load(directory.Path);
        Assert.Equal(project, reopened.Project);
        Assert.Equal(project.GetHashCode(), reopened.Project!.GetHashCode());
    }

    [Fact]
    public void 双行整体变换保存重开后保持不变()
    {
        using TemporaryDirectory directory = new();
        Project project = Example();
        var primary = project.LyricPresentation.Resolve("primary") with
        { Placement = ScenePlacement.Create(new(.6, .45), rotationDegrees: 7, scale: .82) };
        project = PresentationEditor.SetBranch(project, primary);
        ProjectStore.Save(project, directory.Path);
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(directory.Path, ProjectJson.ProjectFileName)))!;
        Assert.Equal(.6, json["lyricPresentation"]!["nodes"]![0]!["placement"]!["x"]!.GetValue<double>());
        Assert.Equal(.82, json["lyricPresentation"]!["nodes"]![0]!["placement"]!["scale"]!.GetValue<double>());
        Assert.Equal(project, ProjectStore.Load(directory.Path).Project);
    }

    [Fact]
    public void 单分支编辑保留真实主分支及其他分支无变化仍返回原引用()
    {
        Project project = Example();
        Assert.Same(project, LyricBranchEditor.Edit(project, "translation", view => view));
        Assert.Same(project, LyricBranchEditor.Edit(project, "unknown", _ => throw new InvalidOperationException()));
        Project edited = LyricBranchEditor.Edit(project, "translation", view => view with
        { Lyrics = view.Lyrics with { Lines = view.Lyrics.Lines.SetItem(0, view.Lyrics.Lines[0] with { Text = "新的翻译" }) } });
        Assert.Equal("primary", edited.Lyrics.PrimaryBranchId);
        Assert.Same(project.Lyrics.PrimaryBranch, edited.Lyrics.PrimaryBranch);
        Assert.Equal("新的翻译", edited.Lyrics.Branches[1].Lines[0].Text);
        Assert.Same(edited, LyricBranchEditor.Edit(edited, "translation", view => view with { Lyrics = view.Lyrics with { Lines = view.Lyrics.Lines } }));
    }

    [Fact]
    public void 主行删除报告稳定关联失效且不改译文()
    {
        Project project = Example();
        var translation = project.Lyrics.Branches[1];
        project = project with { Lyrics = project.Lyrics with { Lines = [] } };
        Assert.Same(translation, project.Lyrics.Branches[1]);
        Assert.Contains(ProjectValidator.Validate(project, ValidationContext.Open).Issues,
            issue => issue.Code == DiagnosticCode.MissingLyricAssociation);
    }

    [Fact]
    public void 标识跨分支重复及无效主分支被报告()
    {
        Project project = Example();
        var lyrics = project.Lyrics with { PrimaryBranchId = "absent", Branches = project.Lyrics.Branches.Add(project.Lyrics.Branches[1]) };
        var report = ProjectValidator.Validate(project with { Lyrics = lyrics }, ValidationContext.Open);
        Assert.Contains(report.Issues, issue => issue.Code == DiagnosticCode.InvalidLyricBranch);
        Assert.Contains(report.Issues, issue => issue.Code == DiagnosticCode.DuplicateIdentifier);
    }

    [Fact]
    public void 注音基础字失效及重复活动注音有明确诊断()
    {
        Project project = Example();
        var line = project.Lyrics.Lines[0];
        line = line with { RubyAnnotations = line.RubyAnnotations.Add(line.RubyAnnotations[0] with { Id = "another" })
            .Add(line.RubyAnnotations[0] with { Id = "missing", BaseTokenId = "absent" }) };
        var report = ProjectValidator.Validate(project with { Lyrics = project.Lyrics with { Lines = [line] } }, ValidationContext.Open);
        Assert.Contains(report.Issues, issue => issue.Code == DiagnosticCode.InvalidRubyAnnotation);
        Assert.Contains(report.Issues, issue => issue.Code == DiagnosticCode.MissingLyricAssociation);
    }

    [Fact]
    public void 分支样式先于行和元素解析且来源清晰()
    {
        var result = LyricStyleResolver.Resolve(LyricStyle.Default,
            branchOverride: new() { FontSizePx = 40, UnsungColor = RgbaColor.Black },
            lineOverride: new() { FontSizePx = 50 }, tokenOverride: new() { UnsungColor = RgbaColor.White });
        Assert.Equal(50, result.Style.FontSizePx);
        Assert.Equal(LyricStyleSource.Line, result.SourceOf(LyricStyleProperty.FontSizePx));
        Assert.Equal(LyricStyleSource.Token, result.SourceOf(LyricStyleProperty.UnsungFill));
        Assert.Equal(LyricStyleSource.Branch, LyricStyleResolver.Resolve(branchOverride: new() { Opacity = .5f }).SourceOf(LyricStyleProperty.Opacity));
    }

    [Fact]
    public void 模板全部作用于所有分支且元素模板包括静态分支的元素()
    {
        Project project = Example();
        var lineTemplate = StyleTemplate.Create("test", "测试", StyleTemplateKind.Line,
            LyricStyle.Default with { Opacity = .5f }, [LyricStyleProperty.Opacity]);
        Assert.True(StyleTemplateApplicator.TryPreview(project, lineTemplate, null, null, true, out var preview, out _));
        Assert.Equal(2, preview.TargetCount); Assert.Equal(2, preview.ChangedCount);
        Assert.All(preview.Result.Lyrics.AllLines, line => Assert.Equal(.5f, line.StyleOverride!.Opacity));
        Assert.Equal("primary", preview.Result.Lyrics.PrimaryBranchId);
        var tokenTemplate = lineTemplate with { Kind = StyleTemplateKind.Token };
        Assert.True(StyleTemplateApplicator.TryPreview(project, tokenTemplate, null, null, true, out preview, out _));
        Assert.Equal(1, preview.TargetCount);
        Assert.Equal(.5f, preview.Result.Lyrics.Lines[0].Tokens[0].StyleOverride!.Opacity);
    }

    [Fact]
    public void 行位置批量可跨分支且缺失目标整批拒绝()
    {
        Project project = Example();
        project = PresentationEditor.SetBranch(project, project.LyricPresentation.Resolve("primary") with { SlotSequenceProfile = null });
        var updates = new Dictionary<string, LyricLocalTransform?> { ["primary-line"] = LyricLocalTransform.Neutral, ["translated-line"] = LyricLocalTransform.Neutral };
        Assert.True(LinePlacementEditor.TryApply(project, updates, out var changed));
        Assert.All(changed.Lyrics.AllLines, line => Assert.Equal(LyricLocalTransform.Neutral, changed.LyricPresentation.FindLine(line.Id)!.LocalTransform));
        updates.Add("absent", LyricLocalTransform.Neutral);
        Assert.False(LinePlacementEditor.TryApply(project, updates, out changed)); Assert.Same(project, changed);
    }

    [Fact]
    public void 行复制同时重建注音ID及基础字关联()
    {
        var line = Example().Lyrics.Lines[0];
        int id = 0;
        var copy = LyricLineDuplicator.Duplicate(line, "copy", () => "copied-" + ++id);
        Assert.NotEqual(line.RubyAnnotations[0].Id, copy.RubyAnnotations[0].Id);
        Assert.Equal(copy.Tokens[0].Id, copy.RubyAnnotations[0].BaseTokenId);
    }

    [Fact]
    public void 歌词末尾包含独立分支而不使用关联分支被忽略的自身时间()
    {
        var project = Example();
        var translation = project.Lyrics.Branches[1];
        translation = translation with { Lines = [translation.Lines[0] with { Time = new(0, 9_000_000), TimingStatus = TimingStatus.Edited }] };
        project = project with { Lyrics = project.Lyrics.ReplaceBranch(translation) };
        Assert.Equal(2_000_000, CompositionEditor.LyricsEndUs(project));
        translation = translation with { TimingMode = LyricBranchTimingMode.Independent };
        project = project with { Lyrics = project.Lyrics.ReplaceBranch(translation) };
        Assert.Equal(9_000_000, CompositionEditor.LyricsEndUs(project));
    }

    [Fact]
    public void 整数时间端点附近的百分百触发精确等于行终点()
    {
        long end = long.MaxValue - 1_000;
        var lines = new[] { LyricLine.Create("a", "甲", new TimeRange(0, end), TimingStatus.Edited),
            LyricLine.Create("b", "乙", new TimeRange(end - 1_000, long.MaxValue), TimingStatus.Edited) };
        var profile = SlotSequenceAnimationProfile.Default with { RevealProgress = 1 };
        Assert.DoesNotContain(SlotSequenceAnimationScheduler.Sample(lines, end - 1, profile), entry => entry.LineId == "b");
        Assert.Equal(end, Assert.Single(SlotSequenceAnimationScheduler.Sample(lines, end, profile)).RevealTimeUs);
    }

    private static Project Example()
    {
        var token = LyricToken.Create("base", "字", new TimeRange(0, 1_000_000), TimingStatus.Edited);
        var line = LyricLine.Create("primary-line", "字", new TimeRange(0, 2_000_000), TimingStatus.Edited, [token]) with
        { RubyAnnotations = [RubyAnnotation.Create("ruby", "base", "zi", language: "zh-Latn")] };
        var document = LyricsDocument.Create([line], "zh-CN");
        var primary = document.PrimaryBranch!;
        var primaryPresentation = new LyricBranchPresentation { BranchId = primary.Id,
            SlotSequenceProfile = SlotSequenceAnimationProfile.Default with
            { EnterFrom = new() { Opacity = .2, OffsetXPx = 25, RotationDegrees = -10 }, Hold = new() { Scale = .9 }, ExitTo = new() { Opacity = 0 } } };
        var translation = LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
            [LyricLine.Create("translated-line", "Translated") with { PrimaryLineId = line.Id }], "en", LyricBranchTimingMode.FollowPrimary) with
        { StyleOverride = new() { FontSizePx = 28 } };
        return Project.Create("分支", lyrics: document with { Branches = [primary, translation] }) with
        { LyricPresentation = LyricPresentationDefinition.FromBranches([primaryPresentation, new() { BranchId = translation.Id,
            DefaultPlacement = ScenePlacement.Create(new(.5, .86)), MaxWidthRatio = .8 }]) };
    }
}
