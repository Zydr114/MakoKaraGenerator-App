using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using Fx = MakoKaraGenerator.Scripting;
using V2 = MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// CPU 文字网格的像素出口回归。
///
/// 这些测试只把 <see cref="GridWarp"/> 作为作者定义的输入，并以普通 affine
/// 或几何不变量作为参照；不从生产渲染器读取最终 mesh 当作 oracle。
/// </summary>
public sealed partial class WarpPixelRenderingTests
{
    private const long TimeUs = 1_500_000;

    [Fact]
    public void 恒等网格保持透明像素逐字节不变()
    {
        var snapshot = TextSnapshot();
        using SkiaRenderPipeline renderer = TestRenderer();
        var scene = renderer.GetSceneFrame(snapshot, TimeUs);
        var token = Assert.Single(scene.TokenGeometries, item => item.WithRuby.TokenId.Value == "a");
        var warp = IdentityWarp(token);
        var prepared = Prepared(snapshot, scene).WithTextWarps([Binding(new V2.TextTarget.Family(token.WithRuby), warp)]);

        using var plain = Render(renderer, snapshot, scene.TimeUs);
        using var actual = Render(renderer, snapshot, scene.TimeUs, prepared);

        Assert.Equal(plain.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        Assert.Equal(0, AlphaAt(actual, 0, 0));
        Assert.Equal(0, AlphaAt(actual, actual.Width - 1, actual.Height - 1));
    }

    [Fact]
    public void 字族平移网格与等效仿射像素接近且真实改变画面()
    {
        var snapshot = TextSnapshot();
        using SkiaRenderPipeline renderer = TestRenderer();
        var scene = renderer.GetSceneFrame(snapshot, TimeUs);
        var token = Assert.Single(scene.TokenGeometries, item => item.WithRuby.TokenId.Value == "a");
        const double dx = 17, dy = -9;

        var warped = Prepared(snapshot, scene).WithTextWarps([Binding(
            new V2.TextTarget.Family(token.WithRuby), TranslationWarp(DomainFor(token), dx, dy))]);
        var affine = Prepared(snapshot, scene, [new(token.WithRuby, Fx.Affine2.Translation(dx, dy))]);

        using var plainPixels = Render(renderer, snapshot, scene.TimeUs);
        using var warpedPixels = Render(renderer, snapshot, scene.TimeUs, warped);
        using var affinePixels = Render(renderer, snapshot, scene.TimeUs, affine);

        var fromPlain = Difference(plainPixels, affinePixels);
        var fromAffine = Difference(warpedPixels, affinePixels);
        Assert.True(fromPlain.DifferentPixels > 20, "参照仿射没有移动可见字族。");
        Assert.True(fromAffine.DifferentPixels <= Math.Max(48, fromPlain.DifferentPixels / 2),
            $"平移网格与等效仿射差异过大：{fromAffine.DifferentPixels}/{fromPlain.DifferentPixels} 像素。");
    }

    [Fact]
    public void 双色半唱字族平移网格在源坐标裁切且与等效仿射接近()
    {
        const long halfSungTimeUs = 1_000_000;
        var snapshot = TextSnapshot(singleToken: true);
        snapshot = snapshot with { LyricStyle = snapshot.LyricStyle with
            { SungColor = new(255, 0, 0), UnsungColor = new(0, 0, 255) } };
        using SkiaRenderPipeline renderer = TestRenderer();
        var scene = renderer.GetSceneFrame(snapshot, halfSungTimeUs);
        var token = Assert.Single(scene.TokenGeometries);
        const double dx = 19, dy = -7;
        var warped = Prepared(snapshot, scene).WithTextWarps([Binding(new V2.TextTarget.Family(token.WithRuby),
            TranslationWarp(DomainFor(token), dx, dy))]);
        var affine = Prepared(snapshot, scene, [new(token.WithRuby, Fx.Affine2.Translation(dx, dy))]);

        using var plainPixels = Render(renderer, snapshot, scene.TimeUs);
        using var warpedPixels = Render(renderer, snapshot, scene.TimeUs, warped);
        using var affinePixels = Render(renderer, snapshot, scene.TimeUs, affine);

        var fromPlain = Difference(plainPixels, affinePixels);
        var fromAffine = Difference(warpedPixels, affinePixels);
        Assert.True(fromPlain.DifferentPixels > 20, "参照仿射没有移动半唱字族。");
        Assert.True(fromAffine.DifferentPixels <= Math.Max(48, fromPlain.DifferentPixels / 2),
            $"半唱网格与等效仿射差异过大：{fromAffine.DifferentPixels}/{fromPlain.DifferentPixels} 像素。");
        foreach (int channel in new[] { 0, 2 })
        {
            long expectedColor = ColorSignal(affinePixels, channel);
            Assert.True(expectedColor > 10_000, $"参照半唱图缺少第 {channel} 通道的可见填充。");
            Assert.InRange(ColorSignal(warpedPixels, channel) / (double)expectedColor, .95, 1.05);
        }
    }

    [Fact]
    public void 基础字平移网格只移动基础字并保留Ruby父族像素()
    {
        var snapshot = TextSnapshot();
        using SkiaRenderPipeline renderer = TestRenderer();
        var scene = renderer.GetSceneFrame(snapshot, TimeUs);
        var token = Assert.Single(scene.TokenGeometries, item => item.WithRuby.TokenId.Value == "a");
        const double dx = 19, dy = 7;
        var prepared = Prepared(snapshot, scene).WithTextWarps([Binding(
            new V2.TextTarget.Base(token.BaseOnly), TranslationWarp(token.BaseGeometry, dx, dy))]);

        using var plain = Render(renderer, snapshot, scene.TimeUs);
        using var actual = Render(renderer, snapshot, scene.TimeUs, prepared);

        Assert.True(Difference(plain, actual).DifferentPixels > 20, "基础字网格没有产生实际像素变化。");
        AssertSamePixelsInGeometry(plain, actual, Assert.Single(token.Ruby).Geometry);
    }

    [Fact]
    public void 基础字簇平移网格与等效簇仿射接近且相邻簇不随动()
    {
        var snapshot = TextSnapshot();
        using SkiaRenderPipeline renderer = TestRenderer();
        var scene = renderer.GetSceneFrame(snapshot, TimeUs);
        var token = Assert.Single(scene.TokenGeometries, item => item.WithRuby.TokenId.Value == "a");
        var cluster = Assert.Single(token.Clusters, item => item.Target.RubyId is null);
        const double dx = -13, dy = -6;
        var warped = Prepared(snapshot, scene).WithTextWarps([Binding(
            new V2.TextTarget.Cluster(cluster.Target), TranslationWarp(cluster.Geometry, dx, dy))]);
        var affine = Prepared(snapshot, scene).WithTextPartitions([], [], [new(cluster.Target, Fx.Affine2.Translation(dx, dy))]);

        using var plain = Render(renderer, snapshot, scene.TimeUs);
        using var warpedPixels = Render(renderer, snapshot, scene.TimeUs, warped);
        using var affinePixels = Render(renderer, snapshot, scene.TimeUs, affine);

        Assert.True(Difference(plain, warpedPixels).DifferentPixels > 10, "基础字簇网格没有产生实际像素变化。");
        var fromPlain = Difference(plain, affinePixels);
        var fromAffine = Difference(warpedPixels, affinePixels);
        Assert.True(fromAffine.DifferentPixels <= Math.Max(48, fromPlain.DifferentPixels / 2),
            $"字簇平移网格与等效仿射差异过大：{fromAffine.DifferentPixels}/{fromPlain.DifferentPixels} 像素。");
        var untouchedToken = Assert.Single(scene.TokenGeometries, item => item.WithRuby.TokenId.Value == "b");
        var untouched = Assert.Single(untouchedToken.Clusters, item => item.Target.RubyId is null);
        AssertSamePixelsInGeometry(plain, warpedPixels, untouched.Geometry);
    }

    [Fact]
    public void Ruby字簇平移网格不移动基础字且实际改变像素()
    {
        var snapshot = TextSnapshot();
        using SkiaRenderPipeline renderer = TestRenderer();
        var scene = renderer.GetSceneFrame(snapshot, TimeUs);
        var token = Assert.Single(scene.TokenGeometries, item => item.WithRuby.TokenId.Value == "a");
        var ruby = Assert.Single(token.Clusters, item => item.Target.RubyId is not null);
        const double dx = -11, dy = -10;
        var prepared = Prepared(snapshot, scene).WithTextWarps([Binding(
            new V2.TextTarget.Cluster(ruby.Target), TranslationWarp(ruby.Geometry, dx, dy))]);

        using var plain = Render(renderer, snapshot, scene.TimeUs);
        using var actual = Render(renderer, snapshot, scene.TimeUs, prepared);

        Assert.True(Difference(plain, actual).DifferentPixels > 10, "Ruby 网格没有产生实际像素变化。");
        // Ruby 的原有墨迹延伸到 Base 上方两像素处；只核对真实 Base 区域，不能把被移动的 Ruby 边缘算入不变区。
        AssertSamePixelsInGeometry(plain, actual, token.BaseGeometry, padding: 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 基础字或字簇网格沿字族基础字字簇仿射链各应用一次且Ruby保持父族像素(bool warpBase)
    {
        var snapshot = PartitionSnapshot();
        using SkiaRenderPipeline renderer = TestRenderer();
        var scene = renderer.GetSceneFrame(snapshot, TimeUs);
        var token = Assert.Single(scene.TokenGeometries);
        var clusters = token.Clusters.Where(static item => item.Target.RubyId is null)
            .OrderBy(static item => item.Target.UnitIndex).ToArray();
        Assert.Equal(2, clusters.Length);
        var cluster = clusters[0];
        var familyMatrix = Fx.Affine2.Translation(9, -4);
        var baseMatrix = Fx.Affine2.Translation(5, 4)
            .Compose(Fx.Affine2.ScaleAt(1.08, token.BaseGeometry.AdvanceBounds.Center));
        var clusterMatrix = Fx.Affine2.Translation(4, 5);
        var parent = Prepared(snapshot, scene, [new(token.WithRuby, familyMatrix)])
            .WithTextPartitions([new(token.BaseOnly, baseMatrix)], [], [new(cluster.Target, clusterMatrix)]);
        var warpMatrix = Fx.Affine2.Translation(-15, 9);
        var domain = DomainFor(warpBase ? token.BaseGeometry : cluster.Geometry);
        if (warpBase)
            domain = Union(domain, BoundsOf(Corners(DomainFor(cluster.Geometry)).Select(clusterMatrix.Map)));
        var warp = TranslationWarp(domain, -15, 9);
        V2.TextTarget target = warpBase ? new V2.TextTarget.Base(token.BaseOnly) : new V2.TextTarget.Cluster(cluster.Target);
        var warped = parent.WithTextWarps([Binding(target, warp)]);
        var affine = Prepared(snapshot, scene, [new(token.WithRuby, familyMatrix)])
            .WithTextPartitions([new(token.BaseOnly, warpBase ? baseMatrix.Compose(warpMatrix) : baseMatrix)], [],
                [new(cluster.Target, warpBase ? clusterMatrix : clusterMatrix.Compose(warpMatrix))]);

        using var parentPixels = Render(renderer, snapshot, scene.TimeUs, parent);
        using var warpedPixels = Render(renderer, snapshot, scene.TimeUs, warped);
        using var affinePixels = Render(renderer, snapshot, scene.TimeUs, affine);

        var fromParent = Difference(parentPixels, affinePixels);
        var fromAffine = Difference(warpedPixels, affinePixels);
        Assert.True(fromParent.DifferentPixels > 20, "组合仿射参照没有移动所选基础字目标。");
        Assert.True(fromAffine.DifferentPixels <= Math.Max(48, fromParent.DifferentPixels / 2),
            $"组合仿射链中网格与等效仿射差异过大：{fromAffine.DifferentPixels}/{fromParent.DifferentPixels} 像素。");
        var parentToken = Assert.Single(parent.FinalGeometry);
        AssertSamePixelsInGeometry(parentPixels, warpedPixels, Assert.Single(parentToken.Ruby).Geometry);
        if (!warpBase)
            AssertSamePixelsInGeometry(parentPixels, warpedPixels,
                parentToken.Clusters.Single(item => item.Target == clusters[1].Target).Geometry);
        var point = cluster.Geometry.AdvanceBounds.Center;
        var localExpected = warpBase
            ? familyMatrix.Map(baseMatrix.Map(warp.MapPoint(clusterMatrix.Map(point))))
            : familyMatrix.Map(baseMatrix.Map(clusterMatrix.Map(warp.MapPoint(point))));
        var expectedAnchor = cluster.Geometry.BaseWorldMatrix.Map(localExpected);
        var actualAnchor = Assert.Single(warped.FinalGeometry).Clusters.Single(item => item.Target == cluster.Target)
            .Geometry.MapPoint(point);
        Assert.InRange(actualAnchor.X, expectedAnchor.X - .0001, expectedAnchor.X + .0001);
        Assert.InRange(actualAnchor.Y, expectedAnchor.Y - .0001, expectedAnchor.Y + .0001);
    }

    [Fact]
    public void 基础字跨token共享连字时Ruby独立字簇网格仍可画且连字像素只绘一次()
    {
        var line = LyricLine.Create("line", "office", new(0, 6_000_000), TimingStatus.Imported,
            [.. "office".Select((character, index) => LyricToken.Create("t" + index, character.ToString(),
                new(index * 1_000_000, (index + 1) * 1_000_000), TimingStatus.Imported))]) with
        {
            RubyAnnotations = [RubyAnnotation.Create("ruby", "t1", "xy")],
        };
        var snapshot = new ProjectSnapshot { Name = "ruby warp ligature", Lyrics = LyricsDocument.Create([line]),
            Composition = new() { CanvasWidth = 640, CanvasHeight = 360 },
            LyricStyle = LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 56,
                Stroke = null, Shadow = null, Glow = null, Opacity = .6f,
                UnsungColor = RgbaColor.White, SungColor = RgbaColor.White } };
        using SkiaRenderPipeline renderer = TestRenderer();
        var scene = renderer.GetSceneFrame(snapshot, 2_500_000);
        var owner = Assert.Single(scene.TokenGeometries, item => item.WithRuby.TokenId.Value == "t1");
        Assert.False(owner.BaseGeometry.CanTransformIndependently);
        var ruby = owner.Clusters.First(static item => item.Target.RubyId is not null);
        Assert.True(ruby.Geometry.CanTransformIndependently);
        var warped = Prepared(snapshot, scene).WithTextWarps([Binding(new V2.TextTarget.Cluster(ruby.Target),
            TranslationWarp(ruby.Geometry, 15, -10))]);
        var affine = Prepared(snapshot, scene)
            .WithTextPartitions([], [], [new(ruby.Target, Fx.Affine2.Translation(15, -10))]);

        using var plainPixels = Render(renderer, snapshot, scene.TimeUs);
        using var warpedPixels = Render(renderer, snapshot, scene.TimeUs, warped);
        using var affinePixels = Render(renderer, snapshot, scene.TimeUs, affine);

        var fromPlain = Difference(plainPixels, affinePixels);
        var fromAffine = Difference(warpedPixels, affinePixels);
        Assert.True(Difference(plainPixels, warpedPixels).DifferentPixels > 10, "独立 Ruby 字簇网格没有产生实际像素变化。");
        Assert.True(fromAffine.DifferentPixels <= Math.Max(48, fromPlain.DifferentPixels / 2),
            $"共享基础连字下 Ruby 网格与等效仿射差异过大：{fromAffine.DifferentPixels}/{fromPlain.DifferentPixels} 像素。");
        var baseCorners = Assert.Single(scene.LineGeometries).BaseGeometry.WorldInkCorners;
        Assert.NotEmpty(baseCorners);
        int top = Math.Max(0, (int)Math.Floor(baseCorners.Min(static point => point.Y)));
        int bottom = Math.Min(plainPixels.Height, (int)Math.Ceiling(baseCorners.Max(static point => point.Y)));
        for (int y = top; y < bottom; y++)
            Assert.True(plainPixels.PixelMemory.Span.Slice(y * plainPixels.Stride, plainPixels.Stride)
                .SequenceEqual(warpedPixels.PixelMemory.Span.Slice(y * warpedPixels.Stride, warpedPixels.Stride)),
                $"共享连字所在第 {y} 行不应被 Ruby-only 网格重绘或移动。");
    }

    [Fact]
    public void 非线性字族网格真实变形且中心锚点按网格映射()
    {
        var snapshot = TextSnapshot();
        using SkiaRenderPipeline renderer = TestRenderer();
        var scene = renderer.GetSceneFrame(snapshot, TimeUs);
        var token = Assert.Single(scene.TokenGeometries, item => item.WithRuby.TokenId.Value == "a");
        var warp = NonLinearWarp(token);
        var prepared = Prepared(snapshot, scene).WithTextWarps([Binding(new V2.TextTarget.Family(token.WithRuby), warp)]);
        var affine = Prepared(snapshot, scene, [new(token.WithRuby, Fx.Affine2.Translation(10, 7))]);

        using var plain = Render(renderer, snapshot, scene.TimeUs);
        using var actual = Render(renderer, snapshot, scene.TimeUs, prepared);
        using var affinePixels = Render(renderer, snapshot, scene.TimeUs, affine);

        var final = Assert.Single(prepared.FinalGeometry, item => item.WithRuby == token.WithRuby).FamilyGeometry;
        var sourceAnchor = warp.Domain.Center;
        var expectedAnchor = token.FamilyGeometry.BaseWorldMatrix.Map(warp.MapPoint(sourceAnchor));
        var actualAnchor = final.MapPoint(sourceAnchor);
        Assert.InRange(actualAnchor.X, expectedAnchor.X - 0.0001, expectedAnchor.X + 0.0001);
        Assert.InRange(actualAnchor.Y, expectedAnchor.Y - 0.0001, expectedAnchor.Y + 0.0001);
        Assert.NotEqual(token.FamilyGeometry.BaseWorldMatrix.Map(sourceAnchor), actualAnchor);
        Assert.True(Difference(plain, actual).DifferentPixels > 20, "非线性网格没有产生真实像素变形。");
        Assert.True(Difference(actual, affinePixels).DifferentPixels > 20, "非线性网格退化成了整体仿射。");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 字族平移网格保留阴影或辉光的完整外扩颜色(bool glow)
    {
        var snapshot = TextSnapshot(singleToken: true);
        var style = snapshot.LyricStyle with
        {
            Shadow = glow ? null : ShadowSpec.Create(new(255, 0, 0), angleDegrees: 36.86989765,
                distancePx: 10, blurRadiusPx: 6),
            Glow = glow ? GlowSpec.Create(new(0, 255, 0), blurRadiusPx: 6, spreadPx: 4) : null,
        };
        snapshot = snapshot with { LyricStyle = style };
        using SkiaRenderPipeline renderer = TestRenderer();
        var scene = renderer.GetSceneFrame(snapshot, TimeUs);
        var token = Assert.Single(scene.TokenGeometries);
        const double dx = -17, dy = -9;
        var warped = Prepared(snapshot, scene).WithTextWarps([Binding(new V2.TextTarget.Family(token.WithRuby),
            TranslationWarp(DomainFor(token, style), dx, dy))]);
        var affine = Prepared(snapshot, scene, [new(token.WithRuby, Fx.Affine2.Translation(dx, dy))]);

        using var plainPixels = Render(renderer, snapshot, scene.TimeUs);
        using var warpedPixels = Render(renderer, snapshot, scene.TimeUs, warped);
        using var affinePixels = Render(renderer, snapshot, scene.TimeUs, affine);

        var fromPlain = Difference(plainPixels, affinePixels);
        var fromAffine = Difference(warpedPixels, affinePixels);
        Assert.True(fromPlain.DifferentPixels > 20, "参照仿射没有移动带装饰的可见字族。");
        Assert.True(fromAffine.DifferentPixels <= Math.Max(48, fromPlain.DifferentPixels / 2),
            $"带装饰平移网格与等效仿射差异过大：{fromAffine.DifferentPixels}/{fromPlain.DifferentPixels} 像素。");
        long expectedDecoration = ColorSignal(affinePixels, glow ? 1 : 2);
        Assert.True(expectedDecoration > 10_000, "参照图没有产生可辨认的外扩装饰颜色。");
        Assert.InRange(ColorSignal(warpedPixels, glow ? 1 : 2) / (double)expectedDecoration, .95, 1.05);
        Assert.Equal(0, AlphaAt(warpedPixels, 0, 0));
    }

    [Fact]
    public void 父级倾斜旋转仿射下字族网格只应用一次世界矩阵()
    {
        var snapshot = TextSnapshot();
        using SkiaRenderPipeline renderer = TestRenderer();
        var scene = renderer.GetSceneFrame(snapshot, TimeUs);
        var token = Assert.Single(scene.TokenGeometries, item => item.WithRuby.TokenId.Value == "a");
        var pivot = token.FamilyGeometry.AdvanceBounds.Center;
        var parent = Fx.Affine2.Translation(pivot.X + 5, pivot.Y - 3)
            .Compose(new(.94, .18, -.12, .97, 0, 0))
            .Compose(Fx.Affine2.Translation(-pivot.X, -pivot.Y));
        var lineTarget = Assert.Single(scene.LineGeometries).Target;
        ImmutableArray<Fx.LineTransform> parents = [new(lineTarget, parent)];
        var parentOnly = Prepared(snapshot, scene, lineTransforms: parents);
        var warp = TranslationWarp(DomainFor(token), -13, -6);
        var warped = parentOnly.WithTextWarps([Binding(new V2.TextTarget.Family(token.WithRuby), warp)]);
        var affine = Prepared(snapshot, scene,
            [new(token.WithRuby, Fx.Affine2.Translation(-13, -6))], parents);

        using var parentPixels = Render(renderer, snapshot, scene.TimeUs, parentOnly);
        using var warpedPixels = Render(renderer, snapshot, scene.TimeUs, warped);
        using var affinePixels = Render(renderer, snapshot, scene.TimeUs, affine);

        var fromParent = Difference(parentPixels, affinePixels);
        var fromAffine = Difference(warpedPixels, affinePixels);
        Assert.True(fromParent.DifferentPixels > 20, "父级仿射下的参照局部平移没有移动可见字族。");
        Assert.True(fromAffine.DifferentPixels <= Math.Max(48, fromParent.DifferentPixels / 2),
            $"父级仿射下网格与等效局部仿射差异过大：{fromAffine.DifferentPixels}/{fromParent.DifferentPixels} 像素。");
        var parentToken = Assert.Single(parentOnly.HierarchyScene.TokenGeometries, item => item.WithRuby == token.WithRuby);
        var sourceAnchor = warp.Domain.Center;
        var expectedAnchor = parentToken.FamilyGeometry.BaseWorldMatrix.Map(warp.MapPoint(sourceAnchor));
        var actualAnchor = Assert.Single(warped.FinalGeometry, item => item.WithRuby == token.WithRuby)
            .FamilyGeometry.MapPoint(sourceAnchor);
        Assert.InRange(actualAnchor.X, expectedAnchor.X - .0001, expectedAnchor.X + .0001);
        Assert.InRange(actualAnchor.Y, expectedAnchor.Y - .0001, expectedAnchor.Y + .0001);
    }

    [Fact]
    public void 前奏标记网格平移实际改变透明像素()
    {
        var snapshot = ProjectSnapshot.FromProject(MakoKaraGenerator.TestSupport.LeadInFixture.Create());
        const long timeUs = 10_000_000;
        using SkiaRenderPipeline renderer = TestRenderer();
        var scene = renderer.GetSceneFrame(snapshot, timeUs);
        Assert.NotEmpty(scene.MarkGeometries);
        var mark = scene.MarkGeometries[0];
        var markStyle = scene.TextMarks.Single(item => item.NodeId == mark.Target.NodeId.Value
            && item.CueId == mark.Target.CueId.Value).Appearance.ToLyricStyle();
        var prepared = Prepared(snapshot, scene).WithTextWarps([Binding(
            new V2.TextTarget.Mark(mark.Target), TranslationWarp(DomainFor(mark.Geometry, markStyle), 14, 8))]);

        using var plain = Render(renderer, snapshot, timeUs);
        using var actual = Render(renderer, snapshot, timeUs, prepared);

        Assert.True(Difference(plain, actual).DifferentPixels > 10, "提示标记网格没有产生实际像素变化。");
        Assert.Equal(0, AlphaAt(actual, 0, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 标记网格与局部仿射保持一次合成且恒等网格像素不变(bool identity)
    {
        var snapshot = ProjectSnapshot.FromProject(MakoKaraGenerator.TestSupport.LeadInFixture.Create());
        const long time = 10_000_000;
        using var renderer = TestRenderer();
        var scene = renderer.GetSceneFrame(snapshot, time);
        var mark = scene.MarkGeometries[0];
        var style = scene.TextMarks.Single(item => item.NodeId == mark.Target.NodeId.Value
            && item.CueId == mark.Target.CueId.Value && item.Geometry.BindingId == mark.Target.BindingId.Value).Appearance.ToLyricStyle();
        var transform = Fx.Affine2.Translation(8, -5);
        var shift = identity ? Fx.Affine2.Identity : Fx.Affine2.Translation(14, 8);
        var frame = new PreparedRenderFrame(snapshot, time, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition), scene, [], [], [], [new(mark.Target, transform)])
            .WithTextWarps([Binding(new V2.TextTarget.Mark(mark.Target),
                TranslationWarp(DomainFor(mark.Geometry, style), shift.Tx, shift.Ty))]);
        var expectedFrame = new PreparedRenderFrame(snapshot, time, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition), scene, [], [], [], [new(mark.Target, transform.Compose(shift))]);
        using var actual = Render(renderer, snapshot, time, frame);
        using var expected = Render(renderer, snapshot, time, expectedFrame);
        if (identity) Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        else
        {
            using var plain = Render(renderer, snapshot, time);
            var moved = Difference(plain, expected);
            var difference = Difference(actual, expected);
            Assert.True(moved.DifferentPixels > 20);
            Assert.True(difference.DifferentPixels <= Math.Max(48, moved.DifferentPixels / 2),
                $"标记网格与仿射差异：{difference.DifferentPixels}/{moved.DifferentPixels}。");
        }
        var point = mark.Geometry.AdvanceBounds.Center;
        var mapped = frame.FinalMarkGeometry.Single(item => item.Target == mark.Target).Geometry.MapPoint(point);
        var expectedPoint = mark.Geometry.BaseWorldMatrix.Map(transform.Map(shift.Map(point)));
        Assert.InRange(mapped.X, expectedPoint.X - .0001, expectedPoint.X + .0001);
        Assert.InRange(mapped.Y, expectedPoint.Y - .0001, expectedPoint.Y + .0001);
    }

    [Fact]
    public void 非线性透明网格重复渲染逐字节确定()
    {
        var snapshot = TextSnapshot();
        using SkiaRenderPipeline renderer = TestRenderer();
        var scene = renderer.GetSceneFrame(snapshot, TimeUs);
        var token = Assert.Single(scene.TokenGeometries, item => item.WithRuby.TokenId.Value == "a");
        var prepared = Prepared(snapshot, scene).WithTextWarps([Binding(
            new V2.TextTarget.Family(token.WithRuby), NonLinearWarp(token))]);

        using var first = Render(renderer, snapshot, scene.TimeUs, prepared);
        using var second = Render(renderer, snapshot, scene.TimeUs, prepared);

        Assert.Equal(first.PixelMemory.ToArray(), second.PixelMemory.ToArray());
        Assert.Equal(0, AlphaAt(first, 0, 0));
        Assert.Equal(0, AlphaAt(first, first.Width - 1, first.Height - 1));
    }

    private static ProjectSnapshot TextSnapshot(bool singleToken = false)
    {
        ImmutableArray<LyricToken> tokens = [LyricToken.Create("a", "A", new(0, 2_000_000), TimingStatus.Confirmed)];
        if (!singleToken) tokens = tokens.Add(LyricToken.Create("b", "B", new(2_000_000, 4_000_000), TimingStatus.Confirmed));
        var line = LyricLine.Create("line", singleToken ? "A" : "AB", new(0, 4_000_000), TimingStatus.Confirmed,
            tokens) with
        {
            RubyAnnotations = [RubyAnnotation.Create("ruby", "a", "x")],
        };
        var project = Project.Create("warp pixels", Composition.Create(320, 180, FrameRate.Fps30, outputDurationUs: 4_000_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with
            {
                FontFamilyName = "DejaVu Sans", FontSizePx = 64,
                Stroke = null, Shadow = null, Glow = null,
                UnsungColor = RgbaColor.White, SungColor = RgbaColor.White, Opacity = 1,
            });
        return ProjectSnapshot.FromProject(project);
    }

    private static ProjectSnapshot PartitionSnapshot()
    {
        var line = LyricLine.Create("line", "AB", new(0, 4_000_000), TimingStatus.Confirmed,
            [LyricToken.Create("a", "AB", new(0, 4_000_000), TimingStatus.Confirmed)]) with
        {
            RubyAnnotations = [RubyAnnotation.Create("ruby", "a", "xy")],
        };
        return ProjectSnapshot.FromProject(Project.Create("warp partitions",
            Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 4_000_000), LyricsDocument.Create([line]),
            lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 64,
                Stroke = null, Shadow = null, Glow = null,
                UnsungColor = RgbaColor.White, SungColor = RgbaColor.White, Opacity = 1 }));
    }

    private static SkiaRenderPipeline TestRenderer()
    {
        var device = SkiaRenderDeviceFactory.Create(SkiaRenderBackendOptions.Parse(
            Environment.GetEnvironmentVariable("MAKO_WARP_TEST_BACKEND")));
        return new SkiaRenderPipeline(null, null, null, null, false, device, true);
    }

    private static PreparedRenderFrame Prepared(ProjectSnapshot snapshot, SceneFrame scene,
        ImmutableArray<Fx.TokenFamilyTransform> familyTransforms = default,
        ImmutableArray<Fx.LineTransform> lineTransforms = default)
        => new(snapshot, scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), scene,
            familyTransforms.IsDefault ? [] : familyTransforms, [], [], lineTransforms: lineTransforms);

    private static RenderFrame Render(SkiaRenderPipeline renderer, ProjectSnapshot snapshot, long timeUs,
        PreparedRenderFrame? prepared = null)
        => renderer.Render(RenderRequest.At(snapshot, timeUs) with { TransparentBackground = true, PreparedFrame = prepared });

    private static PreparedTextWarpBinding Binding(V2.TextTarget target, Fx.GridWarp warp)
        => new(new Fx.EffectInstanceId("warp-pixel-test"), "warp-pixel-test-artifact", null, target, warp);

    private static Fx.GridWarp IdentityWarp(Fx.TokenGeometrySample token)
    {
        var domain = DomainFor(token);
        return new(domain, 1, 1, Corners(domain));
    }

    private static Fx.GridWarp TranslationWarp(Fx.GeometrySample geometry, double dx, double dy)
        => TranslationWarp(DomainFor(geometry), dx, dy);

    private static Fx.GridWarp TranslationWarp(Fx.Rect2 domain, double dx, double dy)
        => new(domain, 1, 1, [.. Corners(domain).Select(point => new Fx.Point2(point.X + dx, point.Y + dy))]);

    private static Fx.GridWarp NonLinearWarp(Fx.TokenGeometrySample token)
    {
        var domain = DomainFor(token);
        ImmutableArray<Fx.Point2>.Builder vertices = ImmutableArray.CreateBuilder<Fx.Point2>(9);
        for (int row = 0; row <= 2; row++)
        for (int column = 0; column <= 2; column++)
        {
            var point = new Fx.Point2(domain.Left + domain.Width * column / 2d, domain.Top + domain.Height * row / 2d);
            if (row == 1 && column == 1) point = new(point.X + 20, point.Y + 14);
            vertices.Add(point);
        }
        return new(domain, 2, 2, vertices.MoveToImmutable());
    }

    private static Fx.Rect2 DomainFor(Fx.TokenGeometrySample token, LyricStyle? style = null)
        // FamilyGeometry 的 Advance 只属于基础字；Ruby 的 ascent/descent、基线和子簇也必须在域内。
        => token.Ruby.Select(static ruby => ruby.Geometry)
            .Concat(token.Clusters.Select(static cluster => cluster.Geometry))
            .Append(token.BaseGeometry).Append(token.FamilyGeometry)
            .Select(geometry => DomainFor(geometry, style)).Aggregate(Union);

    private static Fx.Rect2 DomainFor(Fx.GeometrySample geometry, LyricStyle? style = null)
    {
        var bounds = geometry.AdvanceBounds;
        if (geometry.InkBounds is { } ink)
            bounds = Union(bounds, ink);
        bounds = Union(bounds, new(geometry.Baseline.X, geometry.Baseline.Y, geometry.Baseline.X, geometry.Baseline.Y));
        // 作者测试域保守容纳全部装饰，避免以缺少定义域掩盖实际像素出口。
        double padding = 2;
        if (style?.Stroke is { IsEnabled: true } stroke) padding += stroke.WidthPx;
        if (style?.Shadow is { IsEnabled: true } shadow)
            padding += Math.Abs(shadow.OffsetXPx) + Math.Abs(shadow.OffsetYPx) + 3d * shadow.BlurRadiusPx;
        if (style?.Glow is { IsEnabled: true } glow) padding += glow.SpreadPx + 3d * glow.BlurRadiusPx;
        return new(bounds.Left - padding, bounds.Top - padding, bounds.Right + padding, bounds.Bottom + padding);
    }

    private static Fx.Rect2 Union(Fx.Rect2 first, Fx.Rect2 second)
        => new(Math.Min(first.Left, second.Left), Math.Min(first.Top, second.Top),
            Math.Max(first.Right, second.Right), Math.Max(first.Bottom, second.Bottom));

    private static Fx.Rect2 BoundsOf(IEnumerable<Fx.Point2> points)
    {
        var vertices = points.ToArray();
        return new(vertices.Min(static point => point.X), vertices.Min(static point => point.Y),
            vertices.Max(static point => point.X), vertices.Max(static point => point.Y));
    }

    private static ImmutableArray<Fx.Point2> Corners(Fx.Rect2 rectangle)
        // GridWarp vertices are row-major: TL/TR, then BL/BR.
        => [new(rectangle.Left, rectangle.Top), new(rectangle.Right, rectangle.Top),
            new(rectangle.Left, rectangle.Bottom), new(rectangle.Right, rectangle.Bottom)];

    private static void AssertSamePixelsInGeometry(RenderFrame expected, RenderFrame actual, Fx.GeometrySample geometry, int padding = 2)
    {
        var corners = geometry.WorldInkCorners;
        Assert.NotEmpty(corners);
        int left = Math.Max(0, (int)Math.Floor(corners.Min(point => point.X)) - padding);
        int right = Math.Min(expected.Width, (int)Math.Ceiling(corners.Max(point => point.X)) + padding);
        int top = Math.Max(0, (int)Math.Floor(corners.Min(point => point.Y)) - padding);
        int bottom = Math.Min(expected.Height, (int)Math.Ceiling(corners.Max(point => point.Y)) + padding);
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
        {
            int offset = y * expected.Stride + x * 4;
            Assert.True(expected.PixelMemory.Span.Slice(offset, 4).SequenceEqual(actual.PixelMemory.Span.Slice(offset, 4)),
                $"像素 ({x},{y}) 不应被其它目标的网格改变。ink={geometry.InkBounds}; world={geometry.BaseWorldMatrix}; expected={Convert.ToHexString(expected.PixelMemory.Span.Slice(offset, 4))}; actual={Convert.ToHexString(actual.PixelMemory.Span.Slice(offset, 4))}");
        }
    }

    private static byte AlphaAt(RenderFrame frame, int x, int y)
        => frame.PixelMemory.Span[y * frame.Stride + x * 4 + 3];

    private static long ColorSignal(RenderFrame frame, int channel)
    {
        long signal = 0;
        var pixels = frame.PixelMemory.Span;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            int component = pixels[offset + channel];
            int other = Math.Max(pixels[offset + (channel + 1) % 3], pixels[offset + (channel + 2) % 3]);
            signal += Math.Max(0, component - other);
        }
        return signal;
    }

    private static PixelDifference Difference(RenderFrame first, RenderFrame second)
    {
        Assert.Equal(first.PixelMemory.Length, second.PixelMemory.Length);
        int different = 0, maximum = 0;
        long total = 0;
        var a = first.PixelMemory.Span; var b = second.PixelMemory.Span;
        for (int offset = 0; offset < a.Length; offset += 4)
        {
            int pixelMaximum = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                int delta = Math.Abs(a[offset + channel] - b[offset + channel]);
                pixelMaximum = Math.Max(pixelMaximum, delta); total += delta;
            }
            if (pixelMaximum > 4) different++;
            maximum = Math.Max(maximum, pixelMaximum);
        }
        return new(different, total, maximum);
    }

    private readonly record struct PixelDifference(int DifferentPixels, long TotalAbs, int MaximumChannel);
}
