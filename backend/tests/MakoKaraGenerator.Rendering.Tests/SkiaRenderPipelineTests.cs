using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Text;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// Skia 渲染管线的端到端测试（roadmap RENDER-02）。
/// </summary>
/// <remarks>
/// <para>
/// 断言的是<b>像素</b>而不是中间结构：这一层是"预览与最终输出得到一致视觉结果"的
/// 唯一可验证位置（阶段 4 出口条件）。
/// </para>
/// <para>
/// 测试刻意用自定义样式与纯色，使颜色判定不依赖界面主题；画布用 640×360 而不是
/// 1920×1080，纯粹为了跑得快，几何规则与尺寸无关。
/// </para>
/// </remarks>
public sealed class SkiaRenderPipelineTests : IDisposable
{
    private const int CanvasWidth = 640;
    private const int CanvasHeight = 360;
    private const float FontSize = 80f;
    private const string LineText = "夜空中最亮的星";

    private static readonly RgbaColor Background = new(0x10, 0x10, 0x14);
    private static readonly RgbaColor Unsung = new(0x40, 0x40, 0x48);
    private static readonly RgbaColor Sung = new(0xE0, 0x20, 0x20);

    private readonly SkiaTextLayoutEngine _layoutEngine = new();
    private readonly SkiaRenderPipeline _pipeline;

    /// <summary>创建测试用的渲染管线。</summary>
    public SkiaRenderPipelineTests()
    {
        _pipeline = new SkiaRenderPipeline(_layoutEngine);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _pipeline.Dispose();
    }

    [Fact]
    public void 帧尺寸与格式跟随渲染目标()
    {
        RenderRequest request = CreateRequest(Us(1_000_000)) with
        {
            Target = new RenderTarget { PixelWidth = 320, PixelHeight = 180 },
        };

        using RenderFrame frame = _pipeline.Render(request);

        Assert.Equal(320, frame.Width);
        Assert.Equal(180, frame.Height);
        Assert.Equal(320 * 4, frame.Stride);
        Assert.Equal(PixelFormat.Bgra8888Premultiplied, frame.Format);
        Assert.Equal(1_000_000, frame.TimeUs);
        Assert.True(frame.IsPooled);
    }

    [Fact]
    public void 默认按合成画布尺寸输出()
    {
        using RenderFrame frame = _pipeline.Render(CreateRequest(Us(1_000_000)));

        Assert.Equal(CanvasWidth, frame.Width);
        Assert.Equal(CanvasHeight, frame.Height);
    }

    [Fact]
    public void 背景色来自合成设置()
    {
        using RenderFrame frame = _pipeline.Render(CreateRequest(Us(0)));

        Assert.True(frame.TryGetPixel(0, 0, out RgbaColor corner));
        Assert.Equal(Background, corner);
        Assert.True(frame.TryGetPixel(CanvasWidth - 1, CanvasHeight - 1, out RgbaColor opposite));
        Assert.Equal(Background, opposite);
    }

    [Fact]
    public void 没有可显示行时只有一个纯色背景()
    {
        // 时刻在首行开始之前：画面应当是干净的背景，没有任何文字像素。
        using RenderFrame frame = _pipeline.Render(CreateRequest(Us(500)));

        Assert.Equal(0, CountPixelsDifferentFrom(frame, Background, tolerance: 8));
    }

    [Fact]
    public void 行文本会被画到画布上()
    {
        using RenderFrame frame = _pipeline.Render(CreateRequest(Us(1_500_000)));

        int painted = CanvasWidth * CanvasHeight - CountPixelsDifferentFrom(frame, Background, tolerance: 8);

        Assert.True(painted > 500, $"画布上只有 {painted} 个文字像素，看上去没有画出歌词。");
    }

    [Fact]
    public void 未唱区域在不同时刻的像素完全一致()
    {
        // 这是 RENDER-02 的验收项：整行只整形一次，高亮推进不会让整行重排、跳动。
        // 取最后一个元素所在的横向区间比较：它在两个时刻都还没唱到。
        int[] starts = TokenStartColumns();
        int lastTokenLeft = starts[^1];

        using RenderFrame early = _pipeline.Render(CreateRequest(Us(1_000_000)));
        using RenderFrame later = _pipeline.Render(CreateRequest(Us(3_000_000)));

        for (int x = lastTokenLeft; x < CanvasWidth; x++)
        {
            for (int y = 0; y < CanvasHeight; y++)
            {
                Assert.True(early.TryGetPixel(x, y, out RgbaColor first));
                Assert.True(later.TryGetPixel(x, y, out RgbaColor second));
                Assert.Equal(first, second);
            }
        }
    }

    [Fact]
    public void 已唱宽度随时间单调不减()
    {
        int previous = -1;

        for (long timeUs = 0; timeUs < 5_000_000; timeUs += 250_000)
        {
            using RenderFrame frame = _pipeline.Render(CreateRequest(timeUs));
            int rightmost = RightmostColumnNear(frame, Sung, tolerance: 60);

            Assert.True(
                rightmost >= previous,
                $"{timeUs} µs 时已唱右边界退回到第 {rightmost} 列（之前是 {previous}）。");
            previous = rightmost;
        }

        Assert.True(previous > 0, "整行唱完之后应当存在已唱像素。");
    }

