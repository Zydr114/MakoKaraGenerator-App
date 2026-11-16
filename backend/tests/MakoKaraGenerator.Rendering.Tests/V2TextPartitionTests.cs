using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;
using V2 = MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class V2TextPartitionTests
{
    [Fact]
    public void 字簇来自真实整行整形且基础字和Ruby有各自身份()
    {
        var snapshot = Source(); using SkiaRenderPipeline renderer = new();
        var scene = renderer.GetSceneFrame(snapshot, 2_000_000); var token = Assert.Single(scene.TokenGeometries);
        Assert.Equal(token.BaseGeometry.DrawUnits.Length, token.Clusters.Count(item => item.Target.RubyId is null));
        Assert.Equal(Assert.Single(token.Ruby).Geometry.DrawUnits.Length, token.Clusters.Count(item => item.Target.RubyId is not null));
        Assert.All(token.Clusters, cluster =>
        {
            Assert.Equal(token.WithRuby.TokenId, Assert.Single(cluster.OwnerTokens));
            Assert.True(cluster.Geometry.CanTransformIndependently);
            Assert.Equal(Assert.Single(cluster.Geometry.DrawUnits), cluster.Target.DrawUnitId);
            Assert.Equal(token.WithRuby.BindingId, cluster.Target.BindingId);
        });
        Assert.Equal(token.Clusters.Select(item => item.Target).Distinct().Count(), token.Clusters.Length);
    }

    [Fact]
    public void 基础字仿射实际改变像素而Ruby几何和像素保持原父族基线()
    {
        var snapshot = Source(); using SkiaRenderPipeline renderer = new();
        var scene = renderer.GetSceneFrame(snapshot, 2_000_000); var token = Assert.Single(scene.TokenGeometries);
        var prepared = Prepared(snapshot, scene).WithTextPartitions([new(token.BaseOnly, Fx.Affine2.Translation(25, 35))], [], []);
        var final = Assert.Single(prepared.FinalGeometry);
        Assert.Equal(token.BaseGeometry.BaseWorldMatrix.Compose(Fx.Affine2.Translation(25, 35)), final.BaseGeometry.BaseWorldMatrix);
        Assert.Equal(Assert.Single(token.Ruby).Geometry.BaseWorldMatrix, Assert.Single(final.Ruby).Geometry.BaseWorldMatrix);
        using var plain = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true });
        using var actual = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true, PreparedFrame = prepared });
        Assert.False(plain.PixelMemory.Span.SequenceEqual(actual.PixelMemory.Span));
        AssertRubyPixelsEqual(plain, actual, token);
    }

    [Fact]
    public void 基础字外观关闭填充不会关闭Ruby且副本保留真实分区输出()
    {
        var snapshot = Source(); using SkiaRenderPipeline renderer = new();
        var scene = renderer.GetSceneFrame(snapshot, 2_000_000); var token = Assert.Single(scene.TokenGeometries);
        var prepared = Prepared(snapshot, scene).WithTextPartitions([], [new(token.BaseOnly, new(opacityFactor: 0))], []);
        var copied = prepared.WithGroupOpacities([]).WithOverlayOutput([], []);
        Assert.Single(copied.BaseStyles);
        using var plain = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true });
        using var actual = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true, PreparedFrame = copied });
        Assert.True(Alpha(actual) < Alpha(plain)); Assert.True(Alpha(actual) > 0);
        AssertRubyPixelsEqual(plain, actual, token);
    }

    [Fact]
    public void 非空基础字外观继续组合字族覆盖且Ruby保持父族像素()
    {
        var snapshot = Source(); using SkiaRenderPipeline renderer = new();
        var scene = renderer.GetSceneFrame(snapshot, 2_000_000); var token = Assert.Single(scene.TokenGeometries);
        var parent = new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition), scene, [], [new(token.WithRuby, new(opacityFactor: .5))]);
        var prepared = parent.WithTextPartitions([], [new(token.BaseOnly, new(opacityFactor: .5))], []);
        Assert.Equal(.25f, prepared.GetBaseDrawingStyle(token.BaseOnly).Opacity);
        Assert.Equal(.5f, prepared.GetTokenStyle(token.WithRuby).Opacity);
        using var plain = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true, PreparedFrame = parent });
        using var actual = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true, PreparedFrame = prepared });
        Assert.True(Alpha(actual) < Alpha(plain)); Assert.True(Alpha(actual) > 0);
        AssertRubyPixelsEqual(plain, actual, token);
    }

    [Fact]
    public void 空字族外观输出仍在准备帧主构造拒绝缺失冻结元素外观()
    {
        var snapshot = Source(); using SkiaRenderPipeline renderer = new();
        var scene = renderer.GetSceneFrame(snapshot, 2_000_000); var line = Assert.Single(scene.Lines);
        var metadata = new ExitReplicaMetadata(scene.TimeUs, 0, [],
            PresetSceneStyleProjection.LyricsForBinding(snapshot, line, null, scene),
            ImmutableDictionary<Fx.TokenId, PresetStyleResolution>.Empty);
        var invalid = scene with { Lines = [line with { ExitReplica = metadata }] };
        var error = Assert.Throws<PreparedFrameException>(() => Prepared(snapshot, invalid));
        Assert.Equal("MissingBaselineTarget", error.Code);
    }

    [Fact]
    public void 空字族外观输出仍在准备帧主构造拒绝实际样式父链循环()
    {
        var snapshot = Source(); using SkiaRenderPipeline renderer = new();
        var scene = renderer.GetSceneFrame(snapshot, 2_000_000); var line = Assert.Single(scene.Lines);
        var invalid = scene with { Elements = [.. scene.Elements.Select(node => node.Id == line.ElementId
            ? node with { ParentId = node.Id } : node)] };
        var error = Assert.Throws<PreparedFrameException>(() => Prepared(snapshot, invalid));
        Assert.Equal("InvalidSceneTransform", error.Code);
    }

    [Fact]
    public void 单个基础字簇独立绘制并继承字族与BaseOnly矩阵各一次()
    {
        var snapshot = Source(); using SkiaRenderPipeline renderer = new();
        var scene = renderer.GetSceneFrame(snapshot, 2_000_000); var token = Assert.Single(scene.TokenGeometries);
        var basic = token.Clusters.Where(item => item.Target.RubyId is null).OrderBy(item => item.Target.UnitIndex).ToArray();
        Assert.Equal(2, basic.Length);
        var familyMatrix = Fx.Affine2.Translation(3, 4); var baseMatrix = Fx.Affine2.ScaleAt(1.2, token.BaseGeometry.AdvanceBounds.Center);
        var clusterMatrix = Fx.Affine2.Translation(18, 9);
        var parent = new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition), scene, [new(token.WithRuby, familyMatrix)]);
        var prepared = parent.WithTextPartitions([new(token.BaseOnly, baseMatrix)], [], [new(basic[0].Target, clusterMatrix)]);
        var final = Assert.Single(prepared.FinalGeometry);
        var expectedBase = token.BaseGeometry.BaseWorldMatrix.Compose(familyMatrix).Compose(baseMatrix);
        Assert.Equal(expectedBase.Compose(clusterMatrix), final.Clusters.Single(item => item.Target == basic[0].Target).Geometry.BaseWorldMatrix);
        Assert.Equal(expectedBase, final.Clusters.Single(item => item.Target == basic[1].Target).Geometry.BaseWorldMatrix);
        Assert.All(final.Clusters.Where(item => item.Target.RubyId is not null), item =>
            Assert.Equal(token.BaseGeometry.BaseWorldMatrix.Compose(familyMatrix), item.Geometry.BaseWorldMatrix));
        using var plain = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true, PreparedFrame = parent });
        using var actual = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true, PreparedFrame = prepared });
        Assert.False(plain.PixelMemory.Span.SequenceEqual(actual.PixelMemory.Span));
    }

    [Fact]
    public void Ruby独立字簇改变实际像素而基础字不跟随()
    {
        var snapshot = Source(); using SkiaRenderPipeline renderer = new();
        var scene = renderer.GetSceneFrame(snapshot, 2_000_000); var token = Assert.Single(scene.TokenGeometries);
        var ruby = token.Clusters.First(item => item.Target.RubyId is not null);
        var prepared = Prepared(snapshot, scene).WithTextPartitions([], [], [new(ruby.Target, Fx.Affine2.Translation(20, -15))]);
        var final = Assert.Single(prepared.FinalGeometry);
        Assert.Equal(token.BaseGeometry.BaseWorldMatrix, final.BaseGeometry.BaseWorldMatrix);
        Assert.Equal(ruby.Geometry.BaseWorldMatrix.Compose(Fx.Affine2.Translation(20, -15)),
            final.Clusters.Single(item => item.Target == ruby.Target).Geometry.BaseWorldMatrix);
        using var plain = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true });
        using var actual = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true, PreparedFrame = prepared });
        Assert.False(plain.PixelMemory.Span.SequenceEqual(actual.PixelMemory.Span));
    }

    [Fact]
    public void 跨token连字公开完整所属范围且不能通过字簇目标绕过不可拆门禁()
    {
        var line = LyricLine.Create("line", "office", new(0, 6_000_000), TimingStatus.Imported,
            [.. "office".Select((character, index) => LyricToken.Create("t" + index, character.ToString(),
                new(index * 1_000_000, (index + 1) * 1_000_000), TimingStatus.Imported))]);
        var snapshot = new ProjectSnapshot { Name = "ligature", Lyrics = LyricsDocument.Create([line]),
            Composition = new() { CanvasWidth = 640, CanvasHeight = 360 },
            LyricStyle = LyricStyle.Default with { FontFamilyName = TestFonts.LigatureFfi, FontSizePx = 56 } };
        using SkiaRenderPipeline renderer = new(); var scene = renderer.GetSceneFrame(snapshot, 2_500_000);
        var shared = scene.TokenGeometries.SelectMany(item => item.Clusters).First(item => item.OwnerTokens.Length > 1);
        Assert.False(shared.Geometry.CanTransformIndependently);
        Assert.Equal("UnsupportedGlyphPartition", shared.Geometry.DiagnosticCode);
        var error = Assert.Throws<PreparedFrameException>(() => Prepared(snapshot, scene).WithTextPartitions([], [], [new(shared.Target, Fx.Affine2.Translation(1, 0))]));
        Assert.Equal("UnsupportedGlyphPartition", error.Code);
        Assert.Equal(new V2.TextTarget.Cluster(shared.Target), error.TextTarget);
        var owner = scene.TokenGeometries.First(token => !token.BaseGeometry.CanTransformIndependently);
        var baseError = Assert.Throws<PreparedFrameException>(() => Prepared(snapshot, scene).WithTextPartitions([], [new(owner.BaseOnly, new(opacityFactor: .5))], []));
        Assert.Equal("UnsupportedGlyphPartition", baseError.Code);
        Assert.Equal(new V2.TextTarget.Base(owner.BaseOnly), baseError.TextTarget);
        using var pixels = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true });
        Assert.True(Alpha(pixels) > 0);
    }

    [Fact]
    public void 基础字跨token连字不阻止独立Ruby字簇且连字仍只绘一次()
    {
        var line = LyricLine.Create("line", "office", new(0, 6_000_000), TimingStatus.Imported,
            [.. "office".Select((character, index) => LyricToken.Create("t" + index, character.ToString(),
                new(index * 1_000_000, (index + 1) * 1_000_000), TimingStatus.Imported))]) with
            { RubyAnnotations = [RubyAnnotation.Create("ruby", "t1", "xy")] };
        var snapshot = new ProjectSnapshot { Name = "ruby ligature", Lyrics = LyricsDocument.Create([line]),
            Composition = new() { CanvasWidth = 640, CanvasHeight = 360 },
            LyricStyle = LyricStyle.Default with { FontFamilyName = TestFonts.LigatureFfi, FontSizePx = 56, Stroke = null, Shadow = null, Glow = null,
                Opacity = .6f, UnsungColor = RgbaColor.White, SungColor = RgbaColor.White } };
        using SkiaRenderPipeline renderer = new(); var scene = renderer.GetSceneFrame(snapshot, 2_500_000);
        var owner = scene.TokenGeometries.Single(item => item.WithRuby.TokenId.Value == "t1");
        Assert.False(owner.BaseGeometry.CanTransformIndependently);
        var ruby = owner.Clusters.First(item => item.Target.RubyId is not null);
        Assert.True(ruby.Geometry.CanTransformIndependently);
        var prepared = Prepared(snapshot, scene).WithTextPartitions([], [], [new(ruby.Target, Fx.Affine2.Translation(15, -10))]);
        using var plain = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true });
        using var actual = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true, PreparedFrame = prepared });
        Assert.False(plain.PixelMemory.Span.SequenceEqual(actual.PixelMemory.Span));
        var corners = owner.BaseGeometry.WorldInkCorners;
        int top = Math.Max(0, (int)Math.Floor(corners.Min(item => item.Y)));
        int bottom = Math.Min(plain.Height, (int)Math.Ceiling(corners.Max(item => item.Y)));
        for (int y = top; y < bottom; y++)
            Assert.True(plain.PixelMemory.Span.Slice(y * plain.Stride, plain.Stride)
                .SequenceEqual(actual.PixelMemory.Span.Slice(y * actual.Stride, actual.Stride)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void 动态字族Glow及基础字字簇分区沿相同全域部件顺序绘制(int partition)
    {
        var line = LyricLine.Create("line", "AB", new(0, 5_000_000), TimingStatus.Confirmed,
            [LyricToken.Create("a", "A", new(1_000_000, 3_000_000), TimingStatus.Confirmed),
                LyricToken.Create("b", "B", new(1_000_000, 3_000_000), TimingStatus.Confirmed)]) with
            { RubyAnnotations = [RubyAnnotation.Create("ruby-a", "a", "aa"), RubyAnnotation.Create("ruby-b", "b", "bb")] };
        var snapshot = Source() with { Lyrics = LyricsDocument.Create([line]),
            LyricStyle = Source().LyricStyle with { Opacity = .65f, UnsungColor = new(255, 255, 255, 180), SungColor = new(255, 255, 255, 180) } };
        var glow = GlowSpec.Create(new(255, 0, 0, 210), .8f, 14, 3);
        var staticLine = line with { Tokens = [.. line.Tokens.Select(token => token with
            { StyleOverride = new() { Glow = GlowSpecOverride.FromSpec(glow) } })] };
        var expectedSnapshot = snapshot with { Lyrics = LyricsDocument.Create([staticLine]) };
        using SkiaRenderPipeline renderer = new();
        var scene = renderer.GetSceneFrame(snapshot, 2_000_000);
        var expectedScene = renderer.GetSceneFrame(expectedSnapshot, scene.TimeUs);
        Fx.GlowValue value = new(new(1, 0, 0, 210 / 255d), glow.Opacity, glow.BlurRadiusPx, glow.SpreadPx);
        var dynamicPrepared = new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition), scene, [],
            [.. scene.TokenGeometries.Select(token => new Fx.TokenFamilyStyle(token.WithRuby, TestStyle.Update(glow: new(value))))]);
        var expectedPrepared = Prepared(expectedSnapshot, expectedScene);
        var target = scene.TokenGeometries[0];
        if (partition == 1)
        {
            V2.BaseTokenTransform[] transforms = [new(target.BaseOnly, Fx.Affine2.Translation(5, 8))];
            dynamicPrepared = dynamicPrepared.WithTextPartitions([.. transforms], [], []);
            expectedPrepared = expectedPrepared.WithTextPartitions([.. transforms], [], []);
        }
        else if (partition == 2)
        {
            V2.TextClusterTransform[] transforms = [new(target.Clusters.First(item => item.Target.RubyId is null).Target, Fx.Affine2.Translation(5, 8))];
            dynamicPrepared = dynamicPrepared.WithTextPartitions([], [], [.. transforms]);
            expectedPrepared = expectedPrepared.WithTextPartitions([], [], [.. transforms]);
        }
        using var expected = renderer.Render(RenderRequest.At(expectedSnapshot, scene.TimeUs) with { TransparentBackground = true, PreparedFrame = expectedPrepared });
        using var actual = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true, PreparedFrame = dynamicPrepared });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
    }

    private static PreparedRenderFrame Prepared(ProjectSnapshot snapshot, SceneFrame scene) => new(snapshot, scene.TimeUs,
        Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), scene, []);

    private static ProjectSnapshot Source()
    {
        var line = LyricLine.Create("line", "AB", new(0, 5_000_000), TimingStatus.Confirmed,
            [LyricToken.Create("token", "AB", new(1_000_000, 3_000_000), TimingStatus.Confirmed)]) with
            { RubyAnnotations = [RubyAnnotation.Create("ruby", "token", "ab")] };
        return ProjectSnapshot.FromProject(Project.Create("partitions", Composition.Create(640, 360, Core.Time.FrameRate.Fps30, outputDurationUs: 5_000_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = TestFonts.LigatureFfi, FontSizePx = 64,
                Stroke = null, Shadow = null, Glow = null, UnsungColor = RgbaColor.White, SungColor = RgbaColor.White }));
    }
    private static long Alpha(RenderFrame pixels)
    {
        long total = 0; for (int offset = 3; offset < pixels.PixelMemory.Length; offset += 4) total += pixels.PixelMemory.Span[offset];
        return total;
    }
    private static void AssertRubyPixelsEqual(RenderFrame plain, RenderFrame actual, Fx.TokenGeometrySample token)
    {
        var corners = Assert.Single(token.Ruby).Geometry.WorldInkCorners;
        int left = Math.Max(0, (int)Math.Floor(corners.Min(item => item.X)) - 1), right = Math.Min(plain.Width, (int)Math.Ceiling(corners.Max(item => item.X)) + 1);
        int top = Math.Max(0, (int)Math.Floor(corners.Min(item => item.Y)) - 1), bottom = Math.Min(plain.Height, (int)Math.Ceiling(corners.Max(item => item.Y)) + 1);
        int visible = 0;
        for (int y = top; y < bottom; y++) for (int x = left; x < right; x++)
        {
            int offset = y * plain.Stride + x * 4;
            Assert.True(plain.PixelMemory.Span.Slice(offset, 4).SequenceEqual(actual.PixelMemory.Span.Slice(offset, 4)));
            if (plain.PixelMemory.Span[offset + 3] > 0) visible++;
        }
        Assert.True(visible > 0);
    }
}
