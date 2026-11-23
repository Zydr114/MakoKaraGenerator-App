using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

// Direct renderer protocol fixtures, separate from actual package/host admission.
public sealed partial class MainCanvasImageEffectsTests
{
    [Fact]
    public void 父组配方不能部分读取已融合的孩子颜色且异常保留父owner与表面回收()
    {
        var request = Request(Snapshot()); using var child = Tint("child"); using var parent = Tint("parent");
        using var resources = new SceneImageEffectResources([Program("first", child, Whole), Program("last", parent, Fill with { IncludeDescendantGroups = true })]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var childBinding = Bind("child", Line(request)); var parentBinding = Bind("parent", GroupRoot(request), "last");
        var error = Assert.Throws<EffectFailureException>(() => renderer.Render(Bindings(request, childBinding, parentBinding)));
        Assert.Equal("ImageEffectPartialFusion", error.Code); Assert.Equal("parent", error.InstanceId.Value); Assert.Equal("artifact-parent", error.ArtifactHash);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }
    [Fact]
    public void GroupWhole可接孩子融合产物且保留未选背景()
    {
        var request = Request(Snapshot()); using var child = Tint("child"); using var identity = GroupIdentity();
        using var resources = new SceneImageEffectResources([Program("first", child, Whole), Program("last", identity, GroupWhole)]);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        var childBinding = Bind("child", Line(request)); using var expected = renderer.Render(Bindings(request, childBinding));
        using var actual = renderer.Render(Bindings(request, childBinding, Bind("parent", GroupRoot(request), "last")));
        Close(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray(), 2);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }
    private static Project GroupInterleavingProject()
    {
        LyricLine Make(string id, long start) => LyricLine.Create(id, "A", new(start, start + 1_000_000), TimingStatus.Edited,
            [LyricToken.Create(id + "-token", "A", new(start, start + 1_000_000), TimingStatus.Edited)]);
        var project = Project.Create("G4 interleaving", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 4_000_000),
            LyricsDocument.Create([Make("a", 0), Make("b", 1_000_000), Make("c", 2_000_000)]), lyricStyle: LyricStyle.Default with
            { FontFamilyName = "DejaVu Sans", FontSizePx = 64, SungColor = RgbaColor.White, UnsungColor = RgbaColor.White, Stroke = null, Shadow = null, Glow = null });
        var slots = SlotSequenceAnimationProfile.Default with { MaxVisibleLines = 3,
            Slots = [SlotSequenceSlot.Create("s1", ScenePlacement.Create(new(.5, .5))), SlotSequenceSlot.Create("s2", ScenePlacement.Create(new(.5, .5))),
                SlotSequenceSlot.Create("s3", ScenePlacement.Create(new(.5, .5)))] };
        project = PresentationEditor.SetBranch(project, new() { BranchId = "primary", SlotSequenceProfile = slots });
        var lyrics = project.Lyrics; var branch = LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
            [.. lyrics.Lines.Select(line => LyricLine.Create("t-" + line.Id, "Translation") with { PrimaryLineId = line.Id })], "en", LyricBranchTimingMode.FollowPrimary);
        project = project with { Lyrics = lyrics with { Branches = lyrics.Branches.Add(branch) } };
        return PresentationEditor.SetBranch(project, new() { BranchId = "translation", DefaultPlacement = ScenePlacement.Create(new(.5, .9)) });
    }
    [Fact]
    public void 组图交错后代按首项合成且缓存保留具名诊断与真实owner()
    {
        var request = Request(ProjectSnapshot.FromProject(GroupInterleavingProject()), time: 1_500_000); var frame = request.PreparedFrame!;
        var primary = frame.Scene.Lines.Where(item => item.Branch.Id == "primary").ToArray();
        var translation = frame.Scene.Lines.Where(item => item.Branch.Id == "translation").ToArray();
        Assert.True(primary.Length >= 2); Assert.True(translation.Length >= 2);
        var reordered = frame.Scene with { Lines = [primary[0], translation[0], .. primary.Skip(1), .. translation.Skip(1)] };
        var contiguous = frame.Scene with { Lines = [.. primary, .. translation] };
        request = request with { PreparedFrame = new(request.Project, request.TimeUs, frame.OutputMode, frame.Target, reordered, []) };
        var expectedRequest = request with { PreparedFrame = new(request.Project, request.TimeUs, frame.OutputMode, frame.Target, contiguous, []) };
        using var graph = Tint("tint"); using var resources = new SceneImageEffectResources([Program("first", graph, GroupWhole)]);
        using var budget = new ImageSurfaceBudgetSession(); using var native = new SkiaRenderPipeline(null, null, budget, resources);
        var binding = Bind("interleaved-parent", GroupRoot(request)); var cached = new CachedRenderPipeline(native);
        using var expected = native.Render(Bindings(expectedRequest, binding));
        using var fresh = cached.Render(Bindings(request, binding)); using var hit = cached.Render(Bindings(request, binding));
        var diagnostic = Assert.Single(fresh.SceneDiagnostics.Where(item => item.Diagnostic.Code == "GroupIsolationChangesOrder"));
        Assert.Equal("interleaved-parent", diagnostic.InstanceId.Value); Assert.Equal("artifact-interleaved-parent", diagnostic.ArtifactHash);
        Assert.EndsWith("合成。", diagnostic.Diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(fresh.SceneDiagnostics.ToArray(), hit.SceneDiagnostics.ToArray()); Close(fresh.PixelMemory.ToArray(), hit.PixelMemory.ToArray(), 0);
        Close(expected.PixelMemory.ToArray(), fresh.PixelMemory.ToArray(), 0);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }
}