    [Fact]
    public void 行内空档歌词静止且行终点离屏()
    {
        ProjectSnapshot baseline = CreateSnapshot();
        LyricLine line = baseline.Lyrics.Lines[0] with
        {
            Tokens =
            [
                CreateToken("t1", "夜", 2_000_000, 3_000_000),
                CreateToken("t2", "空中最亮的星", 4_000_000, 4_500_000),
            ],
        };
        ProjectSnapshot snapshot = baseline with { Lyrics = LyricsDocument.Create([line]) };

        using RenderFrame before = _pipeline.Render(RenderRequest.At(snapshot, 1_500_000));
        using RenderFrame gapA = _pipeline.Render(RenderRequest.At(snapshot, 3_500_000));
        using RenderFrame gapB = _pipeline.Render(RenderRequest.At(snapshot, 3_750_000));
        using RenderFrame after = _pipeline.Render(RenderRequest.At(snapshot, 5_000_000));

        Assert.True(MaxDistanceFromBackground(before) > 8);
        Assert.Equal(-1, RightmostColumnNear(before, Sung, tolerance: 40));
        Assert.Equal(RightmostColumnNear(gapA, Sung, tolerance: 40),
            RightmostColumnNear(gapB, Sung, tolerance: 40));
        Assert.Equal(0, MaxDistanceFromBackground(after));
    }

    [Fact]
    public void 后续元素演唱时中间未定时元素仍保持未唱像素()
    {
        ProjectSnapshot baseline = CreateSnapshot();
        LyricLine line = baseline.Lyrics.Lines[0] with
        {
            Tokens =
            [
                CreateToken("t1", "夜", 1_000_000, 2_000_000),
                LyricToken.Create("t2", "空"),
                CreateToken("t3", "中最亮的星", 3_000_000, 5_000_000),
            ],
        };
        ProjectSnapshot snapshot = baseline with { Lyrics = LyricsDocument.Create([line]) };
        int[] starts = TokenStartColumns();

        using RenderFrame gap = _pipeline.Render(RenderRequest.At(snapshot, 2_500_000));
        using RenderFrame later = _pipeline.Render(RenderRequest.At(snapshot, 4_000_000));

        int changed = 0;
        for (int x = starts[1] + 3; x < starts[2] - 3; x++)
        {
            for (int y = 0; y < CanvasHeight; y++)
            {
                Assert.True(gap.TryGetPixel(x, y, out RgbaColor before));
                Assert.True(later.TryGetPixel(x, y, out RgbaColor after));
                if (Distance(before, after) > 8) changed++;
            }
        }

        Assert.Equal(0, changed);
        Assert.True(RightmostColumnNear(later, Sung, tolerance: 40) >= starts[2],
            "后面的已定时元素应继续显示逐字进度。");
    }

    [Fact]
    public void 未开始的元素不会把填充边界拉到整行末尾()
    {
        // 回归：曾把"尚未开始的元素"也算成"已唱到它的左边缘"，
        // 取最大值后边界直接跳到整行末尾（后面那个元素起点最大），画面一整句都变已唱。
        // 4 s 位于第 4 个元素"最亮"（3.4 s ~ 4.2 s）的 75% 处，边界应当落在"最亮"中间，
        // 而不是被后面尚未开始的"的星"拉到它的前缘。
        ShapedLine shaped = _layoutEngine.Shape(
            LineText,
            new TextLayoutOptions { FontSizePx = FontSize });

        float originX = Math.Max(0f, (CanvasWidth - shaped.Width) / 2f);
        float expected = originX + shaped.XAt(3) + ((shaped.XAt(5) - shaped.XAt(3)) * (float)((4_000_000d - 3_400_000d) / 800_000d));

        using RenderFrame frame = _pipeline.Render(CreateRequest(Us(4_000_000)));
        int rightmost = RightmostColumnNear(frame, Sung, tolerance: 60);

        Assert.InRange(rightmost, expected - 4f, expected + 4f);
        Assert.True(
            rightmost < originX + shaped.XAt(5),
            "填充越过了尚未开始的元素边界。");
    }

    [Fact]
    public void 当前元素不再使用独立颜色()
    {
        // 当前元素的未唱部分继续使用未唱样式；不存在第三种播放颜色。
        using RenderFrame frame = _pipeline.Render(CreateRequest(Us(4_000_000)));

        Assert.Equal(-1, RightmostColumnNear(frame, new RgbaColor(0x20, 0xC0, 0x40), tolerance: 60));
    }

    [Fact]
    public void 行级字号覆盖会改变该行的排版()
    {
        // 刻意用"变小"来断言：字号变大时整行会超出画布被裁掉，测出来的宽度就不准了。
        using RenderFrame baseline = _pipeline.Render(CreateRequest(Us(2_000_000)));
        using RenderFrame smaller = _pipeline.Render(
            RenderRequest.At(
                CreateSnapshot(lineOverride: new LyricStyleOverride { FontSizePx = FontSize / 2f }),
                Us(2_000_000)));

        int baselineWidth = TextExtent(baseline);
        int smallerWidth = TextExtent(smaller);

        Assert.True(
            smallerWidth < baselineWidth * 0.6f,
            $"字号覆盖没有生效：原本 {baselineWidth} 像素宽，覆盖后 {smallerWidth} 像素。");
    }

