using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class LyricSceneIntegrationTests
{
    [Fact]
    public void 双行在上一行时间阈值提前显示下一句并纯时间重建()
    {
        var snapshot = Example();
        var before = LyricSceneSampler.Sample(snapshot, 999_999);
        Assert.Single(before.Where(entry => entry.Branch.Role == LyricBranchRole.Primary));
        var after = LyricSceneSampler.Sample(snapshot, 1_500_000);
        var primary = after.Where(entry => entry.Branch.Role == LyricBranchRole.Primary).ToArray();
        Assert.Equal(2, primary.Length);
        Assert.Equal("upper-left", primary[0].SlotId); Assert.Equal("lower-right", primary[1].SlotId);
        Assert.Equal(0, primary[1].Frame.LineProgress); Assert.Equal(0, primary[1].Frame.Tokens[0].Progress);
        _ = LyricSceneSampler.Sample(snapshot, 7_000_000);
        var seeked = LyricSceneSampler.Sample(snapshot, 1_500_000);
        Assert.Equal(after.Select(entry => (entry.Branch.Id, entry.Frame.Line!.Id, entry.SlotId)),
            seeked.Select(entry => (entry.Branch.Id, entry.Frame.Line!.Id, entry.SlotId)));
        Assert.Empty(LyricSceneSampler.Sample(snapshot, 7_000_000));
    }

    [Fact]
    public void 关联翻译跟随提前可见区间而始终静态并且失效关联不按索引补齐()
    {
        var snapshot = Example();
        var frames = LyricSceneSampler.Sample(snapshot, 1_500_000);
        var translation = Assert.Single(frames.Where(entry => entry.Branch.Role == LyricBranchRole.Translation));
        Assert.Equal("translation-two", translation.Frame.Line!.Id);
        Assert.Null(translation.SlotId);
        Assert.Equal(0, translation.Frame.LineProgress); Assert.All(translation.Frame.Tokens, token => Assert.Equal(0, token.Progress));
        var primary = snapshot.Lyrics.PrimaryBranch!;
        snapshot = snapshot with { Lyrics = snapshot.Lyrics.ReplaceBranch(primary with { Lines = [primary.Lines[2], primary.Lines[0]] }) };
        Assert.DoesNotContain(LyricSceneSampler.Sample(snapshot, 1_500_000), entry => entry.Branch.Role == LyricBranchRole.Translation);
    }

    [Fact]
    public void 无关联翻译使用自身半开行时间()
    {
        var snapshot = Example();
        var branch = snapshot.Lyrics.Branches[1] with { Lines = [LyricLine.Create("independent", "译文", new TimeRange(0, 1_000_000), TimingStatus.Edited)] };
        snapshot = snapshot with { Lyrics = snapshot.Lyrics.ReplaceBranch(branch) };
        Assert.Contains(LyricSceneSampler.Sample(snapshot, 0), entry => entry.Frame.Line!.Id == "independent");
        Assert.DoesNotContain(LyricSceneSampler.Sample(snapshot, 1_000_000), entry => entry.Frame.Line!.Id == "independent");
    }

    [Theory]
    [InlineData(SlotConflictPolicy.QueueUntilSlotFree)]
    [InlineData(SlotConflictPolicy.AllowOverflow)]
    [InlineData(SlotConflictPolicy.ReplacePrevious)]
    [InlineData(SlotConflictPolicy.Skip)]
    public void 槽位冲突遵从策略且跳过行不会稍后自动复活(SlotConflictPolicy policy)
    {
        var lines = new[] { Timed("a", 0, 4_000_000), Timed("b", 1_000_000, 6_000_000), Timed("c", 2_000_000, 7_000_000) };
        var profile = SlotSequenceAnimationProfile.Default with { RevealProgress = .1, ConflictPolicy = policy };
        var at = SlotSequenceAnimationScheduler.Sample(lines, 2_500_000, profile);
        switch (policy)
        {
            case SlotConflictPolicy.AllowOverflow: Assert.Equal(3, at.Length); break;
            case SlotConflictPolicy.ReplacePrevious: Assert.DoesNotContain(at, entry => entry.LineId == "a"); break;
            default: Assert.DoesNotContain(at, entry => entry.LineId == "c"); break;
        }
        if (policy == SlotConflictPolicy.Skip)
            Assert.DoesNotContain(SlotSequenceAnimationScheduler.Sample(lines, 4_500_000, profile), entry => entry.LineId == "c");
        if (policy == SlotConflictPolicy.QueueUntilSlotFree)
            Assert.Contains(SlotSequenceAnimationScheduler.Sample(lines, 4_500_000, profile), entry => entry.LineId == "c");
    }

    [Fact]
    public void 未定时与零长行不占槽位且短行采样有限()
    {
        var branch = Example().Lyrics.PrimaryBranch!;
        branch = branch with { Lines = [LyricLine.Create("missing", "无时间"), Timed("short", 0, 10), Timed("next", 10, 20)] };
        var snapshot = Example() with { Lyrics = new LyricsDocument { Branches = [branch] } };
        var first = LyricSceneSampler.Sample(snapshot, 1);
        Assert.Equal("upper-left", first[0].SlotId);
        Assert.DoesNotContain(first, entry => entry.Frame.Line!.Id == "missing");
        Assert.Contains(LyricSceneSampler.Sample(snapshot, 5), entry => entry.Frame.Line!.Id == "short");
    }

    [Fact]
    public void 多分支真实像素可见且翻译在时间内不逐字换色()
    {
        using SkiaRenderPipeline pipeline = new();
        var snapshot = Example();
        using var full = pipeline.Render(RenderRequest.At(snapshot, 1_500_000) with { TransparentBackground = true });
        using var primaryOnly = pipeline.Render(RenderRequest.At(snapshot with { Lyrics = snapshot.Lyrics with { Branches = [snapshot.Lyrics.PrimaryBranch!] } }, 1_500_000)
            with { TransparentBackground = true });
        Assert.True(Difference(full, primaryOnly, 240, 359) > 100);
        using var later = pipeline.Render(RenderRequest.At(snapshot, 2_200_000) with { TransparentBackground = true });
        Assert.Equal(0, Difference(full, later, 280, 359));
        Assert.True(HasAlpha(full, 50, 150)); Assert.True(HasAlpha(full, 150, 270));
    }

    [Fact]
    public void 卡拉OK逐字进度改变像素而缩小预览保持相对几何()
    {
        using SkiaRenderPipeline pipeline = new();
        var snapshot = Example();
        using var early = pipeline.Render(RenderRequest.At(snapshot, 100_000) with { TransparentBackground = true });
        using var middle = pipeline.Render(RenderRequest.At(snapshot, 500_000) with { TransparentBackground = true });
        using var late = pipeline.Render(RenderRequest.At(snapshot, 1_900_000) with { TransparentBackground = true });
        Assert.True(Difference(early, middle, 0, 150) > 100);
        Assert.True(Difference(late, middle, 0, 150) > 100);
        using var small = pipeline.Render(RenderRequest.At(snapshot, 500_000) with { TransparentBackground = true,
            Target = new() { PixelWidth = 320, PixelHeight = 180 } });
        Assert.True(HasAlpha(small, 20, 85));
    }

    [Fact]
    public void 注音扩大占用几何并绘出像素且不重分配基础字进度()
    {
        using SkiaRenderPipeline pipeline = new();
        var snapshot = Example();
        var primary = snapshot.Lyrics.PrimaryBranch!;
        var line = primary.Lines[0] with { RubyAnnotations = [RubyAnnotation.Create("reading", "a-token", "kanji", relativeScale: .6, gapPx: 4)] };
        primary = primary with { Lines = [line] };
        snapshot = snapshot with { Lyrics = snapshot.Lyrics with { Branches = [primary] } };
        snapshot = Configure(snapshot, primary.Id, definition => definition with { SlotSequenceProfile = null,
            DefaultPlacement = ScenePlacement.Create(new(.5, .4)) });
        var plain = snapshot with { Lyrics = snapshot.Lyrics.ReplaceBranch(primary with { Lines = [line with { RubyAnnotations = [] }] }) };
        var annotatedGeometry = pipeline.GetLineGeometry(snapshot, line.Id)!;
        var plainGeometry = pipeline.GetLineGeometry(plain, line.Id)!;
        Assert.True(annotatedGeometry.BottomRight.Y - annotatedGeometry.TopLeft.Y > plainGeometry.BottomRight.Y - plainGeometry.TopLeft.Y + 10);
        using var ruby = pipeline.Render(RenderRequest.At(snapshot, 500_000) with { TransparentBackground = true });
        using var without = pipeline.Render(RenderRequest.At(plain, 500_000) with { TransparentBackground = true });
        Assert.True(Difference(ruby, without, 0, 359) > 100);
        double reserved = (annotatedGeometry.BottomRight.Y - annotatedGeometry.TopLeft.Y)
            - (plainGeometry.BottomRight.Y - plainGeometry.TopLeft.Y);
        Assert.True(HasAlpha(ruby, (int)Math.Floor(annotatedGeometry.TopLeft.Y),
            (int)Math.Floor(annotatedGeometry.TopLeft.Y + reserved - 6)), "注音预留区域必须真的绘出文字");
        Assert.Equal(LyricSceneSampler.Sample(plain, 500_000)[0].Frame.Tokens.ToArray(),
            LyricSceneSampler.Sample(snapshot, 500_000)[0].Frame.Tokens.ToArray());
        Assert.True(annotatedGeometry.BottomRight.Y < 200); // 下方翻译安全区从 240 开始，不重叠。
    }

    [Fact]
    public void 同时可见的关联翻译在安全区内分行而不互相覆盖()
    {
        using SkiaRenderPipeline pipeline = new();
        var snapshot = Example();
        var translation = snapshot.Lyrics.Branches[1] with
        {
            Lines = [LyricLine.Create("translation-one", "FIRST") with { PrimaryLineId = "a" },
                LyricLine.Create("translation-two", "SECOND") with { PrimaryLineId = "b" }],
        };
        snapshot = snapshot with { Lyrics = snapshot.Lyrics.ReplaceBranch(translation) };
        snapshot = Configure(snapshot, translation.Id, definition => definition with { SafeArea = NormalizedSafeArea.Translation });
        using var frame = pipeline.Render(RenderRequest.At(snapshot, 1_500_000) with { TransparentBackground = true });
        int bands = 0; bool previous = false;
        for (int y = 274; y < 346; y++)
        {
            bool painted = HasAlpha(frame, y, y);
            if (painted && !previous) bands++;
            previous = painted;
        }
        Assert.Equal(2, bands);
        Assert.False(HasAlpha(frame, 346, 359));
    }

    [Fact]
    public void 整体跟随分支跟随主句可见但不继承逐字换色()
    {
        var snapshot = Example();
        var primary = snapshot.Lyrics.PrimaryBranch!;
        var line = primary.Lines[0];
        var translated = snapshot.Lyrics.Branches[1] with
        {
            Lines = [LyricLine.Create("translation-one", "Follow") with { PrimaryLineId = "a" }],
        };
        snapshot = snapshot with { Lyrics = snapshot.Lyrics with { Branches = [primary with { Lines = primary.Lines.SetItem(0, line) }, translated] } };
        snapshot = Configure(snapshot, translated.Id, definition => definition with { AnimationPolicy = LyricBranchAnimationPolicy.FollowPrimary });
        var scene = LyricSceneSampler.Sample(snapshot, 0);
        var following = scene.First(entry => entry.Branch.Role == LyricBranchRole.Translation);
        Assert.Equal("translation-one", following.Frame.Line!.Id);
        Assert.Equal(0, following.Frame.LineProgress); Assert.Empty(following.Frame.Tokens);
    }

    [Fact]
    public void 静态布局编辑与实际双行槽位使用相同位置且辅助线不影响时间渲染()
    {
        using SkiaRenderPipeline pipeline = new();
        var snapshot = Example();
        LyricBranch primary = snapshot.Lyrics.PrimaryBranch!;
        snapshot = Configure(snapshot, primary.Id, definition => definition with { Lines = [new()
            { LineId = "b", LocalTransform = new() { OffsetXRatio = .1, OffsetYRatio = .1 } }] });
        var geometry = pipeline.GetLineGeometry(snapshot, "b")!;
        Assert.Equal(640 * .82, geometry.Pivot.X, 8); Assert.Equal(360 * .8, geometry.Pivot.Y, 8);
        using var specimen = pipeline.Render(RenderRequest.At(snapshot, 0) with { LayoutLineId = "b", TransparentBackground = true });
        Assert.True(HasAlpha(specimen, 200, 335));
        Assert.True(HasAlpha(specimen, 0, 140)); // 编辑整体时两槽位共同展示。
        Assert.Equal(new(.72, .7), pipeline.GetNodeGeometry(snapshot,snapshot.LyricPresentation.FindNode(snapshot.LyricPresentation.FindProgram("primary")!.DefaultContainerId)!.Layout!.SlotIds[1],"b")!.Placement.Position);

        snapshot = Configure(snapshot, primary.Id, definition => definition with { SlotSequenceProfile = null });
        Assert.Equal(new CanvasPoint(640 * .6, 360 * .6), pipeline.GetLineGeometry(snapshot, "b")!.Pivot);
    }

    [Fact]
    public void 双行整体变换作用于槽位外层并保持相对关系()
    {
        using SkiaRenderPipeline pipeline = new();
        var baseline = Example();
        var moved = Configure(baseline, baseline.Lyrics.PrimaryBranchId, definition => definition with
            { Placement = ScenePlacement.Create(new(.6, .45), scale: .8) });
        var scenes = LyricSceneSampler.Sample(moved, 1_500_000)
            .Where(entry => entry.Branch.Role == LyricBranchRole.Primary).ToArray();
        Assert.Equal(2, scenes.Length);
        Assert.Equal(new(.28, .3), moved.LyricPresentation.FindNode(scenes[0].ParentId)!.Placement.Position);
        Assert.Equal(new(.72, .7), moved.LyricPresentation.FindNode(scenes[1].ParentId)!.Placement.Position);
        var container = SceneMatrix.Container(scenes[0].Presentation, moved.Composition);
        var firstPivot = container.Map(new(640 * .28, 360 * .3));
        var secondPivot = container.Map(new(640 * .72, 360 * .7));
        Assert.Equal(640 * .352, secondPivot.X - firstPivot.X, 6);
        Assert.Equal(360 * .32, secondPivot.Y - firstPivot.Y, 6);
        CanvasPoint pivot = pipeline.GetLineGeometry(moved, "b")!.Pivot;
        Assert.Equal(640 * .776, pivot.X, 8);
        Assert.Equal(360 * .61, pivot.Y, 8);

        using var before = pipeline.Render(RenderRequest.At(baseline, 1_500_000) with { TransparentBackground = true });
        using var after = pipeline.Render(RenderRequest.At(moved, 1_500_000) with { TransparentBackground = true });
        Assert.True(Difference(before, after, 0, 359) > 100);
    }

    private static ProjectSnapshot Example()
    {
        var lyrics = LyricsDocument.Create([Timed("a", 0, 2_000_000), Timed("b", 2_000_000, 4_000_000), Timed("c", 4_000_000, 7_000_000)]);
        var primary = lyrics.PrimaryBranch!;
        var translation = LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
            [LyricLine.Create("translation-two", "Static translation") with { PrimaryLineId = "b" }], "en", LyricBranchTimingMode.FollowPrimary) with
        { StyleOverride = new() { FontSizePx = 22 } };
        return ProjectSnapshot.FromProject(Project.Create("scene", composition: new() { CanvasWidth = 640, CanvasHeight = 360 },
            lyricStyle: LyricStyle.Default with { FontSizePx = 32, UnsungColor = RgbaColor.White, SungColor = new(255, 0, 0) },
            lyrics: lyrics with { Branches = [primary, translation] }) with
            { LyricPresentation = LyricPresentationDefinition.FromBranches([new() { BranchId = primary.Id, SlotSequenceProfile = SlotSequenceAnimationProfile.Default },
                new() { BranchId = translation.Id, DefaultPlacement = ScenePlacement.Create(new(.5, .86)) }]) });
    }
    private static ProjectSnapshot Configure(ProjectSnapshot snapshot, string branchId, Func<LyricBranchPresentation, LyricBranchPresentation> configure) =>
        snapshot with { LyricPresentation = snapshot.LyricPresentation.SetBranch(configure(snapshot.LyricPresentation.Resolve(branchId))) };
    private static LyricLine Timed(string id, long start, long end) => LyricLine.Create(id, "汉字",
        new TimeRange(start, end), TimingStatus.Edited,
        [LyricToken.Create(id + "-token", "汉字", new TimeRange(start, end), TimingStatus.Edited)]);
    private static int Difference(RenderFrame first, RenderFrame second, int top, int bottom)
    {
        int count = 0;
        for (int y = top; y <= bottom; y++) for (int x = 0; x < first.Width; x++)
        {
            int offset = y * first.Stride + x * 4;
            if (!first.PixelMemory.Span.Slice(offset, 4).SequenceEqual(second.PixelMemory.Span.Slice(offset, 4))) count++;
        }
        return count;
    }
    private static bool HasAlpha(RenderFrame frame, int top, int bottom)
    {
        for (int y = top; y <= bottom; y++) for (int x = 0; x < frame.Width; x++)
            if (frame.PixelMemory.Span[y * frame.Stride + x * 4 + 3] > 30) return true;
        return false;
    }
}
