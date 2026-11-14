using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Application.Tests;

public sealed class BasicLayoutTests
{
    [Theory]
    [InlineData(BasicLayoutPreset.SingleCentered, new double[] { .5, .75 }, new[] { ScenePlacementAnchor.Center })]
    [InlineData(BasicLayoutPreset.KaraokeDouble, new double[] { .05, .6, .95, .8 }, new[] { ScenePlacementAnchor.LeftCenter, ScenePlacementAnchor.RightCenter })]
    [InlineData(BasicLayoutPreset.DoubleCentered, new double[] { .5, .55, .5, .77 }, new[] { ScenePlacementAnchor.Center, ScenePlacementAnchor.Center })]
    [InlineData(BasicLayoutPreset.TripleCentered, new double[] { .5, .55, .5, .66, .5, .77 }, new[] { ScenePlacementAnchor.Center, ScenePlacementAnchor.Center, ScenePlacementAnchor.Center })]
    public void 四种预设遵循画布坐标和锚点并且只改展示(BasicLayoutPreset preset, double[] coordinates, ScenePlacementAnchor[] anchors)
    {
        var source = ExclusiveDisplayFixture.Create();
        Assert.True(TryPreview(source, "primary", preset, null, out var preview, out var error), error);
        var tree = preview.Result.LyricPresentation;
        var layout = tree.FindNode(tree.FindProgram("primary")!.DefaultContainerId)!.Layout!;
        Assert.Equal(anchors.Length, layout.SlotIds.Length);
        for (int index = 0; index < anchors.Length; index++)
        {
            var placement = tree.FindNode(layout.SlotIds[index])!.Placement;
            Assert.Equal(new NormalizedPosition(coordinates[2 * index], coordinates[2 * index + 1]), placement.Position);
            Assert.Equal(anchors[index], placement.Anchor);
        }
        Assert.Equal(preset, BasicLayoutOperation.Recognize(tree, tree.FindProgram("primary")!.DefaultContainerId));
        Assert.Same(source.Lyrics, preview.Result.Lyrics);
        Assert.Same(source.LyricStyle, preview.Result.LyricStyle);
        Assert.Same(source.Effects, preview.Result.Effects);
        Assert.Same(source.PresetApplications, preview.Result.PresetApplications);
        Assert.All(source.LyricPresentation.Nodes, node => Assert.Equal(node, tree.FindNode(node.Id)));
        Assert.Equal(source.LyricPresentation.FindProgram("translation"), tree.FindProgram("translation"));
        Assert.True(TryPreview(preview.Result, "primary", preset, null, out var unchanged, out error), error);
        Assert.Same(preview.Result, unchanged.Result);
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(1, 1, 0)]
    [InlineData(3, 4, 0)]
    public void 精确拆分将原身份留给最早未选部分且一次撤销(int first, int last, int retainedStart)
    {
        var source = AutoClipSource();
        var selected = source.Lyrics.Lines[first..(last + 1)].Select(line => line.Id).ToArray();
        var workspace = ProjectWorkspace.Adopt(source);
        Assert.True(TryPreview(source, "primary", BasicLayoutPreset.KaraokeDouble, selected, out var preview, out var error), error);
        Assert.Equal(selected.Length, preview.AffectedLines);
        Assert.Equal(1, preview.SplitClips);
        var clips = preview.Result.LyricPresentation.FindProgram("primary")!.Clips;
        Assert.NotEmpty(preview.NewClipIds);
        Assert.All(preview.NewClipIds, id =>
        {
            Assert.DoesNotContain(id, source.LyricPresentation.FindProgram("primary")!.Clips.Select(clip => clip.Id));
            Assert.Contains(clips, clip => clip.Id == id);
        });
        var retained = clips.Single(clip => clip.Id == ExclusiveDisplayFixture.ClipId);
        Assert.Equal("line-" + retainedStart, retained.LineIds[0]);
        Assert.Equal(ExclusiveDisplayFixture.CenterId, retained.ContainerId);
        Assert.Equal(source.Lyrics.Lines.Select(line => line.Id), clips.SelectMany(clip => clip.LineIds));
        foreach (var clip in clips)
        {
            if (selected.Contains(clip.LineIds[0]))
                Assert.Equal(BasicLayoutPreset.KaraokeDouble, BasicLayoutOperation.Recognize(preview.Result.LyricPresentation, clip.ContainerId));
            else Assert.Equal(ExclusiveDisplayFixture.CenterId, clip.ContainerId);
        }
        using var renderer = new SkiaRenderPipeline();
        foreach (var line in source.Lyrics.Lines.Where(line => !selected.Contains(line.Id)))
        {
            long time = line.Time!.Value.StartUs + 1_000_000;
            using var before = renderer.Render(RenderRequest.At(ProjectSnapshot.FromProject(source), time));
            using var after = renderer.Render(RenderRequest.At(ProjectSnapshot.FromProject(preview.Result), time));
            Assert.True(before.PixelMemory.Span.SequenceEqual(after.PixelMemory.Span));
        }
        workspace.Execute(ProjectCommand.Create("scene.preset", _ => preview.Result));
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.True(TryPreview(workspace.Project, "primary", BasicLayoutPreset.KaraokeDouble, selected, out var unchanged, out error), error);
        Assert.Same(workspace.Project, unchanged.Result);
        workspace.Undo(); Assert.Same(source, workspace.Project);
        workspace.Redo(); Assert.Same(preview.Result, workspace.Project);
    }