    [Fact]
    public void 行级颜色覆盖会改变已唱颜色()
    {
        RgbaColor overrideColor = new(0x00, 0x90, 0xFF);

        using RenderFrame frame = _pipeline.Render(
            RenderRequest.At(
                CreateSnapshot(lineOverride: new LyricStyleOverride { SungColor = overrideColor }),
                Us(2_000_000)));

        Assert.True(RightmostColumnNear(frame, overrideColor, tolerance: 40) > 0);
        Assert.Equal(-1, RightmostColumnNear(frame, Sung, tolerance: 40));
    }

    [Fact]
    public void 元素级颜色覆盖只影响那个元素()
    {
        // "最亮"（第 4 个元素）单独指定已唱色：唱到它时，它用覆盖色，邻居仍用行颜色。
        // 断言分两半才守得住名字里的"只影响"：①区间内出现覆盖色、确实变了；
        // ②区间外与**同一时刻的无覆盖基线**逐像素一致。
        // 只查①的话，"覆盖被画到了整行"这种错也能通过（neighbour 用行色画一遍并不稀奇）。
        RgbaColor tokenSung = new(0x00, 0xC0, 0x40);
        long timeUs = Us(4_000_000);

        using RenderFrame baseline = _pipeline.Render(CreateRequest(timeUs));
        using RenderFrame frame = _pipeline.Render(
            RenderRequest.At(
                CreateSnapshot(tokenOverride: new LyricStyleOverride { SungColor = tokenSung }),
                timeUs));

        int[] starts = TokenStartColumns();
        int tokenLeft = starts[3];
        int tokenRight = starts[4];

        // 覆盖色出现在该元素区间内。
        int overrideRightmost = RightmostColumnNear(frame, tokenSung, tolerance: 40);
        Assert.InRange(overrideRightmost, tokenLeft, tokenRight);

        // 它左边（已唱的行颜色）仍然是行样式的已唱色。
        Assert.True(
            RightmostColumnNear(frame, Sung, tolerance: 40) > 0,
            "行样式的已唱色不见了：覆盖不应影响其它元素。");

        int changedInside = 0;

        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                Assert.True(baseline.TryGetPixel(x, y, out RgbaColor before));
                Assert.True(frame.TryGetPixel(x, y, out RgbaColor after));

                if (Distance(before, after) <= 8)
                {
                    continue;
                }

                Assert.True(
                    x >= tokenLeft && x < tokenRight,
                    $"第 {x} 列在这个元素之外，却被覆盖改了。");

                changedInside++;
            }
        }

        Assert.True(changedInside > 0, "该元素一个像素都没变：覆盖没有画进画面。");
    }

    [Fact]
    public void 缩小到一半时文字位置也等比缩小()
    {
        using RenderFrame full = _pipeline.Render(CreateRequest(Us(1_500_000)));
        using RenderFrame half = _pipeline.Render(CreateRequest(Us(1_500_000)) with
        {
            Target = new RenderTarget { PixelWidth = CanvasWidth / 2, PixelHeight = CanvasHeight / 2 },
        });

        int fullLeft = FirstColumnDifferentFrom(full, Background, tolerance: 8);
        int halfLeft = FirstColumnDifferentFrom(half, Background, tolerance: 8);

        Assert.True(fullLeft > 0);
        Assert.InRange(halfLeft, (fullLeft / 2) - 3, (fullLeft / 2) + 3);
    }

    [Fact]
    public void 草稿档位不改变几何()
    {
        // 质量档位只允许改变画质成本：同一时刻的已唱边界必须完全一致。
        using RenderFrame standard = _pipeline.Render(CreateRequest(Us(2_000_000)));
        using RenderFrame draft = _pipeline.Render(CreateRequest(Us(2_000_000)) with
        {
            Quality = RenderQuality.Draft,
        });

        Assert.Equal(
            RightmostColumnNear(standard, Sung, tolerance: 90),
            RightmostColumnNear(draft, Sung, tolerance: 90));
    }

    [Fact]
    public void 取消令牌会中断渲染()
    {
        using CancellationTokenSource source = new();
        source.Cancel();

        RenderRequest request = CreateRequest(Us(1_000_000)) with { CancellationToken = source.Token };

        Assert.Throws<OperationCanceledException>(() => _pipeline.Render(request));
    }

    [Fact]
    public void 释放后不能再渲染()
    {
        SkiaRenderPipeline pipeline = new();
        pipeline.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pipeline.Render(CreateRequest(Us(1_000_000))));

        // 重复释放无害。
        pipeline.Dispose();
    }

    [Fact]
    public void 渲染不会修改项目数据()
    {
        ProjectSnapshot snapshot = CreateSnapshot();
        LyricsDocument before = snapshot.Lyrics;

        using RenderFrame frame = _pipeline.Render(RenderRequest.At(snapshot, Us(2_000_000)));

        Assert.Same(before, snapshot.Lyrics);
        Assert.Equal(LineText, snapshot.Lyrics.Lines[0].Text);
        Assert.Equal(TimeRange.Create(1_000_000, 5_000_000), snapshot.Lyrics.Lines[0].Time);
        Assert.Equal(FontSize, snapshot.LyricStyle.FontSizePx);
        Assert.Equal("test-preset", snapshot.LyricStylePresetId);
    }

    // ---------------- STYLE-03：渐变、描边、阴影、不透明度与缩放 ----------------

    /// <summary>
    /// 回归（ADR-0032）：三态各自一对渐变时，逐字进度必须仍然可见。
    /// </summary>
    /// <remarks>
    /// 曾经把同一对渐变颜色用在“未唱”与“已唱”两遍上，两遍画出的像素完全相同，
    /// 扫描高亮直接消失。这条测试就是那次修正的看门人：已唱色的最右列必须明显浅于未唱色的。
    /// </remarks>
    [Fact]
    public void 三态渐变不会抹平逐字进度()
    {
        RgbaColor cyan = new(0x00, 0xE0, 0xFF);
        RgbaColor dark = new(0x20, 0x20, 0x28);

        using RenderFrame frame = RenderWith(
            new LyricStyleOverride
            {
                UnsungGradientSpec = OptionalValue.Set<GradientSpec?>(GradientSpec.Create([
                    new GradientStop(0d, dark), new GradientStop(1d, dark),
                ])),
                SungGradientSpec = OptionalValue.Set<GradientSpec?>(GradientSpec.Create([
                    new GradientStop(0d, cyan), new GradientStop(1d, cyan),
                ])),
            },
            Us(2_000_000));

        int sungRightmost = RightmostColumnNear(frame, cyan, tolerance: 30);
        int unsungRightmost = RightmostColumnNear(frame, dark, tolerance: 12);

        Assert.True(sungRightmost > 0, "已唱渐变没有参与绘制。");
        Assert.True(
            unsungRightmost > sungRightmost,
            $"逐字进度被渐变抹平：已唱最右列 {sungRightmost}，未唱最右列 {unsungRightmost}。");
    }

    [Fact]
    public void 已唱渐变终点色会改变画面()
    {
        RgbaColor cyan = new(0x00, 0xE0, 0xFF);
        RgbaColor red = new(0xE0, 0x20, 0x20);

        using RenderFrame flat = RenderWith(
            new LyricStyleOverride
            {
                SungGradientSpec = OptionalValue.Set<GradientSpec?>(GradientSpec.Create([
                    new GradientStop(0d, cyan), new GradientStop(1d, cyan),
                ])),
            },
            Us(4_000_000));

        using RenderFrame ramp = RenderWith(
            new LyricStyleOverride
            {
                SungGradientSpec = OptionalValue.Set<GradientSpec?>(GradientSpec.Create([
                    new GradientStop(0d, cyan), new GradientStop(1d, red),
                ])),
            },
            Us(4_000_000));

        Assert.True(
            CountFramesDifferent(flat, ramp) > 100,
            "改变渐变终点色后画面几乎没变：渐变没有真正参与绘制。");
    }

    [Fact]
    public void 完整渐变描述的方向会改变实际像素()
    {
        GradientStop[] stops =
        [
            new(0d, new RgbaColor(0x00, 0x20, 0xFF)),
            new(1d, new RgbaColor(0xFF, 0x20, 0x00)),
        ];

        LyricStyle horizontal = CreateStyle() with
        {
            UnsungGradientSpec = GradientSpec.Create(stops, angleDegrees: 0d),
        };
        LyricStyle vertical = horizontal with
        {
            UnsungGradientSpec = GradientSpec.Create(stops, angleDegrees: 90d),
        };

        using RenderFrame leftToRight = _pipeline.Render(
            RenderRequest.At(CreateSnapshot(style: horizontal), Us(1_500_000)));
        using RenderFrame topToBottom = _pipeline.Render(
            RenderRequest.At(CreateSnapshot(style: vertical), Us(1_500_000)));

        Assert.True(
            CountFramesDifferent(leftToRight, topToBottom) > 100,
            "完整渐变的方向变化没有反映到实际渲染像素。");
    }

    [Fact]
    public void 径向渐变的中心和椭圆半径会改变实际像素()
    {
        GradientSpec radial = GradientSpec.Create(
            [
                new GradientStop(0d, new RgbaColor(0xFF, 0x20, 0x20)),
                new GradientStop(1d, new RgbaColor(0x20, 0x20, 0xFF)),
            ],
            kind: GradientKind.Radial,
            coordinateSpace: GradientCoordinateSpace.Line,
            centerX: 0.2d,
            centerY: 0.5d,
            radiusX: 0.3d,
            radiusY: 0.8d);
        LyricStyle radialStyle = CreateStyle() with
        {
            UnsungFill = CreateStyle().UnsungFill with { Gradient = radial },
        };
        using RenderFrame plain = _pipeline.Render(CreateRequest(Us(1_500_000)));
        using RenderFrame changed = _pipeline.Render(
            RenderRequest.At(CreateSnapshot(style: radialStyle), Us(1_500_000)));

        Assert.True(CountFramesDifferent(plain, changed) > 100,
            "径向渐变没有进入实际预览渲染。");
    }

    [Fact]
    public void 行覆盖的完整渐变会实际画进预览帧()
    {
        GradientSpec gradient = GradientSpec.Create(
            [new GradientStop(0d, new RgbaColor(0xFF, 0x00, 0x00)),
                new GradientStop(0.5d, new RgbaColor(0x00, 0xFF, 0x00)),
                new GradientStop(1d, new RgbaColor(0x00, 0x00, 0xFF))],
            angleDegrees: 90d, colorSpace: GradientColorSpace.Hsv);
        using RenderFrame original = _pipeline.Render(CreateRequest(Us(1_500_000)));
        using RenderFrame changed = RenderWith(
            new LyricStyleOverride { UnsungGradientSpec = gradient }, Us(1_500_000));

        Assert.True(CountFramesDifferent(original, changed) > 100,
            "完整渐变虽然写入了行覆盖，但预览帧没有变化。");
    }

    [Fact]
    public void 描边会在文字外增加像素()
    {
        RgbaColor strokeColor = new(0xFF, 0xFF, 0xFF);

        using RenderFrame plain = _pipeline.Render(CreateRequest(Us(2_000_000)));
        using RenderFrame outlined = RenderWith(
            new LyricStyleOverride { StrokeColor = strokeColor, StrokeWidthPx = 8f },
            Us(2_000_000));

        Assert.True(
            CountPixelsDifferentFrom(outlined, Background, tolerance: 8)
                > CountPixelsDifferentFrom(plain, Background, tolerance: 8),
            "描边没有画出来：像素数没有增加。");
        Assert.True(RightmostColumnNear(outlined, strokeColor, tolerance: 24) > 0, "找不到描边颜色。");
    }

    [Fact]
    public void 外部描边描述会增加填充外侧像素()
    {
        LyricStyle style = CreateStyle() with
        {
            UnsungColor = RgbaColor.White,
            SungColor = RgbaColor.White,
            StrokeSpec = StrokeSpec.Create(new RgbaColor(0xFF, 0x80, 0x10), 8f, StrokeJoin.Round),
        };

        using RenderFrame plain = _pipeline.Render(CreateRequest(Us(2_000_000)));
        using RenderFrame outlined = _pipeline.Render(
            RenderRequest.At(CreateSnapshot(style: style), Us(2_000_000)));

        Assert.True(
            CountPixelsDifferentFrom(outlined, Background, tolerance: 8)
                > CountPixelsDifferentFrom(plain, Background, tolerance: 8),
            "StrokeSpec 没有在填充外侧增加像素。");
        Assert.True(RightmostColumnNear(outlined, new RgbaColor(0xFF, 0x80, 0x10), tolerance: 32) >= 0);
    }

    [Fact]
    public void 元素明确关闭完整描边后包括外沿都与无描边一致()
    {
        LyricStyle plainStyle = CreateStyle();
        using RenderFrame plain = _pipeline.Render(RenderRequest.At(CreateSnapshot(style: plainStyle), Us(3_500_000)));
        using RenderFrame disabled = _pipeline.Render(RenderRequest.At(CreateSnapshot(
            style: plainStyle with { StrokeSpec = StrokeSpec.Create(new RgbaColor(240, 40, 20), 10f) },
            tokenOverride: new LyricStyleOverride { StrokeSpec = OptionalValue.Set<StrokeSpec?>(null) }), Us(3_500_000)));
        int[] starts = TokenStartColumns();
        int changed = 0;
        for (int y = 0; y < CanvasHeight; y++)
            for (int x = starts[3] + 10; x < starts[4] - 10; x++)
            {
                plain.TryGetPixel(x, y, out RgbaColor expected);
                disabled.TryGetPixel(x, y, out RgbaColor actual);
                if (expected != actual) changed++;
            }
        Assert.True(changed == 0, $"被关闭的元素区域仍有 {changed} 个基线描边像素。");
        Assert.False(plain.PixelMemory.Span.SequenceEqual(disabled.PixelMemory.Span));
    }

    [Theory]
    [InlineData(128, 1f)]
    [InlineData(255, 0.5f)]
    [InlineData(0, 1f)]
    public void 外部描边不侵入半透明或透明填充(int alpha, float opacity)
    {
        LyricStyle fill = CreateStyle() with
        {
            UnsungColor = new RgbaColor(20, 180, 80, (byte)alpha),
            SungColor = new RgbaColor(20, 180, 80, (byte)alpha),
            Opacity = opacity,
        };
        LyricStyle opaque = fill with
        {
            UnsungColor = RgbaColor.White, SungColor = RgbaColor.White, Opacity = 1f,
        };
        using RenderFrame mask = _pipeline.Render(RenderRequest.At(CreateSnapshot(style: opaque), Us(2_000_000)) with
        { TransparentBackground = true });
        using RenderFrame plain = _pipeline.Render(RenderRequest.At(CreateSnapshot(style: fill), Us(2_000_000)));
        using RenderFrame outlined = _pipeline.Render(RenderRequest.At(CreateSnapshot(style: fill with
        {
            StrokeSpec = StrokeSpec.Create(new RgbaColor(250, 20, 30), 8f),
        }), Us(2_000_000)));
        int interior = 0;
        int changed = 0;
        List<string> examples = [];
        for (int y = 0; y < CanvasHeight; y++)
            for (int x = 0; x < CanvasWidth; x++)
            {
                mask.TryGetPixel(x, y, out RgbaColor coverage);
                if (coverage.Alpha != 255) continue;
                // 3×3 邻域都满覆盖才算字形内部；抗锯齿边界存在光栅舍入，另由外扩像素断言检查。
                bool inside = true;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        inside &= mask.TryGetPixel(x + dx, y + dy, out RgbaColor neighbor) && neighbor.Alpha == 255;
                if (!inside) continue;
                interior++;
                plain.TryGetPixel(x, y, out RgbaColor before);
                outlined.TryGetPixel(x, y, out RgbaColor after);
                if (before != after)
                {
                    changed++;
                    if (examples.Count < 5) examples.Add($"({x},{y}) {before} -> {after}");
                }
            }
        Assert.True(interior > 500);
        Assert.True(changed == 0, $"描边侵入了 {changed}/{interior} 个完整填充像素：{string.Join(", ", examples)}。");
        Assert.False(plain.PixelMemory.Span.SequenceEqual(outlined.PixelMemory.Span));
    }

    [Fact]
    public void 阴影会在文字下方多出像素()
    {
        RgbaColor shadowColor = new(0x00, 0x00, 0x00);

        using RenderFrame plain = _pipeline.Render(CreateRequest(Us(2_000_000)));
        using RenderFrame shadowed = RenderWith(
            new LyricStyleOverride
            {
                ShadowColor = shadowColor,
                ShadowSpec = OptionalValue.Set<ShadowSpec?>(new ShadowSpec
                {
                    Color = shadowColor, AngleDegrees = 90d, DistancePx = 24f,
                }),
                ShadowBlurPx = 6f,
            },
            Us(2_000_000));

        Assert.True(
            LastRowDifferentFrom(shadowed, Background) > LastRowDifferentFrom(plain, Background),
            "阴影没有出现在文字下方。");
    }

    [Fact]
    public void 方向阴影描述会按角度和距离绘制()
    {
        LyricStyle style = CreateStyle() with
        {
            ShadowSpec = ShadowSpec.Create(
                new RgbaColor(0x00, 0xD0, 0xFF, 220),
                opacity: 0.9f,
                angleDegrees: 90d,
                distancePx: 22f,
                blurRadiusPx: 5f),
        };

        using RenderFrame shadowed = _pipeline.Render(
            RenderRequest.At(CreateSnapshot(style: style), Us(2_000_000)));

        Assert.True(
            LastRowDifferentFrom(shadowed, Background) > CanvasHeight / 2,
            "ShadowSpec 的向下偏移没有出现在文字下方。");
    }

    [Fact]
    public void 不透明度会让像素更接近背景()
    {
        using RenderFrame opaque = _pipeline.Render(CreateRequest(Us(2_000_000)));
        using RenderFrame translucent = RenderWith(new LyricStyleOverride { Opacity = 0.35f }, Us(2_000_000));

        Assert.True(
            MaxDistanceFromBackground(translucent) < MaxDistanceFromBackground(opaque),
            "不透明度没有生效：最鲜艳的像素没有变淡。");
    }

    [Fact]
    public void 基础缩放会等比缩小文字()
    {
        using RenderFrame full = _pipeline.Render(CreateRequest(Us(2_000_000)));
        using RenderFrame half = RenderWith(new LyricStyleOverride { Scale = 0.5f }, Us(2_000_000));

        int fullExtent = TextExtent(full);
        int halfExtent = TextExtent(half);

        Assert.True(fullExtent > 0);
        Assert.True(
            halfExtent < fullExtent * 0.65f,
            $"缩放没有生效：原宽 {fullExtent}，缩到一半后 {halfExtent}。");
    }

    /// <summary>
    /// 回归（ADR-0032）：元素级只改描边也必须重画那个元素。
    /// </summary>
    /// <remarks>
    /// 元素重画的跳过条件曾经只看颜色，于是“只设描边/阴影/不透明度”的覆盖被静默忽略。
    /// </remarks>
    [Fact]
    public void 元素级只设描边也会生效()
    {
        using RenderFrame plain = _pipeline.Render(
            RenderRequest.At(CreateSnapshot(), Us(2_000_000)));

        using RenderFrame outlined = _pipeline.Render(
            RenderRequest.At(
                CreateSnapshot(
                    tokenOverride: new LyricStyleOverride
                    {
                        StrokeColor = new RgbaColor(0xFF, 0xFF, 0xFF),
                        StrokeWidthPx = 8f,
                    }),
                Us(2_000_000)));

        Assert.True(
            CountFramesDifferent(plain, outlined) > 0,
            "元素级只设描边时没有重画该元素。");
    }

    [Fact]
    public void 元素明确关闭描边和阴影后不残留整行外沿()
    {
        LyricStyle plain = CreateStyle();
        LyricStyle decorated = plain with
        {
            Stroke = new StrokeSpec { Color = new RgbaColor(0xFF, 0x20, 0x20), WidthPx = 10f },
            Shadow = new ShadowSpec
            {
                Color = new RgbaColor(0x20, 0xC0, 0xFF),
                AngleDegrees = 45d,
                DistancePx = MathF.Sqrt(128f),
                BlurRadiusPx = 6f,
            },
        };
        LyricStyleOverride off = new()
        {
            StrokeColor = OptionalValue.Set<RgbaColor?>(null),
            StrokeWidthPx = 0f,
            ShadowColor = OptionalValue.Set<RgbaColor?>(null),
            ShadowBlurPx = 0f,
        };

        using RenderFrame baseline = _pipeline.Render(RenderRequest.At(
            SingleTokenSnapshot(plain), Us(2_000_000)));
        using RenderFrame withEffects = _pipeline.Render(RenderRequest.At(
            SingleTokenSnapshot(decorated), Us(2_000_000)));
        using RenderFrame turnedOff = _pipeline.Render(RenderRequest.At(
            SingleTokenSnapshot(decorated, off), Us(2_000_000)));

        Assert.False(baseline.Pixels.SequenceEqual(withEffects.Pixels));
        Assert.True(baseline.Pixels.SequenceEqual(turnedOff.Pixels),
            "元素明确关闭装饰后，仍残留整行描边或阴影的外沿。");
    }

    [Fact]
    public void 字距进入实际渲染且不会复用旧布局()
    {
        LyricStyle normal = CreateStyle();
        LyricStyle typography = normal with { LetterSpacingPx = 10f };
        using RenderFrame plain = _pipeline.Render(RenderRequest.At(SingleTokenSnapshot(normal), Us(2_000_000)));
        using RenderFrame changed = _pipeline.Render(RenderRequest.At(SingleTokenSnapshot(typography), Us(2_000_000)));

        Assert.True(CountFramesDifferent(plain, changed) > 100,
            "字距没有进入绘制和布局缓存键。");
    }

    [Fact]
    public void 请求粗字重会改变实际字形()
    {
        LyricStyle normal = CreateStyle() with { FontFamilyName = SkiaTextLayoutEngine.DefaultFamilyName };
        LyricStyle heavy = normal with { FontWeight = 900 };
        using RenderFrame plain = _pipeline.Render(RenderRequest.At(SingleTokenSnapshot(normal), Us(2_000_000)));
        using RenderFrame changed = _pipeline.Render(RenderRequest.At(SingleTokenSnapshot(heavy), Us(2_000_000)));

        Assert.True(CountFramesDifferent(plain, changed) > 100,
            "字重请求没有改变文字字形。");
    }

    [Fact]
    public void 请求斜体会改变支持斜体的字体字形()
    {
        LyricStyle normal = CreateStyle() with { FontFamilyName = "DejaVu Sans" };
        LyricStyle italic = normal with { IsItalic = true };
        LyricToken token = CreateToken("latin", "Hello", 1_000_000, 3_000_000);
        LyricLine line = LyricLine.Create("latin-line", "Hello",
            TimeRange.Create(1_000_000, 3_000_000), TimingStatus.Confirmed, [token]);
        Project project = Project.Create("斜体像素", Composition.Create(CanvasWidth, CanvasHeight,
            FrameRate.Fps30, Background, outputDurationUs: 3_000_000), LyricsDocument.Create([line]),
            lyricStyle: normal);
        using RenderFrame plain = _pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(project), Us(2_000_000)));
        using RenderFrame changed = _pipeline.Render(RenderRequest.At(
            ProjectSnapshot.FromProject(project with { LyricStyle = italic }), Us(2_000_000)));

        Assert.True(CountFramesDifferent(plain, changed) > 100,
            "斜体请求没有改变文字字形。");
    }

    [Fact]
    public void 元素方向阴影覆盖会改变真实像素()
    {
        LyricStyle style = CreateStyle() with
        {
            ShadowSpec = ShadowSpec.Create(new RgbaColor(255, 30, 30), angleDegrees: 0d,
                distancePx: 18f, blurRadiusPx: 6f),
        };
        LyricStyleOverride token = new()
        {
            ShadowSpec = ShadowSpec.Create(new RgbaColor(30, 30, 255), angleDegrees: 90d,
                distancePx: 18f, blurRadiusPx: 6f),
        };
        using RenderFrame inherited = _pipeline.Render(RenderRequest.At(
            SingleTokenSnapshot(style), Us(2_000_000)));
        using RenderFrame overridden = _pipeline.Render(RenderRequest.At(
            SingleTokenSnapshot(style, token), Us(2_000_000)));

        Assert.True(CountFramesDifferent(inherited, overridden) > 100,
            "元素覆盖的阴影方向没有进入实际渲染。");
    }

    private static ProjectSnapshot SingleTokenSnapshot(LyricStyle style,
        LyricStyleOverride? tokenOverride = null)
    {
        LyricToken token = CreateToken("single", "夜空", 1_000_000, 3_000_000, tokenOverride);
        LyricLine line = LyricLine.Create("single-line", "夜空",
            TimeRange.Create(1_000_000, 3_000_000), TimingStatus.Confirmed, [token]);
        return ProjectSnapshot.FromProject(Project.Create("元素装饰测试",
            Composition.Create(CanvasWidth, CanvasHeight, FrameRate.Fps30, Background,
                outputDurationUs: 3_000_000), LyricsDocument.Create([line]), lyricStyle: style));
    }

    /// <summary>用一份行级覆盖渲染同一时刻。</summary>
    private RenderFrame RenderWith(LyricStyleOverride lineOverride, long timeUs) =>
        _pipeline.Render(RenderRequest.At(CreateSnapshot(lineOverride: lineOverride), timeUs));

    /// <summary>两帧之间差异明显的像素数。</summary>
    private static int CountFramesDifferent(RenderFrame first, RenderFrame second)
    {
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);

        int count = 0;

        for (int y = 0; y < first.Height; y++)
        {
            for (int x = 0; x < first.Width; x++)
            {
                Assert.True(first.TryGetPixel(x, y, out RgbaColor left));
                Assert.True(second.TryGetPixel(x, y, out RgbaColor right));

                if (Distance(left, right) > 8)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>全帧里最偏离背景的那个像素的色差。</summary>
    private static int MaxDistanceFromBackground(RenderFrame frame)
    {
        int maximum = 0;

        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                Assert.True(frame.TryGetPixel(x, y, out RgbaColor color));
                maximum = Math.Max(maximum, Distance(color, Background));
            }
        }

        return maximum;
    }

    /// <summary>最下方一个非背景行。</summary>
    private static int LastRowDifferentFrom(RenderFrame frame, RgbaColor background)
    {
        for (int y = frame.Height - 1; y >= 0; y--)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                Assert.True(frame.TryGetPixel(x, y, out RgbaColor color));

                if (Distance(color, background) > 8)
                {
                    return y;
                }
            }
        }

        return -1;
    }

    private static long Us(long microseconds) => microseconds;

    private static RenderRequest CreateRequest(long timeUs) => RenderRequest.At(CreateSnapshot(), timeUs);

    private static LyricStyle CreateStyle() => new()
    {
        FontSizePx = FontSize,
        UnsungColor = Unsung,
        SungColor = Sung,
        VerticalCenterRatio = 0.5d,
    };

    private static ProjectSnapshot CreateSnapshot(
        LyricStyle? style = null,
        LyricStyleOverride? lineOverride = null,
        LyricStyleOverride? tokenOverride = null)
    {
        // 五个元素各占 0.8 s：整行 1 s ~ 5 s。
        LyricToken[] tokens =
        [
            CreateToken("t1", "夜", 1_000_000, 1_800_000),
            CreateToken("t2", "空", 1_800_000, 2_600_000),
            CreateToken("t3", "中", 2_600_000, 3_400_000),
            CreateToken("t4", "最亮", 3_400_000, 4_200_000, tokenOverride),
            CreateToken("t5", "的星", 4_200_000, 5_000_000),
        ];

        LyricLine line = LyricLine.Create(
            "l1",
            LineText,
            TimeRange.Create(1_000_000, 5_000_000),
            TimingStatus.Imported,
            tokens,
            lineOverride);

        Project project = Project.Create(
            "渲染测试",
            Composition.Create(CanvasWidth, CanvasHeight, FrameRate.Fps30, Background, outputDurationUs: 6_000_000),
            LyricsDocument.Create([line]),
            lyricStyle: style ?? CreateStyle(),
            lyricStylePresetId: "test-preset");

        return ProjectSnapshot.FromProject(project);
    }

    private static LyricToken CreateToken(
        string id,
        string text,
        long startUs,
        long endUs,
        LyricStyleOverride? styleOverride = null) =>
        LyricToken.Create(id, text, TimeRange.Create(startUs, endUs), TimingStatus.Imported, styleOverride);

    /// <summary>
    /// 算出每个元素在画布上的起始列（用与管线相同的布局与居中规则）。
    /// </summary>
    /// <remarks>
    /// 元素在行文本里的下标由元素自身的文本长度累加而来（"夜"、"空"、"中"、"最亮"、"的星"
    /// → 0、1、2、3、5），不能按元素个数均分：元素长度并不相等。
    /// </remarks>
    private int[] TokenStartColumns()
    {
        ShapedLine shaped = _layoutEngine.Shape(
            LineText,
            new TextLayoutOptions { FontSizePx = FontSize });

        float originX = Math.Max(0f, (CanvasWidth - shaped.Width) / 2f);
        string[] tokenTexts = ["夜", "空", "中", "最亮", "的星"];
        int[] columns = new int[tokenTexts.Length];
        int offset = 0;

        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = (int)Math.Floor(originX + shaped.XAt(offset));
            offset += tokenTexts[i].Length;
        }

        return columns;
    }

    /// <summary>文字占据的横向像素宽度（首末非背景列之差）。</summary>
    private static int TextExtent(RenderFrame frame)
    {
        int first = FirstColumnDifferentFrom(frame, Background, tolerance: 8);
        int last = LastColumnDifferentFrom(frame, Background, tolerance: 8);

        return last < 0 || first < 0 ? 0 : last - first;
    }

    private static int LastColumnDifferentFrom(RenderFrame frame, RgbaColor background, int tolerance)
    {
        for (int x = frame.Width - 1; x >= 0; x--)
        {
            for (int y = 0; y < frame.Height; y++)
            {
                Assert.True(frame.TryGetPixel(x, y, out RgbaColor color));

                if (Distance(color, background) > tolerance)
                {
                    return x;
                }
            }
        }

        return -1;
    }

    private static int CountPixelsDifferentFrom(RenderFrame frame, RgbaColor background, int tolerance)
    {
        int count = 0;

        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                if (!frame.TryGetPixel(x, y, out RgbaColor color) || Distance(color, background) > tolerance)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int FirstColumnDifferentFrom(RenderFrame frame, RgbaColor background, int tolerance)
    {
        for (int x = 0; x < frame.Width; x++)
        {
            for (int y = 0; y < frame.Height; y++)
            {
                Assert.True(frame.TryGetPixel(x, y, out RgbaColor color));

                if (Distance(color, background) > tolerance)
                {
                    return x;
                }
            }
        }

        return -1;
    }

    private static int RightmostColumnNear(RenderFrame frame, RgbaColor target, int tolerance)
    {
        int rightmost = -1;

        for (int x = 0; x < frame.Width; x++)
        {
            for (int y = 0; y < frame.Height; y++)
            {
                Assert.True(frame.TryGetPixel(x, y, out RgbaColor color));

                if (Distance(color, target) <= tolerance)
                {
                    rightmost = x;
                    break;
                }
            }
        }

        return rightmost;
    }

    private static int Distance(RgbaColor first, RgbaColor second) =>
        Math.Abs(first.Red - second.Red)
        + Math.Abs(first.Green - second.Green)
        + Math.Abs(first.Blue - second.Blue);
}
