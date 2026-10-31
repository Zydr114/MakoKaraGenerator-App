using System.Collections.Immutable;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Application.Tests;

public sealed class LyricBranchEditUseCaseTests
{
    [Fact]
    public void 新增空翻译沿FollowPrimary且不写外观展示并一次撤销恢复()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;
        var result = LyricBranchEditUseCase.AddTranslation(workspace, new(workspace, workspace.Generation, source.Lyrics, "  翻译  ", "zh-CN"));
        Assert.True(result.Accepted); Assert.True(result.Changed); Assert.Null(result.Error);
        var branch = Assert.Single(result.Project.Lyrics.Branches.Where(item => !source.Lyrics.Branches.Any(old => old.Id == item.Id)));
        Assert.StartsWith("translation-", branch.Id); Assert.Equal(branch.Id, result.SelectedBranchId);
        Assert.Equal("翻译", branch.Name); Assert.Equal("zh-CN", branch.Language);
        Assert.Equal(LyricBranchRole.Translation, branch.Role); Assert.Equal(LyricBranchTimingMode.FollowPrimary, branch.TimingMode);
        Assert.Empty(branch.Lines); Assert.Null(branch.StyleOverride);
        Assert.Same(source.Lyrics.PrimaryBranch, result.Project.Lyrics.PrimaryBranch);
        Assert.Same(source.LyricPresentation, result.Project.LyricPresentation);
        Assert.Same(source.LyricStyle, result.Project.LyricStyle);
        Assert.Same(source.PresetApplications, result.Project.PresetApplications); Assert.Same(source.Effects, result.Project.Effects);
        Assert.Null(result.Project.LyricPresentation.FindProgram(branch.Id));
        Assert.Equal("branch.add", workspace.UndoName); Assert.Equal(1, workspace.History.UndoCount); Assert.Equal(1, notifications);
        Assert.Same(source, workspace.Undo()); Assert.Same(result.Project, workspace.Redo());
    }

    [Fact]
    public void 新增原歌词门禁允许无关设置而重复请求不能再新增()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var request = new LyricTranslationBranchAddRequest(workspace, workspace.Generation, workspace.Project.Lyrics, "新增");
        workspace.Execute(ProjectCommand.Create("canvas", project => CompositionEditor.SetCanvasSize(project, 641, 361)));
        var result = LyricBranchEditUseCase.AddTranslation(workspace, request);
        Assert.True(result.Accepted); Assert.Equal(641, result.Project.Composition.CanvasWidth);
        AssertRejected(workspace, () => LyricBranchEditUseCase.AddTranslation(workspace, request), LyricBranchEditErrorCode.TargetStale);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void 四类入口拒绝旧工作区代次即使工程引用相同(int kind)
    {
        ProjectWorkspace workspace = WithRedo(Sample());
        Func<LyricBranchEditResult> operation = Capture(workspace, kind);
        workspace.StartNew(workspace.Project);
        AssertRejected(workspace, operation, LyricBranchEditErrorCode.WorkspaceStale);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void 四类入口拒绝另一工作区冒用相同工程(int kind)
    {
        Project source = Sample();
        ProjectWorkspace expected = ProjectWorkspace.Adopt(source), current = WithRedo(source);
        AssertRejected(current, Capture(current, kind, expected), LyricBranchEditErrorCode.WorkspaceStale);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void 新增删除配置在相关歌词原值变化后拒绝覆盖(int kind)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        Func<LyricBranchEditResult> operation = Capture(workspace, kind);
        workspace.Execute(ProjectCommand.Create("line", project => ReplaceBranch(project,
            Translation(project) with { Lines = [Translation(project).Lines[0] with { Text = "晚到" }] })));
        AssertRejected(workspace, operation, LyricBranchEditErrorCode.TargetStale);
    }

    [Fact]
    public void 删除主来源拒绝并保留重做()
    {
        ProjectWorkspace workspace = WithRedo(Sample());
        AssertRejected(workspace, () => LyricBranchEditUseCase.Remove(workspace, Baseline(workspace, "primary")), LyricBranchEditErrorCode.PrimaryBranchProtected);
    }

    [Fact]
    public void 删除清理来源程序和行覆盖但保留节点效果预设及原诊断()
    {
        Project source = Sample();
        var presentation = source.LyricPresentation.Resolve("translation") with
            { Lines = [new() { LineId = "translated", EnterAnimation = new() { DurationUs = 17 } }] };
        source = source with { LyricPresentation = source.LyricPresentation.SetBranch(presentation) };
        var package = EffectPackageFixture.Contents();
        var effect = new ProjectEffectInstance { Id = "direct", Name = "直接效果", PackageId = package.PackageId, Revision = package.Revision,
            ExportId = "scale", Binding = new(ProjectEffectBindingKind.Tokens, ids: ["translated-token"]) };
        var preset = StyleTemplate.Create("preset", "固定样式", StyleTemplateKind.Composite, source.LyricStyle, [LyricStyleProperty.SungColor]);
        source = source with { Effects = new([package], [effect]), PresetApplications = new([new("application",
            new(ProjectEffectBindingKind.Branch, "translation"), preset)]) };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var result = LyricBranchEditUseCase.Remove(workspace, Baseline(workspace));
        Assert.True(result.Accepted); Assert.True(result.Changed); Assert.Equal("primary", result.SelectedBranchId);
        Assert.Single(result.Project.Lyrics.Branches); Assert.Same(source.Lyrics.PrimaryBranch, result.Project.Lyrics.PrimaryBranch);
        Assert.Null(result.Project.LyricPresentation.FindProgram("translation"));
        Assert.DoesNotContain(result.Project.LyricPresentation.LineOverrides, line => line.BranchId == "translation");
        Assert.Equal(source.LyricPresentation.Nodes, result.Project.LyricPresentation.Nodes);
        Assert.Same(source.Effects, result.Project.Effects); Assert.Same(source.PresetApplications, result.Project.PresetApplications);
        Assert.Same(source.LyricStyle, result.Project.LyricStyle);
        Assert.True(ProjectValidator.Validate(result.Project, ValidationContext.Open).HasErrors);
        Assert.Equal("branch.remove", workspace.UndoName); Assert.Equal(1, workspace.History.UndoCount);
        Assert.Same(source, workspace.Undo()); Assert.Same(result.Project, workspace.Redo());
    }

    [Fact]
    public void 配置元数据样式与选定行关联合为一次历史且保留完整歌词数据()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        LyricBranch original = Translation(source);
        var style = new LyricStyleOverride { FontSizePx = 25, SungColor = RgbaColor.Black };
        var request = new LyricBranchConfigureEditRequest(Baseline(workspace),
            new("读音", "ja", LyricBranchRole.Annotation, LyricBranchTimingMode.Independent, style), new("translated", null, "main"));
        var result = LyricBranchEditUseCase.Configure(workspace, request);
        Assert.True(result.Accepted); Assert.True(result.Changed); Assert.Null(result.SelectedBranchId);
        LyricBranch edited = Translation(result.Project);
        Assert.Equal("读音", edited.Name); Assert.Equal("ja", edited.Language); Assert.Equal(LyricBranchRole.Annotation, edited.Role);
        Assert.Equal(LyricBranchTimingMode.Independent, edited.TimingMode); Assert.Same(style, edited.StyleOverride);
        Assert.Equal(original.Lines[0] with { PrimaryLineId = "main" }, edited.Lines[0]);
        Assert.Same(original.Lines[0].Tokens[0], edited.Lines[0].Tokens[0]);
        Assert.Same(original.Lines[0].RubyAnnotations[0], edited.Lines[0].RubyAnnotations[0]);
        Assert.Same(source.Lyrics.PrimaryBranch, result.Project.Lyrics.PrimaryBranch);
        Assert.Same(source.LyricPresentation, result.Project.LyricPresentation);
        Assert.Equal("branch.configure", workspace.UndoName); Assert.Equal(1, workspace.History.UndoCount);
        Assert.Same(source, workspace.Undo()); Assert.Same(result.Project, workspace.Redo());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 显式相同元数据关联和展示保留引用干净重做且不补默认存储(bool missingProgram)
    {
        Project source = Sample();
        if (missingProgram) source = source with { LyricPresentation = LyricPresentationDefinition.Empty };
        ProjectWorkspace workspace = WithRedo(source);
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;
        LyricBranch branch = Translation(source);
        var originalPresentation = source.LyricPresentation.Resolve(branch.Id);
        var request = new LyricBranchConfigureEditRequest(Baseline(workspace), Configuration(branch),
            new("translated", null, null), originalPresentation, originalPresentation with { Lines = [.. originalPresentation.Lines] });
        var result = LyricBranchEditUseCase.Configure(workspace, request);
        Assert.True(result.Accepted); Assert.False(result.Changed); Assert.Null(result.Error);
        Assert.Same(source, result.Project); Assert.Same(source, workspace.Project);
        Assert.Same(branch, Translation(result.Project)); Assert.Same(branch.Lines[0], Translation(result.Project).Lines[0]);
        Assert.Equal(branch.Lines, Translation(result.Project).Lines);
        Assert.Same(source.LyricPresentation, result.Project.LyricPresentation);
        Assert.False(workspace.IsDirty); Assert.Equal(0, workspace.History.UndoCount); Assert.Equal(1, workspace.History.RedoCount);
        Assert.Equal(0, notifications);
        if (missingProgram) Assert.Empty(result.Project.LyricPresentation.Programs);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void 原非法元数据的相同配置也拒绝不能借无变化绕过用户校验(int kind)
    {
        Project source = Sample();
        LyricBranch branch = Translation(source);
        branch = kind switch { 0 => branch with { Name = " " }, 1 => branch with { TimingMode = (LyricBranchTimingMode)99 },
            _ => branch with { StyleOverride = new() { FontSizePx = -1 } } };
        ProjectWorkspace workspace = WithRedo(ReplaceBranch(source, branch));
        AssertRejected(workspace, () => LyricBranchEditUseCase.Configure(workspace,
            new(Baseline(workspace), Configuration(branch))), LyricBranchEditErrorCode.InvalidConfiguration);
    }

    [Fact]
    public void 显式相同非法展示投影拒绝但未写展示的无变化配置保留旧树()
    {
        Project source = Sample();
        var tree = source.LyricPresentation;
        var program = tree.FindProgram("translation")!;
        var container = tree.FindNode(program.DefaultContainerId)!;
        source = source with { LyricPresentation = tree with { Nodes = tree.Nodes.SetItem(tree.Nodes.IndexOf(container),
            container with { Layout = container.Layout! with { MaxWidthRatio = 0 } }) } };
        ProjectWorkspace workspace = WithRedo(source);
        var baseline = Baseline(workspace);
        var unchanged = new LyricBranchConfigureEditRequest(baseline, Configuration(baseline.ExpectedBranch));
        var noop = LyricBranchEditUseCase.Configure(workspace, unchanged);
        Assert.True(noop.Accepted); Assert.False(noop.Changed); Assert.Same(source, workspace.Project);
        var invalid = source.LyricPresentation.Resolve("translation");
        Assert.False(invalid.IsValid);
        AssertRejected(workspace, () => LyricBranchEditUseCase.Configure(workspace,
            unchanged with { ExpectedPresentation = invalid, Presentation = invalid }), LyricBranchEditErrorCode.InvalidPresentation);
        Assert.Equal(1, workspace.History.RedoCount);
    }

    [Theory]
    [InlineData("primary", LyricBranchRole.Translation)] [InlineData("translation", LyricBranchRole.Primary)]
    public void 配置不能改变显式主来源角色(string branchId, LyricBranchRole role)
    {
        ProjectWorkspace workspace = WithRedo(Sample());
        var baseline = Baseline(workspace, branchId);
        AssertRejected(workspace, () => LyricBranchEditUseCase.Configure(workspace,
            new(baseline, Configuration(baseline.ExpectedBranch) with { Role = role })), LyricBranchEditErrorCode.PrimaryBranchProtected);
    }

    [Fact]
    public void 配置和删除原来源门禁允许无关主来源与合成变化()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var baseline = Baseline(workspace);
        workspace.Execute(ProjectCommand.Create("other", project => ReplaceBranch(project,
            project.Lyrics.PrimaryBranch! with { Name = "当前主来源" }) with { Composition = project.Composition with { CanvasWidth = 641 } }));
        var result = LyricBranchEditUseCase.Configure(workspace, new(baseline, Configuration(baseline.ExpectedBranch) with { Name = "配置" }));
        Assert.True(result.Accepted); Assert.Equal("当前主来源", result.Project.Lyrics.PrimaryBranch!.Name); Assert.Equal(641, result.Project.Composition.CanvasWidth);
        var remove = Baseline(workspace);
        workspace.Execute(ProjectCommand.Create("background", project => project with { Composition = project.Composition with { BackgroundColor = RgbaColor.White } }));
        Assert.True(LyricBranchEditUseCase.Remove(workspace, remove).Accepted);
        Assert.Equal(RgbaColor.White, workspace.Project.Composition.BackgroundColor);
    }

    [Fact]
    public void 高级展示配置保留独占片段自定义容器与槽位身份并可撤销()
    {
        Project source = ExclusiveDisplayFixture.Create();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var baseline = Baseline(workspace, "primary");
        var old = source.LyricPresentation.Resolve("primary");
        var program = source.LyricPresentation.FindProgram("primary")!;
        var slotIds = source.LyricPresentation.FindNode(program.DefaultContainerId)!.Layout!.SlotIds;
        var result = LyricBranchEditUseCase.Configure(workspace, new(baseline, Configuration(baseline.ExpectedBranch),
            ExpectedPresentation: old, Presentation: old with { Placement = ScenePlacement.Create(new(.4, .5)) }));
        Assert.True(result.Accepted); Assert.True(result.Changed); Assert.True(result.ChangeSet.PresentationChanged);
        Assert.Same(source.Lyrics, result.Project.Lyrics);
        Assert.Equal(program.Clips, result.Project.LyricPresentation.FindProgram("primary")!.Clips);
        Assert.Same(source.LyricPresentation.FindNode(ExclusiveDisplayFixture.CenterId), result.Project.LyricPresentation.FindNode(ExclusiveDisplayFixture.CenterId));
        Assert.True(slotIds.SequenceEqual(result.Project.LyricPresentation.FindNode(program.DefaultContainerId)!.Layout!.SlotIds));
        Assert.True(source.LyricPresentation.LineOverrides.SequenceEqual(result.Project.LyricPresentation.LineOverrides));
        Assert.Same(source, workspace.Undo()); Assert.Same(result.Project, workspace.Redo());
    }

    [Fact]
    public void 展示原投影只保护明确目标并保留当前非投影片段()
    {
        Project source = ExclusiveDisplayFixture.Create();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var baseline = Baseline(workspace, "primary");
        var old = source.LyricPresentation.Resolve("primary");
        var request = new LyricBranchConfigureEditRequest(baseline, Configuration(baseline.ExpectedBranch), ExpectedPresentation: old,
            Presentation: old with { Placement = ScenePlacement.Create(new(.4, .5)) });
        workspace.Execute(ProjectCommand.Create("tree", project => project with { LyricPresentation = project.LyricPresentation.SetBranch(
            project.LyricPresentation.Resolve("translation") with { Placement = ScenePlacement.Create(new(.3, .4)) }) with
            { Programs = project.LyricPresentation.Programs.SetItem(0, project.LyricPresentation.Programs[0] with
                { Clips = [project.LyricPresentation.Programs[0].Clips[0] with { Name = "当前片段" }] }) } }));
        Project latest = workspace.Project;
        Assert.True(LyricBranchEditUseCase.Configure(workspace, request).Accepted);
        Assert.Equal(latest.LyricPresentation.FindProgram("primary")!.Clips, workspace.Project.LyricPresentation.FindProgram("primary")!.Clips);
        Assert.Equal(latest.LyricPresentation.Resolve("translation"), workspace.Project.LyricPresentation.Resolve("translation"));
        Assert.Same(latest, workspace.Undo());
    }

    [Fact]
    public void 展示原投影已改则配置拒绝整笔元数据()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var baseline = Baseline(workspace);
        var old = workspace.Project.LyricPresentation.Resolve("translation");
        var request = new LyricBranchConfigureEditRequest(baseline, Configuration(baseline.ExpectedBranch) with { Name = "不应提交" },
            ExpectedPresentation: old, Presentation: old with { Placement = ScenePlacement.Create(new(.4, .5)) });
        workspace.Execute(ProjectCommand.Create("tree", project => project with { LyricPresentation = project.LyricPresentation.SetBranch(
            old with { Placement = ScenePlacement.Create(new(.2, .3)) }) }));
        AssertRejected(workspace, () => LyricBranchEditUseCase.Configure(workspace, request), LyricBranchEditErrorCode.TargetStale);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void 非法展示和不支持的槽位或未知行拒绝整笔配置(int kind)
    {
        ProjectWorkspace workspace = WithRedo(Sample());
        var baseline = Baseline(workspace);
        var old = workspace.Project.LyricPresentation.Resolve("translation");
        var invalid = kind switch { 0 => old with { MaxWidthRatio = 0 }, 1 => old with { SlotSequenceProfile = SlotSequenceAnimationProfile.Default },
            _ => old with { Lines = [new() { LineId = "missing" }] } };
        AssertRejected(workspace, () => LyricBranchEditUseCase.Configure(workspace,
            new(baseline, Configuration(baseline.ExpectedBranch) with { Name = "不应提交" }, ExpectedPresentation: old, Presentation: invalid)),
            LyricBranchEditErrorCode.InvalidPresentation);
    }

    [Fact]
    public void 已有独占片段拒绝None时间策略且不提交元数据和展示()
    {
        ProjectWorkspace workspace = WithRedo(ExclusiveDisplayFixture.Create());
        var baseline = Baseline(workspace, "primary");
        var old = workspace.Project.LyricPresentation.Resolve("primary");
        AssertRejected(workspace, () => LyricBranchEditUseCase.Configure(workspace,
            new(baseline, Configuration(baseline.ExpectedBranch) with { Name = "不应提交", TimingMode = LyricBranchTimingMode.None },
                ExpectedPresentation: old, Presentation: old with { Placement = ScenePlacement.Create(new(.4, .5)) })), LyricBranchEditErrorCode.DomainValidationFailed);
    }

    [Theory]
    [InlineData(LyricBranchTimingMode.None)] [InlineData(LyricBranchTimingMode.Independent)] [InlineData(LyricBranchTimingMode.FollowPrimary)]
    public void 主行关联允许三种时间策略并空值解除只改变关联(LyricBranchTimingMode mode)
    {
        Project source = Sample();
        source = ReplaceBranch(source, Translation(source) with { TimingMode = mode });
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        var result = LyricBranchEditUseCase.LinkPrimaryLine(workspace, Link(workspace, "main"));
        Assert.True(result.Accepted); Assert.True(result.Changed);
        Assert.Equal(Translation(source).Lines[0] with { PrimaryLineId = "main" }, Translation(result.Project).Lines[0]);
        Assert.Equal(mode, Translation(result.Project).TimingMode);
        Assert.Equal(1, workspace.History.UndoCount); Assert.Same(source, workspace.Undo()); Assert.Same(result.Project, workspace.Redo());
        var unlinked = LyricBranchEditUseCase.LinkPrimaryLine(workspace, Link(workspace, null));
        Assert.True(unlinked.Accepted); Assert.True(unlinked.Changed); Assert.Null(Translation(unlinked.Project).Lines[0].PrimaryLineId);
        Assert.Equal(2, workspace.History.UndoCount); Assert.Same(result.Project, workspace.Undo());
    }

    [Fact]
    public void 关联使用最新行字段不覆盖捕获后正文时间注音样式变化()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var request = Link(workspace, "main");
        workspace.Execute(ProjectCommand.Create("row", project => ReplaceBranch(project, Translation(project) with { Lines =
            [Translation(project).Lines[0] with { Text = "当前", Time = new(17, 29), TimingStatus = TimingStatus.Imported,
                StyleOverride = new() { FontSizePx = 30 }, RubyAnnotations = [Translation(project).Lines[0].RubyAnnotations[0] with { Text = "new" }] }] })));
        LyricLine latest = Translation(workspace.Project).Lines[0];
        Project before = workspace.Project;
        var result = LyricBranchEditUseCase.LinkPrimaryLine(workspace, request);
        Assert.True(result.Accepted); Assert.Equal(latest with { PrimaryLineId = "main" }, Translation(result.Project).Lines[0]);
        Assert.Same(latest.Tokens[0], Translation(result.Project).Lines[0].Tokens[0]);
        Assert.Same(before, workspace.Undo());
    }

    [Theory]
    [InlineData("missing")] [InlineData("translated")]
    public void 新关联必须指向当前真实主来源行(string primaryId)
    {
        ProjectWorkspace workspace = WithRedo(Sample());
        AssertRejected(workspace, () => LyricBranchEditUseCase.LinkPrimaryLine(workspace, Link(workspace, primaryId)), LyricBranchEditErrorCode.InvalidPrimaryLine);
        AssertRejected(workspace, () => LyricBranchEditUseCase.Configure(workspace,
            new(Baseline(workspace), Configuration(Translation(workspace.Project)), new("translated", null, primaryId))), LyricBranchEditErrorCode.InvalidPrimaryLine);
    }

    [Fact]
    public void 原悬空关联未改保持Warning而相同请求无变化保留重做()
    {
        Project source = Sample();
        source = ReplaceBranch(source, Translation(source) with { Lines = [Translation(source).Lines[0] with { PrimaryLineId = "deleted-main" }] });
        ProjectWorkspace workspace = WithRedo(source);
        var noop = LyricBranchEditUseCase.LinkPrimaryLine(workspace, Link(workspace, "deleted-main"));
        Assert.True(noop.Accepted); Assert.False(noop.Changed); Assert.Same(source, workspace.Project); Assert.Equal(1, workspace.History.RedoCount);
        var result = LyricBranchEditUseCase.Configure(workspace, new(Baseline(workspace), Configuration(Translation(source)) with { Name = "改名" },
            new("translated", "deleted-main", "deleted-main")));
        Assert.True(result.Accepted); Assert.Equal("deleted-main", Translation(result.Project).Lines[0].PrimaryLineId);
        Assert.Contains(ProjectValidator.Validate(result.Project, ValidationContext.Open).Issues,
            issue => issue.Code == DiagnosticCode.MissingLyricAssociation && issue.Severity == IssueSeverity.Warning);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void 关联原值变化或稳定来源行消失拒绝晚到请求(int kind)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        var request = Link(workspace, "main");
        workspace.Execute(ProjectCommand.Create("other", project => kind == 2
            ? project with { Lyrics = project.Lyrics with { Branches = project.Lyrics.Branches.Remove(Translation(project)) } }
            : ReplaceBranch(project, Translation(project) with { Lines = kind == 1 ? [] : [Translation(project).Lines[0] with { PrimaryLineId = "main" }] })));
        AssertRejected(workspace, () => LyricBranchEditUseCase.LinkPrimaryLine(workspace, request), LyricBranchEditErrorCode.TargetStale);
    }

    [Fact]
    public void 主来源行不能关联自身()
    {
        ProjectWorkspace workspace = WithRedo(Sample());
        AssertRejected(workspace, () => LyricBranchEditUseCase.LinkPrimaryLine(workspace,
            new(workspace, workspace.Generation, "primary", "main", null, "main")), LyricBranchEditErrorCode.PrimaryBranchProtected);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void 四类展示候选拒绝保留旧工程且不返回成功选择(int kind)
    {
        Project source = Sample();
        source = source with { LyricPresentation = source.LyricPresentation with { Nodes = source.LyricPresentation.Nodes.Add(source.LyricPresentation.Nodes[0]) } };
        ProjectWorkspace workspace = WithRedo(source);
        AssertRejected(workspace, Capture(workspace, kind), LyricBranchEditErrorCode.DomainValidationFailed);
    }

    private static Project Sample()
    {
        var range = new TimeRange(0, 1_000_007);
        var main = LyricLine.Create("main", "主", range, TimingStatus.Edited, [LyricToken.Create("main-token", "主", range, TimingStatus.Edited)]);
        var translated = LyricLine.Create("translated", "译", new(2, 23), TimingStatus.Imported, [LyricToken.Create("translated-token", "译")]) with
            { StyleOverride = new() { FontSizePx = 20 }, RubyAnnotations = [RubyAnnotation.Create("translated-ruby", "translated-token", "やく")] };
        var lyrics = LyricsDocument.Create([main]);
        var branch = LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation, [translated], "en", LyricBranchTimingMode.FollowPrimary)
            with { StyleOverride = new() { FontSizePx = 22 } };
        return Project.Create("来源", lyrics: lyrics with { Branches = lyrics.Branches.Add(branch) }, lyricStylePresetId: "retained-preset");
    }

    private static LyricBranch Translation(Project project) => project.Lyrics.Branches.First(branch => branch.Id == "translation");
    private static Project ReplaceBranch(Project project, LyricBranch branch) => project with { Lyrics = project.Lyrics.ReplaceBranch(branch) };
    private static LyricBranchEditBaseline Baseline(ProjectWorkspace workspace, string id = "translation") => new(workspace, workspace.Generation, id,
        workspace.Project.Lyrics.Branches.First(branch => branch.Id == id));
    private static LyricBranchConfiguration Configuration(LyricBranch branch) => new(branch.Name, branch.Language, branch.Role, branch.TimingMode, branch.StyleOverride);
    private static LyricPrimaryLineLinkEditRequest Link(ProjectWorkspace workspace, string? id) => new(workspace, workspace.Generation, "translation", "translated",
        Translation(workspace.Project).Lines[0].PrimaryLineId, id);
    private static ProjectWorkspace WithRedo(Project source)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        workspace.Execute(ProjectCommand.Create("probe", project => project with { Name = "changed" }));
        workspace.Undo(); return workspace;
    }

    private static Func<LyricBranchEditResult> Capture(ProjectWorkspace workspace, int kind, ProjectWorkspace? expected = null)
    {
        expected ??= workspace;
        var add = new LyricTranslationBranchAddRequest(expected, expected.Generation, expected.Project.Lyrics, "新增");
        var baseline = Baseline(expected);
        var configuration = new LyricBranchConfigureEditRequest(baseline, Configuration(baseline.ExpectedBranch) with { Name = "改名" });
        var link = Link(expected, "main");
        return kind switch { 0 => () => LyricBranchEditUseCase.AddTranslation(workspace, add), 1 => () => LyricBranchEditUseCase.Remove(workspace, baseline),
            2 => () => LyricBranchEditUseCase.Configure(workspace, configuration), _ => () => LyricBranchEditUseCase.LinkPrimaryLine(workspace, link) };
    }

    private static void AssertRejected(ProjectWorkspace workspace, Func<LyricBranchEditResult> operation, LyricBranchEditErrorCode code)
    {
        Project source = workspace.Project;
        int undo = workspace.History.UndoCount, redo = workspace.History.RedoCount, notifications = 0;
        bool dirty = workspace.IsDirty;
        workspace.History.Changed += OnChanged;
        try
        {
            LyricBranchEditResult result = operation();
            Assert.False(result.Accepted); Assert.False(result.Changed); Assert.Equal(code, result.Error?.Code); Assert.Null(result.SelectedBranchId);
            Assert.Same(source, result.Project); Assert.Same(source, workspace.Project);
            Assert.Equal(undo, workspace.History.UndoCount); Assert.Equal(redo, workspace.History.RedoCount);
            Assert.Equal(dirty, workspace.IsDirty); Assert.Equal(0, notifications);
        }
        finally { workspace.History.Changed -= OnChanged; }
        void OnChanged(object? sender, EventArgs args) => notifications++;
    }
}