    [Fact]
    public async Task 拆分映射直接片段效果并分配固定成员预设身份而不重建种子()
    {
        var package = await EffectPackageTransfer.ReadDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "WordEmphasis"), EffectPackageProfiles.V2);
        var source = AutoClipSource() with { Effects = new([package], [new()
        {
            Id = "range-instance", Name = "整个片段", PackageId = package.PackageId, Revision = package.Revision, ExportId = "word-emphasis",
            Binding = new(ProjectEffectBindingKind.Clips, ids: [ExclusiveDisplayFixture.ClipId]), Seed = 0xfedcba9876543210,
            Subscription = new("branch", "primary"), Enabled = false,
        }]) };
        var preset = StyleTemplate.Create("local", "固定逐字", StyleTemplateKind.Composite,
            source.LyricStyle with { FontSizePx = 42 }, [LyricStyleProperty.FontSizePx],
            [new("recipe", "逐字", TemplateEffectTarget.TokenFamily, package.PackageId, package.Revision, "word-emphasis", fixedSeed: 99)], [package]);
        Assert.True(PresetApplicationsEditor.TryApply(source, preset, [new(ProjectEffectBindingKind.Clips, ids: [ExclusiveDisplayFixture.ClipId])], null,
            out source, out var error), error);
        var originalApplication = Assert.Single(source.PresetApplications.Items);
        Assert.True(TryPreview(source, "primary", BasicLayoutPreset.KaraokeDouble, ["line-2"], out var preview, out error), error);
        var clips = preview.Result.LyricPresentation.FindProgram("primary")!.Clips;
        var instance = Assert.Single(preview.Result.Effects.Instances);
        Assert.Equal(clips.Select(clip => clip.Id), instance.Binding.Ids);
        Assert.Equal(source.Effects.Instances[0] with { Binding = instance.Binding }, instance);
        Assert.Equal(3, preview.Result.PresetApplications.Items.Length);
        Assert.Equal(originalApplication.Id, preview.Result.PresetApplications.Find(originalApplication.Target)!.Id);
        Assert.Equal(originalApplication.Identities.OrderBy(member => member.InstanceId),
            preview.Result.PresetApplications.Items.SelectMany(item => item.Identities).OrderBy(member => member.InstanceId));
        Assert.All(preview.Result.PresetApplications.Items, item =>
        {
            Assert.Same(preset, item.Snapshot);
            Assert.Equal(originalApplication.Enabled, item.Enabled);
            var clip = clips.Single(clip => clip.Id == item.Target.Ids[0]);
            Assert.Equal(clip.LineIds.Length, item.Identities.Length);
        });
        Assert.Equal(5, ProjectSnapshot.FromProject(preview.Result).Effects.Instances.Count(item => item.Id != instance.Id));
        using TemporaryDirectory directory = new();
        ProjectStore.Save(preview.Result, directory.Path);
        var loaded = ProjectStore.Load(directory.Path);
        Assert.NotNull(loaded.Project);
        Assert.Equal(preview.Result.LyricPresentation, loaded.Project.LyricPresentation);
        Assert.Equal(preview.Result.Effects, loaded.Project.Effects);
        Assert.Equal(preview.Result.PresetApplications, loaded.Project.PresetApplications);
    }

    [Fact]
    public async Task 单个范围配方无法拆成多个应用时整次取消()
    {
        var source = AutoClipSource();
        var package = await EffectPackageTransfer.ReadDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "WordEmphasis"), EffectPackageProfiles.V2);
        var preset = StyleTemplate.Create("scope", "范围", StyleTemplateKind.Composite, source.LyricStyle, [],
            [new("recipe", "范围效果", TemplateEffectTarget.Application, package.PackageId, package.Revision, "word-emphasis")], [package]);
        Assert.True(PresetApplicationsEditor.TryApply(source, preset, [new(ProjectEffectBindingKind.Clips, ids: [ExclusiveDisplayFixture.ClipId])], null,
            out source, out var error), error);
        Assert.False(TryPreview(source, "primary", BasicLayoutPreset.KaraokeDouble, ["line-2"], out var preview, out error));
        Assert.Contains("RecipeConflict", error);
        Assert.Same(source, preview.Result);
        // Moving the entire object keeps its scope and identity and remains supported.
        Assert.True(TryPreview(source, "primary", BasicLayoutPreset.KaraokeDouble, null, out preview, out error), error);
        Assert.Same(source.PresetApplications, preview.Result.PresetApplications);
    }

    [Fact]
    public void 默认连续成员只为所选行建立片段并保留前奏()
    {
        var source = LeadInFixture.Create();
        var tree = source.LyricPresentation;
        var program = tree.FindProgram("primary")!;
        Assert.True(TryPreview(source, "primary", BasicLayoutPreset.KaraokeDouble, ["chorus"], out var preview, out var error), error);
        var updated = preview.Result.LyricPresentation.FindProgram("primary")!;
        Assert.Equal(program.DefaultContainerId, updated.DefaultContainerId);
        Assert.Equal(program.LeadIns, updated.LeadIns);
        Assert.Equal("chorus", Assert.Single(updated.Clips.Single(clip => clip.ScheduleContext is null).LineIds));
        Assert.All(updated.Clips.Where(clip => clip.ScheduleContext is not null), clip =>
            Assert.Equal(source.Lyrics.Lines.Select(line => line.Id), clip.ScheduleContext!.LineIds));
        Assert.All(tree.Nodes, node => Assert.Equal(node, preview.Result.LyricPresentation.FindNode(node.Id)));
        var frame = LyricSceneSampler.SampleFrame(ProjectSnapshot.FromProject(preview.Result), 38_500_000);
        Assert.Contains(frame.Lines, sample => sample.Frame.Line?.Id == "chorus" && sample.CueId == "cue-chorus");
        Assert.False(TryPreview(source, "primary", (BasicLayoutPreset)100, null, out var rejected, out error));
        Assert.Same(source, rejected.Result);
    }

    [Fact]
    public void 调度上下文保留双槽原身份和显示时钟包括选区内提前显示的未选行()
    {
        var source = AutoClipSource();
        Assert.True(TryPreview(source, "primary", BasicLayoutPreset.KaraokeDouble, null, out var baseline, out var error), error);
        source = baseline.Result;
        Assert.True(TryPreview(source, "primary", BasicLayoutPreset.SingleCentered, ["line-2"], out var preview, out error), error);
        var sourceProgram = source.LyricPresentation.FindProgram("primary")!;
        var resultProgram = preview.Result.LyricPresentation.FindProgram("primary")!;
        var before = LyricDisplayScheduleBuilder.Build(source.Lyrics.PrimaryBranch!, sourceProgram, source.LyricPresentation);
        var after = LyricDisplayScheduleBuilder.Build(source.Lyrics.PrimaryBranch!, resultProgram, preview.Result.LyricPresentation);
        Assert.Equal(sourceProgram.Clips.Single().LineIds, resultProgram.Clips.First().ScheduleContext!.LineIds);
        using var renderer = new SkiaRenderPipeline();
        foreach (long time in new long[] { 0, 500_000, 1_000_000, 3_500_000, 4_500_000, 5_100_000, 5_900_000, 6_000_000, 8_500_000, 9_999_999 })
        {
            var oldFrame = renderer.GetSceneFrame(ProjectSnapshot.FromProject(source), time);
            var newFrame = renderer.GetSceneFrame(ProjectSnapshot.FromProject(preview.Result), time);
            foreach (var sample in oldFrame.Lines.Where(item => item.Branch.Id == "primary" && item.Frame.Line!.Id != "line-2"))
            {
                var current = newFrame.Lines.Single(item => item.Frame.Line!.Id == sample.Frame.Line!.Id);
                Assert.Equal(sample.ParentNodeId, current.ParentNodeId);
                Assert.Equal(sample.Placement, current.Placement);
                Assert.Equal(sample.Transform, current.Transform);
                Assert.Equal(sample.Geometry!.Corners(), current.Geometry!.Corners());
            }
        }
        foreach (var occurrence in before.Where(item => item.Line.Id != "line-2"))
        {
            var current = after.Single(item => item.Line.Id == occurrence.Line.Id);
            Assert.Equal(occurrence.SlotIndex, current.SlotIndex);
            Assert.Equal(occurrence.RevealTimeUs, current.RevealTimeUs);
            Assert.Equal(occurrence.HideTimeUs, current.HideTimeUs);
        }
        using TemporaryDirectory directory = new();
        ProjectStore.Save(preview.Result, directory.Path);
        var restored = ProjectStore.Load(directory.Path);
        Assert.NotNull(restored.Project);
        Assert.Equal(preview.Result.LyricPresentation, restored.Project.LyricPresentation);
        Assert.Equal(after.Select(item => (item.Line.Id, item.SlotIndex, item.RevealTimeUs, item.HideTimeUs)),
            LyricDisplayScheduleBuilder.Build(restored.Project.Lyrics.PrimaryBranch!, restored.Project.LyricPresentation.FindProgram("primary")!, restored.Project.LyricPresentation)
                .Select(item => (item.Line.Id, item.SlotIndex, item.RevealTimeUs, item.HideTimeUs)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 默认槽位段拆分保留相邻片段边界和整轮上下文(bool bounded)
    {
        var source = ExclusiveDisplayFixture.Create();
        var tree = source.LyricPresentation;
        var originalProgram = tree.FindProgram("primary")!;
        if (!bounded) tree = tree with { Programs = tree.Programs.SetItem(0, originalProgram with { Clips = [] }) };
        source = source with { LyricPresentation = tree };
        var program = tree.FindProgram("primary")!;
        Assert.True(TryPreview(source, "primary", BasicLayoutPreset.SingleCentered, ["line-1"], out var preview, out var error), error);
        var context = preview.Result.LyricPresentation.FindProgram("primary")!.Clips.First(clip => clip.ScheduleContext is not null).ScheduleContext!;
        Assert.True(context.IsDefaultSegment);
        Assert.Null(context.PreviousLineId);
        Assert.Equal(bounded ? "line-2" : null, context.NextLineId);
        var oldSchedule = LyricDisplayScheduleBuilder.Build(source.Lyrics.PrimaryBranch!, program, tree);
        var newSchedule = LyricDisplayScheduleBuilder.Build(source.Lyrics.PrimaryBranch!, preview.Result.LyricPresentation.FindProgram("primary")!, preview.Result.LyricPresentation);
        Assert.Equal(oldSchedule.Where(item => item.Line.Id != "line-1").Select(item => (item.Line.Id, item.SlotIndex, item.RevealTimeUs, item.HideTimeUs)),
            newSchedule.Where(item => item.Line.Id != "line-1").Select(item => (item.Line.Id, item.SlotIndex, item.RevealTimeUs, item.HideTimeUs)));
        using var renderer = new SkiaRenderPipeline();
        foreach (long time in new long[] { 0, 1_000_000, 2_500_000, 3_800_000, 4_000_000, 6_500_000, 9_500_000 })
        {
            var before = renderer.GetSceneFrame(ProjectSnapshot.FromProject(source), time).Lines.Where(item => item.Branch.Id == "primary" && item.Frame.Line!.Id != "line-1").ToArray();
            var after = renderer.GetSceneFrame(ProjectSnapshot.FromProject(preview.Result), time).Lines.Where(item => item.Branch.Id == "primary" && item.Frame.Line!.Id != "line-1").ToArray();
            Assert.Equal(before.Select(item => item.Frame.Line!.Id), after.Select(item => item.Frame.Line!.Id));
            Assert.Equal(before.Select(item => item.Geometry!.Corners()), after.Select(item => item.Geometry!.Corners()));
        }
        using TemporaryDirectory directory = new();
        ProjectStore.Save(preview.Result, directory.Path);
        Assert.Equal(preview.Result, ProjectStore.Load(directory.Path).Project);
    }

    [Theory]
    [InlineData("line-0,line-2")]
    [InlineData("line-1,line-0")]
    [InlineData("line-0,line-0")]
    [InlineData("missing")]
    [InlineData("")]
    public void 无效范围无变化(string selection)
    {
        var source = AutoClipSource();
        var selected = selection.Split(',', StringSplitOptions.RemoveEmptyEntries);
        Assert.False(TryPreview(source, "primary", BasicLayoutPreset.KaraokeDouble, selected, out var preview, out var error));
        Assert.NotEmpty(error);
        Assert.Same(source, preview.Result);
    }

    [Fact]
    public void 翻译来源范围布局稳定拒绝且不产生展示树校验内部错误()
    {
        var source = ExclusiveDisplayFixture.Create();
        var names = new BasicLayoutNames("Test layout", ["Slot 1"], "Selected", "Retained");
        Assert.False(BasicLayoutOperation.TryPreview(source, "translation", BasicLayoutPreset.SingleCentered,
            ["translation-line-1"], names, out var preview, out var error));
        Assert.Equal(BasicLayoutErrorCode.UnsupportedSource, error!.Code);
        Assert.Same(source, preview.Result);
    }

    [Fact]
    public void 翻译来源整来源布局以明确领域错误拒绝()
    {
        var source = ExclusiveDisplayFixture.Create();
        var names = new BasicLayoutNames("Translation layout", ["Slot 1"], "Selected", "Retained");
        Assert.False(BasicLayoutOperation.TryPreview(source, "translation", BasicLayoutPreset.SingleCentered,
            null, names, out var preview, out var error));
        Assert.Equal(BasicLayoutErrorCode.UnsupportedSource, error!.Code);
        Assert.Contains("FollowPrimary", error.Detail);
        Assert.Null(error.Validation);
        Assert.Same(source, preview.Result);
    }

    [Theory]
    [InlineData("missing", BasicLayoutErrorCode.SourceMissing)]
    [InlineData("invalid", BasicLayoutErrorCode.InvalidPreset)]
    [InlineData("range", BasicLayoutErrorCode.InvalidRange)]
    [InlineData("discontiguous", BasicLayoutErrorCode.DiscontiguousRange)]
    public void 结构化失败不包含界面标签且保留原工程(string mode, BasicLayoutErrorCode code)
    {
        var source = AutoClipSource();
        var names = new BasicLayoutNames("Provided layout", ["Provided slot 1", "Provided slot 2", "Provided slot 3"], "Selected", "Retained");
        var selected = mode == "range" ? new[] { "missing" } : mode == "discontiguous" ? new[] { "line-0", "line-2" } : null;
        Assert.False(BasicLayoutOperation.TryPreview(source, mode == "missing" ? "missing" : "primary",
            mode == "invalid" ? (BasicLayoutPreset)100 : BasicLayoutPreset.KaraokeDouble, selected, names, out var preview, out var error));
        Assert.Equal(code, error!.Code); Assert.Null(error.Validation); Assert.Null(error.Detail); Assert.Same(source, preview.Result);
    }

    private static bool TryPreview(Project source, string branchId, BasicLayoutPreset preset, IReadOnlyList<string>? selection,
        out BasicLayoutPreview preview, out string error)
    {
        var names = new BasicLayoutNames("Test layout", ["Slot 1", "Slot 2", "Slot 3"], "Selected", "Retained");
        bool succeeded = BasicLayoutOperation.TryPreview(source, branchId, preset, selection, names, out preview, out var failure);
        error = failure?.ToString() ?? string.Empty;
        return succeeded;
    }

    private static Project AutoClipSource()
    {
        var source = ExclusiveDisplayFixture.Create();
        var tree = source.LyricPresentation;
        var program = tree.FindProgram("primary")!;
        var clip = program.Clips.Single() with { LineIds = [.. source.Lyrics.Lines.Select(line => line.Id)] };
        tree = tree with { Programs = tree.Programs.SetItem(tree.Programs.IndexOf(program), program with { Clips = [clip] }) };
        Assert.True(PresentationEditor.TrySetTree(source, tree, out source, out var report), report.ToSummary());
        return source;
    }
}
