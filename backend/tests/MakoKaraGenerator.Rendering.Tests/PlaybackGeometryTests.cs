using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Projection;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class PlaybackGeometryTests
{
    private static ProjectSnapshot Snapshot(LyricLine line, float spacing = 0) => new()
    {
        Name = "真实播放头", Lyrics = LyricsDocument.Create([line]), Composition = new() { CanvasWidth = 640, CanvasHeight = 360 },
        LyricStyle = LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 48, LetterSpacingPx = spacing,
            UnsungColor = RgbaColor.White, SungColor = new(255, 0, 0, 255), Stroke = null, Shadow = null },
    };
    private static PreparedRenderFrame Prepare(SkiaRenderPipeline renderer, ProjectSnapshot snapshot, long time,
        params Fx.TokenFamilyTransform[] transforms)
        => new(snapshot, time, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), renderer.GetSceneFrame(snapshot, time), [.. transforms]);

    [Theory]
    [InlineData("MMMMMMMM", 0)] [InlineData("请照亮我前行", 7)]
    public void 播放头横坐标与真实逐字换色像素边界一致(string text, float spacing)
    {
        var line = LyricLine.Create("line", text, new(0, 1_000_000), TimingStatus.Imported,
            [LyricToken.Create("token", text, new(0, 1_000_000), TimingStatus.Imported)]);
        var snapshot = Snapshot(line, spacing); using SkiaRenderPipeline renderer = new();
        var prepared = Prepare(renderer, snapshot, 432_100);
        var cursor = Assert.Single(prepared.Playback.GetCursors(Fx.CursorAnchorMode.Baseline));
        Assert.Equal(.4321, cursor.Progress); Assert.Equal(new Fx.TokenId("token"), cursor.TokenId);
        using var pixels = renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { TransparentBackground = true, PreparedFrame = prepared });
        int red = 0, white = 0;
        for (int y = 0; y < pixels.Height; y++) for (int x = 0; x < pixels.Width; x++)
        {
            int offset = y * pixels.Stride + x * 4; var span = pixels.PixelMemory.Span;
            if (span[offset + 3] < 200) continue;
            if (span[offset + 2] > 180 && span[offset + 1] < 5 && span[offset] < 5)
            { red++; Assert.True(x < cursor.Position.X + 1); }
            if (span[offset] > 180 && span[offset + 1] > 180 && span[offset + 2] > 180)
            { white++; Assert.True(x >= Math.Floor(cursor.Position.X) - 1); }
        }
        Assert.True(red > 30); Assert.True(white > 30);
    }
    [Fact]
    public void Emoji家庭字簇保留真实相邻进度边界且不按UTF16拆开()
    {
        string emoji = "👩‍👩‍👧‍👦";
        var line = LyricLine.Create("line", "A" + emoji + "B", new(0, 3_000_000), TimingStatus.Imported,
            [LyricToken.Create("a", "A", new(0, 1_000_000), TimingStatus.Imported),
             LyricToken.Create("emoji", emoji, new(1_000_000, 2_000_000), TimingStatus.Imported),
             LyricToken.Create("b", "B", new(2_000_000, 3_000_000), TimingStatus.Imported)]);
        using SkiaRenderPipeline renderer = new(); var prepared = Prepare(renderer, Snapshot(line, 3), 1_500_000);
        var geometries = prepared.FinalGeometry;
        var cursor = Assert.Single(prepared.Playback.GetCursors(Fx.CursorAnchorMode.Baseline));
        Assert.Equal("emoji", cursor.TokenId!.Value.Value);
        Assert.True(geometries[1].BaseGeometry.CanTransformIndependently);
        Assert.Equal(geometries[0].ProgressPath!.EndX, geometries[1].ProgressPath!.StartX);
        Assert.Equal(geometries[1].ProgressPath!.EndX, geometries[2].ProgressPath!.StartX);
        Assert.Equal(geometries[2].ProgressPath!.EndX, Assert.Single(prepared.Scene.LineGeometries).ProgressPath.EndX);
        Assert.True(geometries[1].ProgressPath!.EndX > geometries[1].ProgressPath!.StartX);
        using var pixels = renderer.Render(RenderRequest.At(prepared.Snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
        Assert.True(pixels.PixelMemory.Span.ContainsAnyExcept((byte)0));
    }
    [Fact]
    public void 空白共享字簇按真实整形路径而非墨迹框取进度()
    {
        var tokens = "office ".Select((character, index) => LyricToken.Create("t" + index, character.ToString(),
            new Core.Time.TimeRange(index * 1_000_000, (index + 1) * 1_000_000), TimingStatus.Imported));
        var line = LyricLine.Create("line", "office ", new(0, 7_000_000), TimingStatus.Imported, [.. tokens]);
        var snapshot = Snapshot(line, 4) with { LyricStyle = Snapshot(line, 4).LyricStyle with { FontFamilyName = TestFonts.LigatureFfi } };
        using SkiaRenderPipeline renderer = new();
        var space = Prepare(renderer, snapshot, 6_500_000);
        var spaceGeometry = space.FinalGeometry.Single(item => item.WithRuby.TokenId.Value == "t6");
        Assert.Null(spaceGeometry.BaseGeometry.InkBounds);
        Assert.True(spaceGeometry.ProgressPath!.EndX > spaceGeometry.ProgressPath.StartX);
        Assert.Equal(.5, Assert.Single(space.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter)).Progress);
        var shared = Prepare(renderer, snapshot, 2_500_000);
        Assert.False(shared.FinalGeometry.Single(item => item.WithRuby.TokenId.Value == "t2").BaseGeometry.CanTransformIndependently);
        Assert.Equal("t2", Assert.Single(shared.Playback.GetCursors(Fx.CursorAnchorMode.Baseline)).TokenId!.Value.Value);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 前奏槽位父组微调和元素族缩放只应用一次且注音不改变播放头路径(bool slots)
    {
        var project = LeadInFixture.Create(slots); var tree = project.LyricPresentation;
        var root = tree.FindNode(tree.FindProgram("primary")!.RootContainerId)!;
        tree = tree with { Nodes = tree.Nodes.Replace(root, root with { Placement = ScenePlacement.Create(new(.6, .4), rotationDegrees: 27, scale: .8) }),
            LineOverrides = tree.LineOverrides.Add(new() { BranchId = "primary", LineId = "first",
                LocalTransform = new() { OffsetXRatio = .03, OffsetYRatio = -.02, RotationDegrees = 13, Scale = .9 } }) };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree }); using SkiaRenderPipeline renderer = new();
        var before = Prepare(renderer, snapshot, 9_500_000); Assert.Empty(before.Playback.GetCursors(Fx.CursorAnchorMode.Baseline));
        var plain = Prepare(renderer, snapshot, 12_500_000);
        var basis = plain.FinalGeometry.Single(item => item.WithRuby.TokenId.Value == "first-a");
        var local = Fx.Affine2.ScaleAt(1.7, basis.BaseGeometry.AdvanceBounds.Center);
        var scaled = Prepare(renderer, snapshot, 12_500_000, new Fx.TokenFamilyTransform(basis.WithRuby, local));
        var cursor = Assert.Single(scaled.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter));
        Assert.Equal(before.Scene.TokenGeometries.Single(item => item.WithRuby.TokenId.Value == "first-a").WithRuby.BindingId, cursor.BindingId);
        var path = basis.ProgressPath!; var expected = basis.BaseGeometry.BaseWorldMatrix.Compose(local).Map(path.At(.5, Fx.CursorAnchorMode.AdvanceCenter));
        Assert.Equal(expected, cursor.Position);
        Assert.Equal(cursor.Identity, Assert.Single(plain.Playback.GetCursors(Fx.CursorAnchorMode.Baseline)).Identity);
        Assert.Equal(1, Math.Sqrt(cursor.Direction.X * cursor.Direction.X + cursor.Direction.Y * cursor.Direction.Y), 12);
        var withNoRuby = snapshot with { Lyrics = snapshot.Lyrics with { Branches = [.. snapshot.Lyrics.Branches.Select(branch => branch with
            { Lines = [.. branch.Lines.Select(line => line with { RubyAnnotations = [] })] })] } };
        var noRuby = Prepare(renderer, withNoRuby, 12_500_000);
        var geometry = noRuby.FinalGeometry.Single(item => item.WithRuby.TokenId.Value == "first-a");
        Assert.Equal(path.EndX - path.StartX, geometry.ProgressPath!.EndX - geometry.ProgressPath.StartX);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 全部没有逐字时间才回退行进度且行内空档没有播放头(bool emptyTokens)
    {
        var line = LyricLine.Create("line", "歌词", new(100, 900), TimingStatus.Imported,
            emptyTokens ? [] : [LyricToken.Create("a", "歌"), LyricToken.Create("b", "词")]);
        using SkiaRenderPipeline renderer = new(); var snapshot = Snapshot(line);
        var cursor = Assert.Single(Prepare(renderer, snapshot, 500).Playback.GetCursors(Fx.CursorAnchorMode.Baseline));
        Assert.Equal(Fx.ProgressSource.Line, cursor.ProgressSource); Assert.Null(cursor.TokenId); Assert.Null(cursor.OccurrenceId); Assert.Equal(.5, cursor.Progress);
        Assert.Empty(Prepare(renderer, snapshot, 900).Playback.GetCursors(Fx.CursorAnchorMode.Baseline));
        var partiallyTimed = line with { Tokens = [LyricToken.Create("a", "歌", new(100, 300), TimingStatus.Imported), LyricToken.Create("b", "词")] };
        Assert.Empty(Prepare(renderer, Snapshot(partiallyTimed), 500).Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter));
    }
    [Fact]
    public void 并发真实演唱保留全部播放头按源顺序排列()
    {
        var project = LeadInFixture.Create(true); var first = project.Lyrics.PrimaryBranch!;
        var line = first.Lines[1] with { Time = new(12_000_000, 18_000_000), Tokens =
            [LyricToken.Create("second-a", "歌", new(12_200_000, 13_500_000), TimingStatus.Imported),
             LyricToken.Create("second-b", "詞", new(13_500_000, 18_000_000), TimingStatus.Imported)] };
        project = project with { Lyrics = project.Lyrics with { Branches = project.Lyrics.Branches.Replace(first, first with { Lines = first.Lines.Replace(first.Lines[1], line) }) } };
        var tree = project.LyricPresentation; var container = tree.FindNode(tree.FindProgram("primary")!.DefaultContainerId)!;
        project = project with { LyricPresentation = tree with { Nodes = tree.Nodes.Replace(container, container with
            { Layout = container.Layout! with { SlotAnimation = container.Layout.SlotAnimation! with { RevealMode = SlotRevealMode.LeadBeforeNextStart } } }) } };
        using SkiaRenderPipeline renderer = new(); var prepared = Prepare(renderer, ProjectSnapshot.FromProject(project), 12_500_000);
        var cursors = prepared.Playback.GetCursors(Fx.CursorAnchorMode.Baseline);
        Assert.Equal((string[])["first-a", "second-a"], cursors.Select(cursor => cursor.TokenId!.Value.Value));
        Assert.Single(cursors.Select(cursor => cursor.TrackId).Distinct());
        Assert.Equal(FrameProjection.Tokens(prepared.Scene).Where(token => token.Performance.IsCurrent).Select(token => token.OccurrenceId),
            cursors.Select(cursor => cursor.OccurrenceId));
    }
}
