using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class GradientOpacityTests
{
    [Theory]
    [InlineData(false, 640, 360, 1_110_000)] [InlineData(false, 640, 360, 2_010_000)] [InlineData(false, 640, 360, 2_750_000)]
    [InlineData(false, 480, 180, 1_110_000)] [InlineData(false, 480, 180, 2_010_000)] [InlineData(false, 480, 180, 2_750_000)]
    [InlineData(true, 640, 360, 1_110_000)] [InlineData(true, 640, 360, 2_010_000)] [InlineData(true, 640, 360, 2_750_000)]
    [InlineData(true, 480, 180, 1_110_000)] [InlineData(true, 480, 180, 2_010_000)] [InlineData(true, 480, 180, 2_750_000)]
    public void 亚像素已唱边界在非均匀密度与旋转元素下只应用一次外观alpha(bool family, int width, int height, long time)
    {
        var geometry = LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64,
            UnsungColor = RgbaColor.Transparent, SungColor = RgbaColor.Transparent,
            Stroke = StrokeSpec.Create(RgbaColor.White, 5), Shadow = null, Opacity = 1 };
        var gradient = GradientSpec.Create([new(0, new(255, 0, 0, 128)), new(1, new(0, 0, 255, 128))]);
        var style = geometry with { Opacity = .65f, Stroke = geometry.Stroke! with { Gradient = gradient } };
        var line = LyricLine.Create("row", "歌", new(0, 5_000_000), TimingStatus.Edited,
            [LyricToken.Create("word", "歌", new(1_000_000, 3_000_000), TimingStatus.Edited)]);
        var project = Project.Create("split alpha", Composition.Create(640, 360, Core.Time.FrameRate.Fps30), LyricsDocument.Create([line]), lyricStyle: geometry);
        using var renderer = new SkiaRenderPipeline();
        var output = new RenderTarget { PixelWidth = width, PixelHeight = height };
        using var reference = Capture(ProjectSnapshot.FromProject(project));
        using var actual = Capture(ProjectSnapshot.FromProject(project with { LyricStyle = style }));
        byte[] mask = SkiaTestReadback.Pixels(reference.Surface.Image), pixels = SkiaTestReadback.Pixels(actual.Surface.Image);
        int checkedPixels = 0;
        for (int i = 3; i < mask.Length; i += 4)
        {
            Assert.InRange(pixels[i], 0, 84);
            if (mask[i] != 255) continue;
            Assert.InRange(pixels[i], 82, 84); checkedPixels++;
        }
        Assert.True(checkedPixels > 10);

        SceneColorPartResult Capture(ProjectSnapshot snapshot)
        {
            var scene = renderer.GetSceneFrame(snapshot, time);
            var token = scene.TokenGeometries.Single().WithRuby;
            var request = RenderRequest.At(snapshot, time) with { Target = output, TransparentBackground = true };
            SceneCaptureTarget target = new SceneCaptureTarget.Line(new(new("row"), new(scene.Lines[0].BindingId!)));
            if (family)
            {
                var angle = .13;
                request = request with { PreparedFrame = new(snapshot, time, Fx.OutputMode.Transparent, output, scene,
                    [new(token, new Fx.Affine2(Math.Cos(angle), Math.Sin(angle), -Math.Sin(angle), Math.Cos(angle), 2.35, -3.6))]) };
                target = new SceneCaptureTarget.Token(token);
            }
            return renderer.CaptureSceneColorPart(request, target, SceneColorPart.BaseStroke, new(0, 0, 640, 360), GraphExecutionBudget.Default);
        }
    }

    [Theory]
    [InlineData(false, 0, false)] [InlineData(false, 255, false)]
    [InlineData(true, 0, false)] [InlineData(true, 255, false)]
    [InlineData(false, 0, true)] [InlineData(false, 255, true)]
    [InlineData(true, 0, true)] [InlineData(true, 255, true)]
    public void 渐变色标和样式透明度各乘一次且历史纯色alpha不参与(bool stroke, int legacyAlpha, bool ruby)
    {
        var gradient = GradientSpec.Create([new(0, new(255, 0, 0, 128)), new(1, new(0, 0, 255, 128))]);
        var geometry = LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64,
            UnsungColor = stroke ? RgbaColor.Transparent : RgbaColor.White,
            SungColor = stroke ? RgbaColor.Transparent : RgbaColor.White,
            Stroke = stroke ? StrokeSpec.Create(RgbaColor.White, 5) : null, Shadow = null, Opacity = 1 };
        var fill = new FillSpec { Color = new(255, 255, 255, (byte)legacyAlpha), Gradient = gradient };
        var style = stroke ? geometry with { Opacity = .65f, Stroke = geometry.Stroke! with { Color = legacyAlpha == 0 ? null : RgbaColor.White, Gradient = gradient } }
            : geometry with { Opacity = .65f, UnsungFill = fill, SungFill = fill };
        var line = LyricLine.Create("row", "歌", new(0, 5_000_000), TimingStatus.Edited,
            [LyricToken.Create("word", "歌", new(1_000_000, 3_000_000), TimingStatus.Edited)]) with
            { RubyAnnotations = [RubyAnnotation.Create("ruby", "word", "か")] };
        var snapshot = ProjectSnapshot.FromProject(Project.Create("gradient alpha", Composition.Create(640, 360, Core.Time.FrameRate.Fps30),
            LyricsDocument.Create([line]), lyricStyle: geometry));
        using SkiaRenderPipeline renderer = new();
        var request = RenderRequest.At(snapshot, 2_000_000) with { TransparentBackground = true };
        var target = new SceneCaptureTarget.Token(renderer.GetSceneFrame(snapshot, request.TimeUs).TokenGeometries.Single().WithRuby);
        var part = stroke ? (ruby ? SceneColorPart.RubyStroke : SceneColorPart.BaseStroke)
            : (ruby ? SceneColorPart.RubyFill : SceneColorPart.BaseFill);
        using var reference = renderer.CaptureSceneColorPart(request, target, part, new(0,0,640,360), GraphExecutionBudget.Default);
        using var actual = renderer.CaptureSceneColorPart(request with { Project = snapshot with { LyricStyle = style } },
            target, part, new(0,0,640,360), GraphExecutionBudget.Default);
        using var referenceBitmap = SKBitmap.FromImage(reference.Surface.Image);
        using var actualBitmap = SKBitmap.FromImage(actual.Surface.Image);
        var mask = referenceBitmap.Bytes; var pixels = actualBitmap.Bytes; int checkedPixels = 0;
        for (int i = 3; i < mask.Length; i += 4)
        {
            if (mask[i] != 255) continue;
            // 独立白色几何给出完整内部覆盖；128 × .65 应为83，不能是54或27。
            Assert.True(pixels[i] is >= 82 and <= 84, $"Pixel ({i/4%640},{i/4/640}): reference {mask[i]}, actual {pixels[i]}."); checkedPixels++;
        }
        Assert.True(checkedPixels > 10);
    }
}
