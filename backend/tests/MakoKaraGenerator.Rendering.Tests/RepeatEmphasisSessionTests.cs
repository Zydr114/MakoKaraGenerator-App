using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class RepeatEmphasisSessionTests
{
    private const string Fingerprint = "repeat-author-source-parameters";
    private static readonly Lazy<Task<CompiledEffectPackage>> Repeat = new(async () =>
    {
        var contents = await EffectPackageStorage.ReadSupportedDirectoryAsync(
            Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "RepeatEmphasis"));
        return await V2RenderPackageHelper.CompileAsync(contents);
    });
    private static async Task<PureFrameSourceFactory> Factory(string scope = "branch", Fx.EffectBinding? binding = null,
        Fx.SourceSubscription? subscription = null)
    {
        var package = await Repeat.Value;
        var parameters = new Fx.ParameterValues(new Dictionary<string, Fx.ParameterValue>
        {
            ["countScope"] = new Fx.ParameterValue.Choice(scope), ["scaleStep"] = new Fx.ParameterValue.Number(.2),
            ["maxScale"] = new Fx.ParameterValue.Number(2), ["darkenStep"] = new Fx.ParameterValue.Number(.2),
            ["minBrightness"] = new Fx.ParameterValue.Number(.2),
        });
        var effect = package.CreateEffect(package.Definition.Controllers[0].Id, new("repeat"),
            binding ?? new Fx.EffectBinding.Project(), 0) with
        {
            Parameters = parameters,
            Subscription = subscription ?? new Fx.SourceSubscription.Project(),
        };
        return new([effect], Fingerprint, static () => new SkiaRenderPipeline());
    }
    private static Project ThreeRows()
    {
        var rows = Enumerable.Range(0, 3).Select(index => LyricLine.Create("row-" + index, "田", new(index * 500_000, index * 500_000 + 400_000), TimingStatus.Edited,
            [LyricToken.Create("word-" + index, "田", new(index * 500_000, index * 500_000 + 400_000), TimingStatus.Edited)])
            with { RubyAnnotations = [RubyAnnotation.Create("ruby-" + index, "word-" + index, "た")] });
        return Project.Create("repeat pixels", Composition.Create(640, 360, Core.Time.FrameRate.Fps30, outputDurationUs: 2_000_000),
            LyricsDocument.Create(rows), lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64,
                SungColor = new(200, 100, 50, 255), UnsungColor = RgbaColor.White,
                Stroke = StrokeSpec.Create(new(10, 100, 200, 255), 2),
                Shadow = new() { Color = new(20, 30, 50, 180), DistancePx = 3, BlurRadiusPx = 2, AngleDegrees = 45 } });
    }
    private static ProjectSnapshot Snapshot(Project project) => ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
    private static async Task<PreparedRenderFrame> Frame(IEvaluatedFrameSource source, ProjectSnapshot snapshot, long time)
        => await source.PrepareAsync(time, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
    private static double Scale(PreparedRenderFrame frame, string token)
        {
        Assert.Contains(frame.Scene.TokenGeometries, item => item.WithRuby.TokenId.Value == token);
        return frame.TokenTransforms.SingleOrDefault(item => item.Target.TokenId.Value == token)?.LocalMatrix.A ?? 1;
    }
    private static byte[] Pixels(PreparedRenderFrame frame)
    {
        using SkiaRenderPipeline renderer = new();
        using var pixels = renderer.Render(RenderRequest.At(frame.Snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true });
        return pixels.PixelMemory.ToArray();
    }

    [Theory]
    [InlineData(1, 750_000)] [InlineData(2, 1_250_000)]
    public async Task 真正Repeat源码的第二第三次外观等于独立静态样式及整体矩阵像素(int rowIndex, long time)
    {
        var project = ThreeRows(); var snapshot = Snapshot(project);
        await using var source = await (await Factory()).CreateAsync(snapshot);
        var prepared = await Frame(source, snapshot, time);
        Assert.Equal(1 + .2 * rowIndex, Scale(prepared, "word-" + rowIndex), 12);
        var basis = Assert.Single(prepared.Scene.TokenGeometries); var final = Assert.Single(prepared.FinalGeometry);
        Assert.Equal(final.BaseGeometry.BaseWorldMatrix, Assert.Single(final.Ruby).Geometry.BaseWorldMatrix);
        var rootId = project.LyricPresentation.FindProgram("primary")!.RootContainerId;
        var root = project.LyricPresentation.FindNode(rootId)!;
        var parent = SceneContainerGeometryResolver.Resolve(prepared.Scene.Definition, snapshot.Composition)[rootId].WorldMatrix;
        Fx.Affine2 matrix = new(parent.A, parent.B, parent.C, parent.D, parent.Tx, parent.Ty);
        Assert.True(matrix.TryInverse(out var inverse));
        var local = inverse.Compose(basis.BaseGeometry.BaseWorldMatrix); Assert.True(local.TryInverse(out var localInverse));
        var equivalent = final.BaseGeometry.BaseWorldMatrix.Compose(localInverse);
        double scale = Math.Sqrt(equivalent.A * equivalent.A + equivalent.B * equivalent.B);
        var placement = ScenePlacement.Create(new(equivalent.Tx / snapshot.Composition.CanvasWidth, equivalent.Ty / snapshot.Composition.CanvasHeight),
            ScenePlacementAnchor.TopLeft, Math.Atan2(equivalent.B, equivalent.A) * 180 / Math.PI, scale);
        double brightness = 1 - rowIndex * .2;
        var expected = snapshot with { EffectInputsFingerprint = null,
            LyricStyle = snapshot.LyricStyle with { SungColor = new((byte)Math.Round(200 * brightness), (byte)Math.Round(100 * brightness), (byte)Math.Round(50 * brightness), 255) },
            LyricPresentation = project.LyricPresentation with { Nodes = project.LyricPresentation.Nodes.Replace(root, root with { Placement = placement }) } };
        using SkiaRenderPipeline renderer = new();
        using var plainExpected = renderer.Render(RenderRequest.At(expected, time) with { TransparentBackground = true });
        Assert.Equal(plainExpected.PixelMemory.ToArray(), Pixels(prepared));
        Assert.True(plainExpected.PixelMemory.Span.ContainsAnyExcept((byte)0));
    }
    [Theory]
    [InlineData(24)] [InlineData(30)] [InlineData(60)]
    public async Task 不同帧率中段请求跳转重绘与独立导出状态像素一致(int fps)
    {
        var snapshot = Snapshot(ThreeRows()); var targetTime = 1_250_001L;
        var factory = await Factory(); await using var source = await factory.CreateAsync(snapshot);
        for (long index = 0; index * 1_000_000 / fps < targetTime; index++) await Frame(source, snapshot, index * 1_000_000 / fps);
        var sequential = await Frame(source, snapshot, targetTime);
        var redraw = await Frame(source, snapshot, targetTime);
        await Frame(source, snapshot, 10_001);
        var rewind = await Frame(source, snapshot, targetTime);
        await using var direct = await (await Factory()).CreateAsync(snapshot);
        var exported = await Frame(direct, snapshot, targetTime);
        Assert.Equal(1.4, Scale(sequential, "word-2"), 12);
        foreach (var other in new[] { redraw, rewind, exported })
        {
            Assert.Equal(sequential.TokenTransforms.ToArray(), other.TokenTransforms.ToArray());
            Assert.Equal(sequential.TokenStyles.ToArray(), other.TokenStyles.ToArray());
            Assert.Equal(Pixels(sequential), Pixels(other));
        }
    }
    [Theory]
    [InlineData("branch", 1.4)] [InlineData("project", 1.6)]
    public async Task 分支或工程计数含隐藏源且输出只限指定元素(string scope, double expectedScale)
    {
        var project = ThreeRows();
        var hidden = LyricBranch.Create("hidden", "hidden", LyricBranchRole.Custom,
            [LyricLine.Create("hidden-row", "田", new(750_000, 900_000), TimingStatus.Edited,
                [LyricToken.Create("hidden-word", "田", new(750_000, 900_000), TimingStatus.Edited)])], timingMode: LyricBranchTimingMode.None);
        project = project with { Lyrics = project.Lyrics with { Branches = project.Lyrics.Branches.Add(hidden) } };
        var snapshot = Snapshot(project);
        await using var source = await (await Factory(scope, new Fx.EffectBinding.Tokens([new("word-2")]))).CreateAsync(snapshot);
        var frame = await Frame(source, snapshot, 1_250_000);
        Assert.Equal(expectedScale, Scale(frame, "word-2"), 12);
        Assert.Single(frame.TokenTransforms); Assert.Single(frame.TokenStyles);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 前奏提前显示不计数槽位旧句保留原次数起唱只增加一次(bool slots)
    {
        var project = LeadInFixture.Create(slots, crossClip: true);
        if (slots)
        {
            // 保留已唱旧句到第二句起唱以后，检验其 occurrence 不读取新的总次数。
            var primary = project.Lyrics.PrimaryBranch!;
            var firstLine = primary.Lines.Single(line => line.Id == "first");
            var lyrics = project.Lyrics with { Branches = project.Lyrics.Branches.Replace(primary,
                primary with { Lines = primary.Lines.Replace(firstLine, firstLine with { Time = new(11_500_000, 21_000_000) }) }) };
            project = project with { Lyrics = lyrics };
        }
        var snapshot = Snapshot(project);
        await using var source = await (await Factory(binding: new Fx.EffectBinding.Tokens([new("first-a"), new("second-a"), new("chorus-a")]))).CreateAsync(snapshot);
        var preparing = await Frame(source, snapshot, 9_500_000); Assert.Empty(preparing.TokenTransforms); Assert.Empty(preparing.TokenStyles);
        var first = await Frame(source, snapshot, 12_500_000); Assert.Equal(1, Scale(first, "first-a"));
        Assert.Equal(preparing.Scene.Lines.Single(line => line.Frame.Line!.Id == "first").BindingId, first.Scene.Lines.Single(line => line.Frame.Line!.Id == "first").BindingId);
        var second = await Frame(source, snapshot, 18_000_000); Assert.Equal(1.2, Scale(second, "second-a"), 12);
        if (slots) Assert.Equal(1, Scale(second, "first-a"));
        var chorusPreparing = await Frame(source, snapshot, 38_000_000); Assert.Empty(chorusPreparing.TokenTransforms);
        var chorus = await Frame(source, snapshot, 40_500_000); Assert.Equal(1.4, Scale(chorus, "chorus-a"), 12);
        var again = await Frame(source, snapshot, 40_500_000); Assert.Equal(chorus.TokenTransforms.ToArray(), again.TokenTransforms.ToArray());
    }
    [Fact]
    public async Task 未定时字不计数也不获得强调精确文本不合并异字()
    {
        var lineA = LyricLine.Create("row-a", "田", new(0, 400_000), TimingStatus.Edited, [LyricToken.Create("a", "田", new(0, 400_000), TimingStatus.Edited)]);
        var lineMissing = LyricLine.Create("row-missing", "田", null, TimingStatus.Missing, [LyricToken.Create("missing", "田")]);
        var lineDifferent = LyricLine.Create("row-different", "異", new(500_000, 900_000), TimingStatus.Edited, [LyricToken.Create("different", "異", new(500_000, 900_000), TimingStatus.Edited)]);
        var lineB = LyricLine.Create("row-b", "田", new(1_000_000, 1_500_000), TimingStatus.Edited, [LyricToken.Create("b", "田", new(1_000_000, 1_500_000), TimingStatus.Edited)]);
        var project = ThreeRows() with { Lyrics = LyricsDocument.Create([lineA, lineMissing, lineDifferent, lineB]) };
        var snapshot = Snapshot(project); await using var source = await (await Factory()).CreateAsync(snapshot);
        var frameA = await Frame(source, snapshot, 200_000);
        var frameDiff = await Frame(source, snapshot, 700_000);
        var frameB = await Frame(source, snapshot, 1_250_000);
        foreach (var frame in new[] { frameA, frameDiff, frameB })
        {
            Assert.DoesNotContain(frame.Scene.TokenGeometries, item => item.WithRuby.TokenId.Value == "missing");
            Assert.DoesNotContain(frame.TokenTransforms, item => item.Target.TokenId.Value == "missing");
        }
        Assert.Equal(1, Scale(frameA, "a")); Assert.Equal(1, Scale(frameDiff, "different")); Assert.Equal(1.2, Scale(frameB, "b"), 12);
    }
}
