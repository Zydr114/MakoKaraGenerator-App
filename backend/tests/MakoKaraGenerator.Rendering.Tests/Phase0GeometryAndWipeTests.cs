using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>Phase 0：A1 钳位墨迹扫字与注音居中、无时间空白/标点无缝桥接、B1 多拍注音字分段同步像素与几何测试。</summary>
public sealed class Phase0GeometryAndWipeTests
{
    private static ProjectSnapshot MakeSnapshot(LyricLine line, LyricStyle? style = null, int width = 640, int height = 240) =>
        ProjectSnapshot.FromProject(Project.Create(
            "phase0",
            Composition.Create(width, height, FrameRate.Fps30, outputDurationUs: 5_000_000),
            LyricsDocument.Create([line]),
            lyricStyle: style ?? LyricStyle.Default with
            {
                FontFamilyName = TestFonts.Cjk,
                FontSizePx = 56,
                UnsungColor = new(255, 0, 0),
                SungColor = new(0, 0, 255),
                Stroke = null,
                Shadow = null,
                Glow = null,
            }));

    [Fact]
    public void A1_大字距与斜体下ProgressPath钳位至非空白墨迹且与逐字换色像素边界一致()
    {
        LyricLine line = LyricLine.Create(
            "l1", "AB", new(0, 2_000_000), TimingStatus.Confirmed,
            [
                LyricToken.Create("a", "A", new(0, 1_000_000), TimingStatus.Confirmed),
                LyricToken.Create("b", "B", new(1_000_000, 2_000_000), TimingStatus.Confirmed),
            ]);
        LyricStyle style = LyricStyle.Default with
        {
            FontFamilyName = TestFonts.LigatureFfi,
            FontSizePx = 64,
            LetterSpacingPx = 30,
            UnsungColor = new(255, 255, 255),
            SungColor = new(255, 0, 0),
            Stroke = null,
            Shadow = null,
            Glow = null,
        };
        ProjectSnapshot snapshot = MakeSnapshot(line, style);

        using SkiaRenderPipeline renderer = new();
        SceneFrame scene = renderer.GetSceneFrame(snapshot, 500_000);
        Fx.TokenGeometrySample tokenA = scene.TokenGeometries[0];

        Assert.NotNull(tokenA.ProgressPath);
        // 安全钳位在 Advance 之内，且排除了 30px 的大字距空白。
        Assert.True(tokenA.ProgressPath!.StartX >= tokenA.BaseGeometry.AdvanceBounds.Left - .001);
        Assert.True(tokenA.ProgressPath.EndX <= tokenA.BaseGeometry.AdvanceBounds.Right + .001);
        Assert.True(tokenA.BaseGeometry.AdvanceBounds.Right - tokenA.ProgressPath.EndX >= 20d);

        // 斜体字形墨迹越界时同样安全钳位在 Advance 区间内，不破坏局部字簇匹配。
        SceneFrame italicScene = renderer.GetSceneFrame(MakeSnapshot(line, style with { IsItalic = true }), 500_000);
        Fx.TokenGeometrySample italicA = italicScene.TokenGeometries[0];
        Assert.True(italicA.ProgressPath!.StartX >= italicA.BaseGeometry.AdvanceBounds.Left - .001);
        Assert.True(italicA.ProgressPath.EndX <= italicA.BaseGeometry.AdvanceBounds.Right + .001);

        PreparedRenderFrame prepared = new(snapshot, 500_000, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition), scene, []);
        Fx.PlaybackCursor cursor = Assert.Single(prepared.Playback.GetCursors(Fx.CursorAnchorMode.Baseline));

