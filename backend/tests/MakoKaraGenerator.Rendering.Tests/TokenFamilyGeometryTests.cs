using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Projection;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class TokenFamilyGeometryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 元素族使用真实父组槽位单句动画矩阵并且注音不改变基础Advance(bool slots)
    {
        var project = LeadInFixture.Create(slots); var tree = project.LyricPresentation;
        var program = tree.FindProgram("primary")!; var root = tree.FindNode(program.RootContainerId)!;
        tree = tree with { Nodes = tree.Nodes.Replace(root, root with { Placement = ScenePlacement.Create(new(.57, .41), rotationDegrees: 23, scale: .8) }),
            LineOverrides = tree.LineOverrides.Add(new() { BranchId = "primary", LineId = "first",
                LocalTransform = new() { OffsetXRatio = .03, OffsetYRatio = -.02, Scale = .9, RotationDegrees = 11 },
                EnterAnimation = new() { DurationUs = 2_000_000, StartScale = .8f, StartOffsetPx = 8, StartOpacity = 1 } }) };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree });
        using SkiaRenderPipeline pipeline = new();
        SceneFrame frame = pipeline.GetSceneFrame(snapshot, 10_000_000);
        var line = frame.Lines.Single(item => item.Frame.Line!.Id == "first");
        var family = frame.TokenGeometries.Single(item => item.WithRuby.TokenId.Value == "first-a");
        var matrix = line.Geometry!.WorldMatrix!.Value;
        Assert.Equal(new Fx.Affine2(matrix.A, matrix.B, matrix.C, matrix.D, matrix.Tx, matrix.Ty), family.BaseGeometry.BaseWorldMatrix);
        Assert.Equal(family.BaseGeometry.BaseWorldMatrix, family.FamilyGeometry.BaseWorldMatrix);
        var ruby = Assert.Single(family.Ruby);
        Assert.Equal(family.BaseGeometry.BaseWorldMatrix, ruby.Geometry.BaseWorldMatrix);
        Assert.Equal(family.BaseGeometry.AdvanceBounds, family.FamilyGeometry.AdvanceBounds);
        Assert.True(family.FamilyGeometry.InkBounds!.Value.Top < family.BaseGeometry.InkBounds!.Value.Top);
        Assert.Equal(family.BaseGeometry.DrawUnits.Length + ruby.Geometry.DrawUnits.Length, family.FamilyGeometry.DrawUnits.Length);
        Assert.All(frame.TokenGeometries, item => Assert.True(item.FamilyGeometry.CanTransformIndependently));
        var rubyNode = frame.Elements.Single(item => item.SourceBinding.RubyId == ruby.RubyId.Value);
        foreach (var (point, index) in ruby.Geometry.WorldInkCorners.Select((point, index) => (point, index)))
        {
            Assert.Equal(rubyNode.WorldCorners[index].X, point.X, 5);
            Assert.Equal(rubyNode.WorldCorners[index].Y, point.Y, 5);
        }
    }

    [Fact]
    public void 前奏与起唱共享装载及演唱身份提示没有伪造元素()
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(true));
        using SkiaRenderPipeline pipeline = new();
        var prepared = pipeline.GetSceneFrame(snapshot, 9_500_000);
        var singing = pipeline.GetSceneFrame(snapshot, 12_500_000);
        Assert.Equal(3, prepared.TextMarks.Length);
        var before = FrameProjection.Tokens(prepared).Single(item => item.TokenId.Value == "first-a");
        var after = FrameProjection.Tokens(singing).Single(item => item.TokenId.Value == "first-a");
        Assert.Equal(before.BindingId, after.BindingId);
        Assert.Equal(before.OccurrenceId, after.OccurrenceId);
        Assert.False(before.Performance.IsCurrent); Assert.Equal(0, before.Performance.Progress);
        Assert.True(after.Performance.IsCurrent); Assert.Equal(.5, after.Performance.Progress);
        Assert.Equal(FrameProjection.Tokens(prepared).Select(item => item.WithRuby), prepared.TokenGeometries.Select(item => item.WithRuby));
        Assert.DoesNotContain(FrameProjection.Tokens(prepared), item => prepared.TextMarks.Any(mark => mark.NodeId == item.TokenId.Value));
    }

    [Fact]
    public void 共享Token墨迹几何包住真实基础字和注音像素()
    {
        var project = LeadInFixture.Create();
        var snapshot = ProjectSnapshot.FromProject(project with { LyricStyle = project.LyricStyle with { Stroke = null, Shadow = null } });
        using SkiaRenderPipeline pipeline = new();
        var frame = pipeline.GetSceneFrame(snapshot, 12_500_000);
        var first = frame.Lines.Single(item => item.Frame.Line!.Id == "first");
        using var pixels = pipeline.Render(RenderRequest.At(snapshot, frame.TimeUs) with { TransparentBackground = true });
        int visible = 0;
        for (int y = 0; y < pixels.Height; y++) for (int x = 0; x < pixels.Width; x++)
        {
            if (pixels.PixelMemory.Span[y * pixels.Stride + x * 4 + 3] < 100 || !first.Geometry!.Contains(new(x + .5, y + .5), 2)) continue;
            visible++;
            Assert.Contains(frame.TokenGeometries.Where(item => item.LineId.Value == "first"), token =>
            {
                var corners = token.FamilyGeometry.WorldInkCorners;
                return !corners.IsEmpty && x + .5 >= corners.Min(point => point.X) - 2 && x + .5 <= corners.Max(point => point.X) + 2
                    && y + .5 >= corners.Min(point => point.Y) - 2 && y + .5 <= corners.Max(point => point.Y) + 2;
            });
        }
        Assert.True(visible > 100);
    }

    [Fact]
    public void 合字跨两个源元素时几何明确不可独立但静态原画可用()
    {
        var line = LyricLine.Create("line", "office", new Core.Time.TimeRange(0, 6_000_000), TimingStatus.Imported,
            [.. "office".Select((character, index) => LyricToken.Create("t" + index, character.ToString(),
                new Core.Time.TimeRange(index * 1_000_000, (index + 1) * 1_000_000), TimingStatus.Imported))]);
        var snapshot = new ProjectSnapshot { Name = "ligature", Lyrics = LyricsDocument.Create([line]),
            Composition = new() { CanvasWidth = 640, CanvasHeight = 360 },
            LyricStyle = LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 56 } };
        using SkiaRenderPipeline pipeline = new();
        var frame = pipeline.GetSceneFrame(snapshot, 2_500_000);
        Assert.All(frame.TokenGeometries.Skip(1).Take(3), item =>
        {
            Assert.False(item.BaseGeometry.CanTransformIndependently);
            Assert.Equal("UnsupportedGlyphPartition", item.BaseGeometry.DiagnosticCode);
        });
        using var pixels = pipeline.Render(RenderRequest.At(snapshot, frame.TimeUs) with { TransparentBackground = true });
        Assert.True(pixels.PixelMemory.Span.ContainsAnyExcept((byte)0));
        Assert.All(frame.TokenGeometries.Where(item => item.WithRuby.TokenId.Value is "t0" or "t4" or "t5"),
            item => Assert.True(item.BaseGeometry.CanTransformIndependently));
    }
}
