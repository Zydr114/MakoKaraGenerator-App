using System.Collections.Immutable;
using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class GroupOpacityIsolationTests
{
    private const string Fingerprint = "group-opacity";
    private static readonly Lazy<Task<CompiledEffectPackage>> Package = new(async () =>
    {
        var package = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(EffectPackageFixture.GroupOpacity()),
            new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));
        Assert.True(package.Artifact.Succeeded, string.Join('\n', package.Artifact.Diagnostics)); return package;
    });
    private static string Root(ProjectSnapshot snapshot) => snapshot.LyricPresentation.FindProgram("primary")!.RootContainerId;
    private static RenderRequest Request(ProjectSnapshot snapshot, long time = 10_000_000, bool transparent = true, int width = 640, int height = 360)
    {
        using var measure = new SkiaRenderPipeline(); var scene = measure.GetSceneFrame(snapshot, time);
        var target = new RenderTarget { PixelWidth = width, PixelHeight = height };
        return RenderRequest.At(snapshot, time) with { TransparentBackground = transparent, Target = target,
            PreparedFrame = new(snapshot, time, transparent ? Fx.OutputMode.Transparent : Fx.OutputMode.Opaque, target, scene, []) };
    }
    private static PreparedGroupOpacity Opacity(string node, double factor, string id = "group") => new(new(id), "artifact-" + id, new(new(new(node)), factor));
    private static RenderRequest Apply(RenderRequest request, params PreparedGroupOpacity[] outputs) => request with { PreparedFrame = request.PreparedFrame!.WithGroupOpacities([.. outputs]) };
    private static void Close(byte[] expected, byte[] actual, int tolerance = 2)
    { Assert.Equal(expected.Length, actual.Length); for (int i = 0; i < actual.Length; i++) Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"字节{i}: {expected[i]} != {actual[i]}"); }
    private static byte[] CapturedReference(RenderRequest request, double factor)
    {
        request = request with { PreparedFrame = request.PreparedFrame!.WithGroupOpacities([]) };
        using var native = new SkiaRenderPipeline(); var c = request.Project.Composition; var target = request.ResolveTarget();
        var bounds = new GraphRect(0, 0, c.CanvasWidth, c.CanvasHeight); var budget = GraphExecutionBudget.Default;
        var recipe = new SceneCaptureRecipe(true, true, true, true, SceneCaptureRegion.FillAndStroke);
        using var primary = native.CaptureSceneTarget(request, new SceneCaptureTarget.Group(new(new(Root(request.Project)))), recipe, GraphInputSource.TargetColor, bounds, budget);
        using var srgb = SKColorSpace.CreateSrgb(); using var surface = SKSurface.Create(new SKImageInfo(target.PixelWidth, target.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        surface.Canvas.ClipRect(new(0, 0, target.PixelWidth, target.PixelHeight)); surface.Canvas.Clear(request.TransparentBackground ? SKColors.Transparent : new SKColor(c.BackgroundColor.Red, c.BackgroundColor.Green, c.BackgroundColor.Blue, c.BackgroundColor.Alpha));
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(factor * 255)) };
        surface.Canvas.DrawImage(primary.Image, 0, 0, paint);
        foreach (var program in request.Project.LyricPresentation.Programs.Where(program => program.BranchId != "primary"))
        {
            using var external = native.CaptureSceneTarget(request, new SceneCaptureTarget.Group(new(new(program.RootContainerId))), recipe, GraphInputSource.TargetColor, bounds, budget);
            surface.Canvas.DrawImage(external.Image, 0, 0);
        }
        using var image = surface.Snapshot(); using var bitmap = SKBitmap.FromImage(image); return bitmap.Bytes;
    }
    [Theory]
    [InlineData(true, 640, 360)] [InlineData(true, 480, 180)] [InlineData(false, 320, 180)]
    public void 父组统一透明度保留注音前奏及独立翻译(bool transparent, int width, int height)
    {
        var project = LeadInFixture.Create(true); project = project with { Composition = project.Composition with { BackgroundColor = new(25, 40, 60) } };
        var request = Request(ProjectSnapshot.FromProject(project), transparent: transparent, width: width, height: height);
        Assert.NotEmpty(request.PreparedFrame!.Scene.TextMarks); Assert.Contains(request.PreparedFrame.Scene.Lines, item => item.Branch.Id == "translation");
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(surfaceBudget: budget);
        using var actual = renderer.Render(Apply(request, Opacity(Root(request.Project), .5)));
        Close(CapturedReference(request, .5), actual.Pixels.ToArray());
        Assert.Empty(actual.SceneDiagnostics); Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }
    private static Project OverlapProject(bool translation = false)
    {
        LyricLine Line(string id, long start) => LyricLine.Create(id, "歌", new(start, start + 1_000_000), TimingStatus.Edited,
            [LyricToken.Create(id + "-token", "歌", new(start, start + 1_000_000), TimingStatus.Edited)]);
        var project = Project.Create("重叠", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 4_000_000),
            LyricsDocument.Create([Line("a", 0), Line("b", 1_000_000), Line("c", 2_000_000)]), lyricStyle: LyricStyle.Default with
            { FontSizePx = 64, SungColor = RgbaColor.White, UnsungColor = RgbaColor.White, Stroke = null, Shadow = null });
        var slots = SlotSequenceAnimationProfile.Default with { MaxVisibleLines = 3, EnterDurationUs = 0, ExitDurationUs = 0,
            Slots = [SlotSequenceSlot.Create("s1", ScenePlacement.Create(new(.5, .5))), SlotSequenceSlot.Create("s2", ScenePlacement.Create(new(.5, .5))),
                SlotSequenceSlot.Create("s3", ScenePlacement.Create(new(.5, .5)))] };
        project = PresentationEditor.SetBranch(project, new() { BranchId = "primary", SlotSequenceProfile = slots });
        if (translation)
        {
            var lyrics = project.Lyrics; var branch = LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
                [.. lyrics.Lines.Select(line => LyricLine.Create("t-" + line.Id, "Translation") with { PrimaryLineId = line.Id })], "en", LyricBranchTimingMode.FollowPrimary);
            project = project with { Lyrics = lyrics with { Branches = lyrics.Branches.Add(branch) } };
            project = PresentationEditor.SetBranch(project, new() { BranchId = "translation", DefaultPlacement = ScenePlacement.Create(new(.5, .9)) });
        }
        return project;
    }
    [Fact]
    public void 重叠槽位先合成再乘组alpha而非每个孩子相乘()
    {
        var project = OverlapProject();
        var request = Request(ProjectSnapshot.FromProject(project), 1_500_000); Assert.True(request.PreparedFrame!.Scene.Lines.Length >= 2);
        using var renderer = new SkiaRenderPipeline(); using var original = renderer.Render(request);
        using var actual = renderer.Render(Apply(request, Opacity(Root(request.Project), .5)));
        Assert.Equal(255, original.Pixels.ToArray().Where((_, index) => index % 4 == 3).Max());
        Assert.InRange(actual.Pixels.ToArray().Where((_, index) => index % 4 == 3).Max(), 127, 128);
        Close(CapturedReference(request, .5), actual.Pixels.ToArray());
    }
    [Fact]
    public void 嵌套父组和布局组分别合成一次并保持输出副本()
    {
        var request = Request(ProjectSnapshot.FromProject(LeadInFixture.Create(true)));
        var program = request.Project.LyricPresentation.FindProgram("primary")!;
        var nested = Apply(request, Opacity(program.RootContainerId, .5), Opacity(program.DefaultContainerId, .5, "child"));
        Assert.Equal(2, nested.PreparedFrame!.GroupOpacities.Length);
        var copy = nested.PreparedFrame.WithVisualOutput([], [new(new("info"), "artifact", "Evaluate", request.TimeUs,
            new(Fx.DiagnosticSeverity.Info, "info", "信息"))]).WithImageEffects([]);
        Assert.Equal(nested.PreparedFrame.GroupOpacities, copy.GroupOpacities);
        using var renderer = new SkiaRenderPipeline(); using var actual = renderer.Render(nested with { PreparedFrame = copy });
        using var expected = renderer.Render(Apply(request, Opacity(program.RootContainerId, .25)));
        Close(expected.Pixels.ToArray(), actual.Pixels.ToArray(), 3);
    }
    [Fact]
    public void 恒等输出保持实例隐藏真实组校验失败不夹值()
    {
        var request = Request(ProjectSnapshot.FromProject(LeadInFixture.Create(true))); var frame = request.PreparedFrame!;
        Assert.Same(frame, frame.WithGroupOpacities([Opacity(Root(request.Project), 1)]));
        var hidden = frame.FinalGroupGeometry.First(group => group.WorldInkCorners.IsEmpty).Target.NodeId.Value;
        var hiddenRequest = Apply(request, Opacity(hidden, 0));
        using var native = new SkiaRenderPipeline(); using var a = native.Render(request); using var b = native.Render(hiddenRequest);
        Assert.Equal(a.Pixels.ToArray(), b.Pixels.ToArray());
        Assert.Equal("InvalidStyleOpacity", Assert.Throws<PreparedFrameException>(() => frame.WithGroupOpacities([Opacity(hidden, 1.2)])).Code);
        Assert.Equal("InvalidPreparedGroupOpacity", Assert.Throws<PreparedFrameException>(() => frame.WithGroupOpacities([Opacity("missing", .5)])).Code);
    }
    [Fact]
    public void 原始组颜色捕获明确拒绝忽略最终组alpha而原始覆盖不受影响()
    {
        var request = Request(ProjectSnapshot.FromProject(LeadInFixture.Create(true)));
        var capture = new SceneCaptureTarget.Group(new(new(Root(request.Project)))); var recipe = new SceneCaptureRecipe(true, true, true, true, SceneCaptureRegion.FillAndStroke);
        var bounds = new GraphRect(0, 0, 640, 360); using var renderer = new SkiaRenderPipeline();
        var grouped = Apply(request, Opacity(Root(request.Project), .5));
        Assert.Equal("GroupOpacityCapturePending", Assert.Throws<PreparedFrameException>(() => renderer.CaptureSceneTarget(grouped, capture, recipe,
            GraphInputSource.TargetColor, bounds, GraphExecutionBudget.Default)).Code);
        using var a = renderer.CaptureSceneTarget(request, capture, recipe, GraphInputSource.GlyphCoverage, bounds, GraphExecutionBudget.Default);
        using var b = renderer.CaptureSceneTarget(grouped, capture, recipe, GraphInputSource.GlyphCoverage, bounds, GraphExecutionBudget.Default);
        using var first = SKBitmap.FromImage(a.Image); using var second = SKBitmap.FromImage(b.Image); Assert.Equal(first.Bytes, second.Bytes);
    }
    [Fact]
    public void 组预算错误附加真实实例且取消和失败释放所有产物()
    {
        var request = Request(ProjectSnapshot.FromProject(LeadInFixture.Create(true))); request = Apply(request, Opacity(Root(request.Project), .5, "budget"));
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 2 }); using var renderer = new SkiaRenderPipeline(surfaceBudget: budget);
        var failure = Assert.Throws<EffectFailureException>(() => renderer.Render(request));
        Assert.Equal("budget", failure.InstanceId.Value); Assert.Equal("artifact-budget", failure.ArtifactHash); Assert.Equal("ImageSurfaceBudgetExceeded", failure.Code);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); Assert.Throws<OperationCanceledException>(() => renderer.Render(request with { CancellationToken = cancel.Token }));
    }
    [Theory]
    [InlineData("project", true)] [InlineData("node", true)] [InlineData("branch", false)]
    [InlineData("lines", false)] [InlineData("tokens", false)] [InlineData("clips", false)]
    public async Task 真实源码包保持工程节点组写权限且可重放(string kind, bool allowed)
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(true, true)) with { EffectInputsFingerprint = Fingerprint };
        string root = Root(snapshot);
        Fx.EffectBinding binding = kind switch { "project" => new Fx.EffectBinding.Project(), "node" => new Fx.EffectBinding.Node(new(root), false),
            "branch" => new Fx.EffectBinding.Branch(new("primary")), "lines" => new Fx.EffectBinding.Lines([new("first")]),
            "tokens" => new Fx.EffectBinding.Tokens([new("first-a")]), _ => new Fx.EffectBinding.Clips([new("chorus-clip")]) };
        var effect = (await Package.Value).CreateEffect("group-opacity", new("package"), binding, 1, JsonSerializer.SerializeToUtf8Bytes(new { node = root }));
        await using var source = await new TimelineFrameSourceFactory([effect], Fingerprint, static () => new SkiaRenderPipeline(), Fx.ReplayReason.Export).CreateAsync(snapshot);
        if (!allowed)
        {
            var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await source.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, new() { PixelWidth = 640, PixelHeight = 360 }));
            Assert.Equal("TargetOutsideBinding", error.Code); Assert.Equal("package", error.InstanceId.Value); return;
        }
        var frame = await source.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, new() { PixelWidth = 640, PixelHeight = 360 });
        Assert.Equal(.5, Assert.Single(frame.GroupOpacities).Opacity.Factor); Assert.Empty(frame.TokenStyles);
        using var renderer = new SkiaRenderPipeline(); var request = RenderRequest.At(snapshot, frame.TimeUs) with { Target = frame.Target, TransparentBackground = true, PreparedFrame = frame };
        using var pixels = renderer.Render(request); Close(CapturedReference(request, .5), pixels.Pixels.ToArray());
        _ = await source.PrepareAsync(11_000_000, Fx.OutputMode.Transparent, frame.Target);
        var replay = await source.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, frame.Target);
        using var again = renderer.Render(request with { PreparedFrame = replay }); Assert.Equal(pixels.Pixels.ToArray(), again.Pixels.ToArray());
    }
    [Fact]
    public void 交错后代作为连续单位在首项绘制且缓存保留诊断()
    {
        var request = Request(ProjectSnapshot.FromProject(OverlapProject(true)), 1_500_000);
        var frame = request.PreparedFrame!;
        var primary = frame.Scene.Lines.Where(item => item.Branch.Id == "primary").ToArray();
        var translation = frame.Scene.Lines.Where(item => item.Branch.Id == "translation").ToArray();
        Assert.True(primary.Length >= 2); Assert.True(translation.Length >= 2);
        // 显式构造合法运行父树、交错原绘制序；隔离规则必须依据它而非树列表顺序。
        var reordered = frame.Scene with { Lines = [primary[0], translation[0], .. primary.Skip(1), .. translation.Skip(1)] };
        request = request with { PreparedFrame = new(request.Project, request.TimeUs, frame.OutputMode, frame.Target, reordered, []) };
        request = Apply(request, Opacity(Root(request.Project), .5));
        using var native = new SkiaRenderPipeline(); var cached = new CachedRenderPipeline(native);
        using var fresh = cached.Render(request); using var hit = cached.Render(request);
        var diagnostic = Assert.Single(fresh.SceneDiagnostics);
        Assert.Equal("GroupIsolationChangesOrder", diagnostic.Diagnostic.Code); Assert.Equal("group", diagnostic.InstanceId.Value);
        Assert.Equal(fresh.SceneDiagnostics.ToArray(), hit.SceneDiagnostics.ToArray()); Assert.Equal(fresh.Pixels.ToArray(), hit.Pixels.ToArray());
        Close(CapturedReference(request, .5), fresh.Pixels.ToArray());
    }
    [Fact]
    public async Task 包声明元素外观不能借Style能力输出组透明度()
    {
        var package = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(EffectPackageFixture.GroupOpacity(declaresGroup: false)),
            new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));
        Assert.True(package.Artifact.Succeeded);
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create()) with { EffectInputsFingerprint = Fingerprint };
        var effect = package.CreateEffect("group-opacity", new("undeclared"), new Fx.EffectBinding.Node(new(Root(snapshot)), false), 1,
            JsonSerializer.SerializeToUtf8Bytes(new { node = Root(snapshot) }));
        await using var source = await new PureFrameSourceFactory([effect], Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await source.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal("UndeclaredPackageOutput", error.Code); Assert.Equal("undeclared", error.InstanceId.Value);
    }
    [Fact]
    public async Task 跨实例组因素最后校验并正确归因非法最终值()
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create()) with { EffectInputsFingerprint = Fingerprint };
        var package = await Package.Value; var binding = new Fx.EffectBinding.Node(new(Root(snapshot)), false);
        CompiledFrameEffect Effect(string id, double factor) => package.CreateEffect("group-opacity", new(id), binding, 1,
            JsonSerializer.SerializeToUtf8Bytes(new { node = Root(snapshot), factor }));
        var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var valid = await new PureFrameSourceFactory([Effect("large", 2), Effect("small", .25)], Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        Assert.Equal(.5, Assert.Single((await valid.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, target)).GroupOpacities).Opacity.Factor);
        await using var invalid = await new PureFrameSourceFactory([Effect("large", 2), Effect("still-large", .75)], Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await invalid.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, target));
        Assert.Equal("still-large", error.InstanceId.Value); Assert.Equal("ComposeOutputs", error.Stage); Assert.Equal("InvalidStyleOpacity", error.Code);
    }
}