        using RenderFrame frame = renderer.Render(RenderRequest.At(snapshot, 500_000) with { TransparentBackground = true, PreparedFrame = prepared });
        int red = 0, white = 0;
        for (int y = 0; y < frame.Height; y++)
        for (int x = 0; x < frame.Width; x++)
        {
            int o = y * frame.Stride + x * 4;
            var px = frame.PixelMemory.Span;
            if (px[o + 3] < 200) continue;
            if (px[o + 2] > 180 && px[o + 1] < 10 && px[o] < 10)
            {
                red++;
                Assert.True(x < cursor.Position.X + 1.5);
            }
            else if (px[o] > 180 && px[o + 1] > 180 && px[o + 2] > 180)
            {
                white++;
                Assert.True(x >= Math.Floor(cursor.Position.X) - 1.5);
            }
        }
        Assert.True(red > 20 && white > 20);
    }

    [Fact]
    public void A1_注音按基础字非空白墨迹居中不被旧工程尾随空格右偏()
    {
        LyricLine trailingSpaceLine = LyricLine.Create(
            "l1", "愛   ", new(0, 1_000_000), TimingStatus.Confirmed,
            [LyricToken.Create("t1", "愛   ", new(0, 1_000_000), TimingStatus.Confirmed)]) with
        {
            RubyAnnotations = [RubyAnnotation.Create("r1", "t1", "あい")],
        };
        ProjectSnapshot snapshot = MakeSnapshot(trailingSpaceLine);

        using SkiaRenderPipeline renderer = new();
        SceneFrame scene = renderer.GetSceneFrame(snapshot, 0);
        Fx.TokenGeometrySample token = Assert.Single(scene.TokenGeometries);
        Fx.Rect2 baseInk = token.BaseGeometry.InkBounds!.Value;
        Fx.Rect2 rubyInk = Assert.Single(token.Ruby).Geometry.InkBounds!.Value;

        double baseCenter = (baseInk.Left + baseInk.Right) / 2d;
        double rubyCenter = (rubyInk.Left + rubyInk.Right) / 2d;
        Assert.InRange(Math.Abs(rubyCenter - baseCenter), 0, 1.5);
    }

    [Fact]
    public void 空格与前导尾随标点在两态外辉光下无未唱竖缝且不误染未定时实义字()
    {
        // 两态辉光：未唱纯红辉光，已唱纯蓝辉光；确保空格处无红色竖缝。
        LyricStyle glowStyle = LyricStyle.Default with
        {
            FontFamilyName = TestFonts.Cjk,
            FontSizePx = 56,
            UnsungColor = new(255, 0, 0),
            SungColor = new(0, 0, 255),
            Stroke = null,
            Shadow = null,
            Glow = GlowSpec.Create(new(255, 0, 0), opacity: 1f, blurRadiusPx: 14f, spreadPx: 6f),
            Sung = LyricStyle.Default.Sung with
            {
                Fill = new FillSpec { Material = new MaterialSpec.Solid(new(0, 0, 255)) },
                Glow = Followable.Set<GlowSpec?>(GlowSpec.Create(new(0, 0, 255), opacity: 1f, blurRadiusPx: 14f, spreadPx: 6f)),
            },
        };

        LyricLine line = LyricLine.Create(
            "l1", "「A B」", new(0, 3_000_000), TimingStatus.Confirmed,
            [
                LyricToken.Create("p0", "「"),
                LyricToken.Create("t1", "A", new(500_000, 1_500_000), TimingStatus.Confirmed),
                LyricToken.Create("sp", " "),
                LyricToken.Create("t2", "B", new(1_500_000, 2_500_000), TimingStatus.Confirmed),
                LyricToken.Create("p1", "」"),
            ]);
        ProjectSnapshot snapshot = MakeSnapshot(line, glowStyle);

        using SkiaRenderPipeline renderer = new();
        // t = 1_500_000：「A」已唱完，空格「 」应已跟亮为蓝色；「「」已随 A 起点跟亮为蓝色。
        SceneFrame scene = renderer.GetSceneFrame(snapshot, 1_500_000);
        Fx.Rect2 spaceBox = scene.TokenGeometries[2].BaseGeometry.AdvanceBounds;
        Fx.Rect2 openQuoteInk = scene.TokenGeometries[0].BaseGeometry.InkBounds!.Value;

        using RenderFrame mid = renderer.Render(RenderRequest.At(snapshot, 1_500_000) with { TransparentBackground = true });
        int blueInOpenQuote = 0, redInOpenQuote = 0;
        for (int y = (int)Math.Floor(openQuoteInk.Top); y <= (int)Math.Ceiling(openQuoteInk.Bottom); y++)
        for (int x = (int)Math.Floor(openQuoteInk.Left); x <= (int)Math.Ceiling(openQuoteInk.Right); x++)
        {
            int o = y * mid.Stride + x * 4;
            var px = mid.PixelMemory.Span;
            if (px[o + 3] < 120) continue;
            if (px[o] > px[o + 2] + 50) blueInOpenQuote++;
            if (px[o + 2] > px[o] + 50) redInOpenQuote++;
        }
        Assert.True(blueInOpenQuote > 20, "行首前导无时间标点「在首字开唱后应跟亮。");
        Assert.Equal(0, redInOpenQuote);

        // t = 2_600_000：整行全部唱完后（含尾随 」 与中间空格），整帧不应残留任何未唱红色像素（零辉光竖缝）。
        using RenderFrame done = renderer.Render(RenderRequest.At(snapshot, 2_600_000) with { TransparentBackground = true });
        int redAnywhere = 0, blueInSpace = 0;
        for (int y = 0; y < done.Height; y++)
        for (int x = 0; x < done.Width; x++)
        {
            int o = y * done.Stride + x * 4;
            var px = done.PixelMemory.Span;
            if (px[o + 3] < 20) continue;
            if (px[o + 2] > px[o] + 15) redAnywhere++;
            if (x >= spaceBox.Left && x <= spaceBox.Right && px[o] > 40) blueInSpace++;
        }
        Assert.Equal(0, redAnywhere);
        Assert.True(blueInSpace > 10, "空格处应由已唱蓝辉光无缝填满，无竖缝。");

        // 红线测试：夹在两个已定时实义字之间的未定时实义字必须保持未唱红色，绝不被桥接误染。
        LyricLine untimedMiddle = LyricLine.Create(
            "l2", "歌 词 曲", new(0, 4_000_000), TimingStatus.Confirmed,
            [
                LyricToken.Create("t1", "歌", new(0, 1_000_000), TimingStatus.Confirmed),
                LyricToken.Create("s1", " "),
                LyricToken.Create("t2", "词"), // 未定时实义字！
                LyricToken.Create("s2", " "),
                LyricToken.Create("t3", "曲", new(2_000_000, 3_000_000), TimingStatus.Confirmed),
            ]);
        ProjectSnapshot redLineSnap = MakeSnapshot(untimedMiddle);
        SceneFrame redLineScene = renderer.GetSceneFrame(redLineSnap, 3_500_000);
        Fx.Rect2 untimedWordInk = redLineScene.TokenGeometries[2].BaseGeometry.InkBounds!.Value;
        using RenderFrame redLineFrame = renderer.Render(RenderRequest.At(redLineSnap, 3_500_000) with { TransparentBackground = true });
        int redInUntimedWord = 0, blueInUntimedWord = 0;
        for (int y = (int)Math.Floor(untimedWordInk.Top); y <= (int)Math.Ceiling(untimedWordInk.Bottom); y++)
        for (int x = (int)Math.Floor(untimedWordInk.Left); x <= (int)Math.Ceiling(untimedWordInk.Right); x++)
        {
            int o = y * redLineFrame.Stride + x * 4;
            var px = redLineFrame.PixelMemory.Span;
            if (px[o + 3] < 150) continue;
            if (px[o + 2] > 180 && px[o] < 20) redInUntimedWord++;
            if (px[o] > 180 && px[o + 2] < 20) blueInUntimedWord++;
        }
        Assert.True(redInUntimedWord > 30, "夹在两已定时字之间的未定时实义字必须保持未唱色。");
        Assert.Equal(0, blueInUntimedWord);
    }

    [Fact]
    public void 独立元素样式覆写路径下无时间标点同样跟亮()
    {
        // 给尾随标点「！」设置元素级覆盖颜色，触发 DrawChangedFamilies 独立元素绘制路径。
        LyricToken punct = LyricToken.Create("p1", "！") with
        {
            StyleOverride = new LyricStyleOverride
            {
                UnsungColor = new RgbaColor(255, 0, 0),
                SungColor = new RgbaColor(0, 255, 0),
            },
        };
        LyricLine line = LyricLine.Create(
            "l1", "歌！", new(0, 2_000_000), TimingStatus.Confirmed,
            [
                LyricToken.Create("t1", "歌", new(0, 1_000_000), TimingStatus.Confirmed),
                punct,
            ]);
        ProjectSnapshot snapshot = MakeSnapshot(line);

        using SkiaRenderPipeline renderer = new();
        SceneFrame scene = renderer.GetSceneFrame(snapshot, 1_500_000);
        Fx.Rect2 punctInk = scene.TokenGeometries[1].BaseGeometry.InkBounds!.Value;
        using RenderFrame frame = renderer.Render(RenderRequest.At(snapshot, 1_500_000) with { TransparentBackground = true });

        int green = 0, red = 0;
        for (int y = (int)Math.Floor(punctInk.Top); y <= (int)Math.Ceiling(punctInk.Bottom); y++)
        for (int x = (int)Math.Floor(punctInk.Left); x <= (int)Math.Ceiling(punctInk.Right); x++)
        {
            int o = y * frame.Stride + x * 4;
            var px = frame.PixelMemory.Span;
            if (px[o + 3] < 150) continue;
            if (px[o + 1] > 180 && px[o + 2] < 20) green++;
            if (px[o + 2] > 180 && px[o + 1] < 20) red++;
        }
        Assert.True(green > 15, "独立绘制路径下带样式覆写的无时间标点在前字唱完后应跟亮。");
        Assert.Equal(0, red);
    }

    [Fact]
    public void B1_多拍注音字按字素权重分段同步且羽化与扫字高亮不退化()
    {
        // 「█」（实心方块 100px）绑定两拍注音：「きょ」(2 字素, 1.0s..1.2s) + 「う」(1 字素, 1.2s..2.0s)。
        // 第 1 拍占 2/3 宽度，在 t=1.2s 时基础字已扫到 2/3（而非匀速的 0.2）。
        RubyAnnotation ruby = RubyAnnotation.Create("r1", "t1", "きょう") with
        {
            Segments =
            [
                RubyTextSegment.Create("s1", "きょ", new(1_000_000, 1_200_000), TimingStatus.Confirmed),
                RubyTextSegment.Create("s2", "う", new(1_200_000, 2_000_000), TimingStatus.Confirmed),
            ],
        };
        LyricLine line = LyricLine.Create(
            "l1", "█", new(0, 3_000_000), TimingStatus.Confirmed,
            [LyricToken.Create("t1", "█", new(1_000_000, 2_000_000), TimingStatus.Confirmed)]) with
        {
            RubyAnnotations = [ruby],
        };

        LyricFrameSample atBeat1Mid = LyricSampler.SampleLine(line, 1_100_000);
        Assert.Equal(1d / 3d, atBeat1Mid.Tokens[0].Progress, 10);
        Assert.NotNull(atBeat1Mid.Tokens[0].ActiveSegment);

        LyricFrameSample atBeat1End = LyricSampler.SampleLine(line, 1_200_000);
        Assert.Equal(2d / 3d, atBeat1End.Tokens[0].Progress, 10);

        LyricFrameSample atBeat2Mid = LyricSampler.SampleLine(line, 1_600_000);
        Assert.Equal(5d / 6d, atBeat2Mid.Tokens[0].Progress, 10);

        // 验证羽化过渡带在多拍字上真正产生中间过渡像素（未退化为硬切换），且扫字高亮生效。
        LyricStyle hardStyle = LyricStyle.Default with
        {
            FontFamilyName = TestFonts.Cjk,
            FontSizePx = 100,
            UnsungColor = new(255, 0, 0),
            SungColor = new(0, 0, 255),
            Stroke = null,
            Shadow = null,
            Glow = null,
            Wipe = new WipeSpec { FeatherRatio = 0d },
        };
        LyricStyle featheredHighlightStyle = hardStyle with
        {
            Wipe = new WipeSpec
            {
                FeatherRatio = .4d,
                Highlight = new WipeHighlightSpec
                {
                    Color = new RgbaColor(0, 255, 0),
                    WidthRatio = .3d,
                    Opacity = .9f,
                    Blend = WipeHighlightBlend.Normal,
                },
            },
        };

        using SkiaRenderPipeline renderer = new();
        ProjectSnapshot hardSnap = MakeSnapshot(line, hardStyle);
        ProjectSnapshot featherSnap = MakeSnapshot(line, featheredHighlightStyle);
        using RenderFrame hardFrame = renderer.Render(RenderRequest.At(hardSnap, 1_100_000) with { TransparentBackground = true });
        using RenderFrame featherFrame = renderer.Render(RenderRequest.At(featherSnap, 1_100_000) with { TransparentBackground = true });

        Assert.False(hardFrame.PixelMemory.Span.SequenceEqual(featherFrame.PixelMemory.Span));
        // 在方块中线附近检查存在羽化混合像素及绿色高亮分量。
        int blendedPixels = 0, highlightedPixels = 0;
        SceneFrame scene = renderer.GetSceneFrame(featherSnap, 1_100_000);
        Fx.Rect2 ink = scene.TokenGeometries[0].BaseGeometry.InkBounds!.Value;
        int midY = (int)Math.Round((ink.Top + ink.Bottom) / 2d);
        for (int x = (int)Math.Floor(ink.Left); x < (int)Math.Ceiling(ink.Right); x++)
        {
            int o = midY * featherFrame.Stride + x * 4;
            var px = featherFrame.PixelMemory.Span;
            if (px[o + 3] < 240) continue;
            if (px[o] > 30 && px[o + 2] > 30) blendedPixels++;
            if (px[o + 1] > 40) highlightedPixels++;
        }
        Assert.True(blendedPixels > 5, "多拍注音字应呈现平滑羽化过渡带而非退化为硬切换。");
        Assert.True(highlightedPixels > 5, "多拍注音字扫字高亮峰应正常叠加。");
    }
}
