using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;
using V2 = MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class WarpPixelRenderingTests
{
    /// <summary>
    /// 注音字簇网格的定义域按注音比例缩放后的装饰校验，与实际绘制一致（P3 设计 §7.2、§10.6 第 1 条）：
    /// 比例 2 时只容纳未缩放装饰的定义域要被拒绝；两种比例下只容纳缩放后装饰的定义域都能通过，平移网格的注音分层不裁掉辉光。
    /// </summary>
    [Theory]
    [InlineData(.5)]
    [InlineData(2d)]
    public void 注音字簇网格定义域按注音比例缩放后的装饰校验(double scale)
    {
        var snapshot = TextSnapshot(singleToken: true, rubyScale: scale);
        // 小字号让放大后的注音整个留在画面里；辉光以扩张为主，比例 2 时实心延到字外 16 px，未缩放的框只到 11 px。
        snapshot = snapshot with { LyricStyle = snapshot.LyricStyle with { FontSizePx = 24, Glow = new GlowSpec { BlurRadiusPx = 1, SpreadPx = 8 } } };
        using SkiaRenderPipeline renderer = TestRenderer();
        var scene = renderer.GetSceneFrame(snapshot, TimeUs);
        var token = Assert.Single(scene.TokenGeometries);
        var ruby = Assert.Single(token.Clusters, static cluster => cluster.Target.RubyId is not null);

        var fitted = Domain(snapshot.LyricStyle.ScaleDecorations(scale));
        var prepared = Prepared(snapshot, scene).WithTextWarps([Binding(new V2.TextTarget.Cluster(ruby.Target), TranslationWarp(fitted, 3, 0))]);
        // 横向平移网格走分层绘制：注音分层按缩放后的装饰框建表面，辉光不被裁掉，注音在最上方，画面最上一行有色像素不变。
        using var plain = Render(renderer, snapshot, scene.TimeUs);
        using var warped = Render(renderer, snapshot, scene.TimeUs, prepared);
        Assert.NotEqual(plain.PixelMemory.ToArray(), warped.PixelMemory.ToArray());
        Assert.InRange(TopInkRow(warped), TopInkRow(plain) - 1, TopInkRow(plain) + 1);

        if (scale > 1d)
        {
            var unscaled = Domain(snapshot.LyricStyle);
            var error = Assert.Throws<PreparedFrameException>(() => Prepared(snapshot, scene)
                .WithTextWarps([Binding(new V2.TextTarget.Cluster(ruby.Target), TranslationWarp(unscaled, 3, 0))]));
            Assert.Equal("WarpOutsideDomain", error.Code);
        }

        static int TopInkRow(RenderFrame frame)
        {
            var pixels = frame.PixelMemory.ToArray();
            int row = Enumerable.Range(0, frame.Height).First(y => Enumerable.Range(0, frame.Width).Any(x => pixels[(y * frame.Width + x) * 4 + 3] != 0));
            Assert.True(row > 0, "注音的辉光碰到画面上沿，无法比较。");
            return row;
        }

        // 定义域恰好容纳本字簇的字面、基线与按样式外扩的装饰框，另留半像素。
        Fx.Rect2 Domain(LyricStyle style)
        {
            var geometry = ruby.Geometry;
            var bounds = Union(Union(geometry.AdvanceBounds, PreparedRenderFrame.WarpDecoratedBounds(geometry.InkBounds!.Value, style)),
                new(geometry.Baseline.X, geometry.Baseline.Y, geometry.Baseline.X, geometry.Baseline.Y));
            return new(bounds.Left - .5, bounds.Top - .5, bounds.Right + .5, bounds.Bottom + .5);
        }
    }
}
