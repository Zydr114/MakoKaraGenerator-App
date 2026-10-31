using System.Diagnostics;
using System.Globalization;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Export.Tests;

/// <summary>
/// 成片内容的测试：导出的视频里必须真的画着歌词。
/// </summary>
/// <remarks>
/// 尺寸、编码器、时长都能"看起来对"却拍出一片纯色——只有把画面抽出来看才算验证过。
/// 所以这里导出后用 FFmpeg 抽一帧，再逐像素确认它<b>不是</b>纯背景色。
/// </remarks>
public sealed class ExportedFrameTests : IDisposable
{
    private const int Width = 320;
    private const int Height = 180;
    private const double DurationSeconds = 1d;

    private readonly TemporaryDirectory _directory = new();

    /// <inheritdoc/>
    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task 导出的画面里有歌词而不只是背景色()
    {
        string output = Path.Combine(_directory.Path, "content.mp4");

        // 背景用纯蓝、歌词用默认样式（浅色）：反差足够大，一眼能分辨。
        Project project = Project.Create(
            "画面内容",
            Composition.Create(
                Width,
                Height,
                FrameRate.Fps30,
                RgbaColor.Parse("#0000FF"),
                outputDurationUs: (long)(DurationSeconds * 1_000_000d)),
            CreateLyrics());

        using SkiaRenderPipeline pipeline = new(new SkiaTextLayoutEngine());
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());

        await exporter.ExportAsync(project, new VideoExportOptions { OutputPath = output, IncludeAudio = false });

        string framePath = Path.Combine(_directory.Path, "frame.png");

        await ExtractFrameAsync(output, framePath, frameIndex: 15);

        using SKBitmap bitmap = SKBitmap.Decode(framePath)
            ?? throw new InvalidOperationException("抽出来的帧无法解码。");

        int painted = 0;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);

                // 背景是纯蓝：只要出现明显不同的像素，就说明歌词层被画进去了。
                if (pixel.Blue < 200 || pixel.Red > 40 || pixel.Green > 40)
                {
                    painted++;
                }
            }
        }

        Assert.True(
            painted > 200,
            string.Create(CultureInfo.InvariantCulture, $"画面里几乎没有歌词：与背景不同的像素只有 {painted} 个。"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 成片同帧包含双行注音和静态翻译且与预览像素位置一致(bool transformContainer)
    {
        var first = LyricLine.Create("first", "字", new TimeRange(0, 900_000), TimingStatus.Edited,
            [LyricToken.Create("base", "字", new TimeRange(0, 900_000), TimingStatus.Edited)]) with
        { RubyAnnotations = [RubyAnnotation.Create("ruby", "base", "zi")] };
        var second = LyricLine.Create("second", "第二行", new TimeRange(800_000, 1_000_000), TimingStatus.Edited);
        var lyrics = LyricsDocument.Create([first, second]);
        var primary = lyrics.PrimaryBranch!;
        var translated = LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
            [LyricLine.Create("translated", "TRANSLATED") with { PrimaryLineId = "second" }], timingMode: LyricBranchTimingMode.FollowPrimary) with
        { StyleOverride = new() { FontSizePx = 12 } };
        var project = Project.Create("场景成片", Composition.Create(Width, Height, FrameRate.Fps30, RgbaColor.Parse("#0000FF"), 1_000_000),
            lyrics with { Branches = [primary, translated] }, lyricStyle: LyricStyle.Default with
            { FontSizePx = 24, UnsungColor = RgbaColor.White, SungColor = RgbaColor.White }) with
        { LyricPresentation = LyricPresentationDefinition.FromBranches([new() { BranchId = primary.Id,
            SlotSequenceProfile = SlotSequenceAnimationProfile.Default with { EnterDurationUs = 0, ExitDurationUs = 0 } },
            new() { BranchId = translated.Id, DefaultPlacement = ScenePlacement.Create(new(.5, .86)), SafeArea = NormalizedSafeArea.Translation }]) };
        if (transformContainer) project = PresentationEditor.SetBranch(project, project.LyricPresentation.Resolve(primary.Id) with
            { Placement = ScenePlacement.Create(new(.55, .47), rotationDegrees: 5, scale: .9) });
        string output = Path.Combine(_directory.Path, "scene.mp4"), framePath = Path.Combine(_directory.Path, "scene.png");
        using SkiaRenderPipeline pipeline = new();
        VideoExporter exporter = new(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        await exporter.ExportAsync(project, new VideoExportOptions { OutputPath = output, IncludeAudio = false });
        await ExtractFrameAsync(output, framePath, 15);
        using SKBitmap bitmap = SKBitmap.Decode(framePath)!;
        using var preview = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(project), 500_000) with { TransparentBackground = true });
        foreach ((int top, int bottom) in new[] { (0, 90), (91, 137), (138, 179) })
        {
            int expected = 0, matching = 0;
            for (int y = top; y <= bottom; y++) for (int x = 0; x < Width; x++)
            {
                if (preview.PixelMemory.Span[y * preview.Stride + x * 4 + 3] <= 180) continue;
                expected++;
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 120 && pixel.Green > 120) matching++;
            }
            Assert.True(expected > 20, $"区域 {top}–{bottom} 没有对应歌词层");
            Assert.True(matching >= expected * .9, $"区域 {top}–{bottom} 成片与预览位置不一致：{matching}/{expected}");
        }
    }

    [Fact]
    public async Task 独占案例切换前居中及恢复后成片与预览保持一致()
    {
        var original = ExclusiveDisplayFixture.Create();
        var project = original with { Composition = original.Composition with { CanvasWidth = Width, CanvasHeight = Height },
            LyricStyle = original.LyricStyle with { FontSizePx = 22 } };
        using SkiaRenderPipeline pipeline = new();
        var exporter = new VideoExporter(pipeline, new FfmpegMediaProbe(), new FfmpegMediaEncoder());
        string output = Path.Combine(_directory.Path, "exclusive.mp4");
        await exporter.ExportAsync(project, new VideoExportOptions { OutputPath = output, IncludeAudio = false });
        foreach (int frameIndex in new[] { 105, 150, 225 })
        {
            long timeUs = frameIndex * 1_000_000L / 30;
            using var preview = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(project), timeUs));
            string path = Path.Combine(_directory.Path, "exclusive-" + frameIndex + ".png");
            await ExtractFrameAsync(output, path, frameIndex);
            using var bitmap = SKBitmap.Decode(path)!;
            int expected = 0, matching = 0;
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
            {
                int offset = y * preview.Stride + x * 4;
                var pixels = preview.PixelMemory.Span;
                if (pixels[offset + 2] < 120) continue;
                expected++;
                var actual = bitmap.GetPixel(x, y);
                if (Math.Abs(actual.Red - pixels[offset + 2]) + Math.Abs(actual.Green - pixels[offset + 1])
                    + Math.Abs(actual.Blue - pixels[offset]) < 130) matching++;
            }
            Assert.True(expected > 40, "预览没有绘制歌词");
            Assert.True(matching > expected * .9, $"帧 {frameIndex} 与预览不一致：{matching}/{expected}");
        }
    }

    [Fact]
    public async Task 通用双槽居中三槽成片含全部槽位并与同帧预览一致()
    {
        var original = GeneralDisplayFixture.Create();
        var project = original with { Composition = original.Composition with { CanvasWidth = Width,CanvasHeight = Height },
            LyricStyle = original.LyricStyle with { FontSizePx = 22 } };
        using SkiaRenderPipeline pipeline = new();
        var exporter = new VideoExporter(pipeline,new FfmpegMediaProbe(),new FfmpegMediaEncoder());
        string output = Path.Combine(_directory.Path,"generic.mp4");
        await exporter.ExportAsync(project,new VideoExportOptions { OutputPath = output,IncludeAudio = false });
        foreach (int frameIndex in new[] { 90,150,330 })
        {
            long timeUs = frameIndex * 1_000_000L / 30;
            using var preview = pipeline.Render(RenderRequest.At(ProjectSnapshot.FromProject(project),timeUs));
            string path = Path.Combine(_directory.Path,"generic-"+frameIndex+".png"); await ExtractFrameAsync(output,path,frameIndex);
            using var bitmap = SKBitmap.Decode(path)!; int expected = 0,matching = 0;
            for (int y = 0;y < Height;y++) for (int x = 0;x < Width;x++)
            {
                int offset = y * preview.Stride + x * 4; var bytes = preview.PixelMemory.Span;
                if (bytes[offset+2] < 120) continue; expected++; var actual = bitmap.GetPixel(x,y);
                if (Math.Abs(actual.Red-bytes[offset+2])+Math.Abs(actual.Green-bytes[offset+1])+Math.Abs(actual.Blue-bytes[offset]) < 130) matching++;
            }
            Assert.True(expected > 60); Assert.True(matching > expected * .9,$"帧 {frameIndex}: {matching}/{expected}");
            if (frameIndex == 330) Assert.Equal(3,pipeline.GetSceneFrame(ProjectSnapshot.FromProject(project),timeUs).Lines.Count(l => l.Branch.Id == "primary"));
        }
    }

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    public async Task 前奏完整案例不同帧率成片与同帧预览包含相同标记和歌词(int fps)
    {
        var original=LeadInFixture.Create(true,true);
        TimeRange? ScaleTime(TimeRange? time) => time is { } range ? new(range.StartUs/10,range.EndUs/10) : null;
        var lyrics=original.Lyrics with { Branches=[..original.Lyrics.Branches.Select(branch => branch with
        { Lines=[..branch.Lines.Select(line => line with { Time=ScaleTime(line.Time),
            Tokens=[..line.Tokens.Select(token => token with { Time=ScaleTime(token.Time) })] })] })] };
        var tree=original.LyricPresentation;
        var project=original with { Lyrics=lyrics,Composition=original.Composition with
            { CanvasWidth=Width,CanvasHeight=Height,FrameRate=new(fps,1),OutputDurationUs=5_700_000 },
            LyricStyle=original.LyricStyle with { FontSizePx=22 },LyricPresentation=tree with
            { Programs=[..tree.Programs.Select(program => program with { LeadIns=[..program.LeadIns.Select(cue => cue with { DurationUs=cue.DurationUs/10 })] })],
              Nodes=[..tree.Nodes.Select(node => node.Content is CountdownSequenceContent sequence ? node with
                { Content=sequence with { Appearance=sequence.Appearance with { Typography=sequence.Appearance.Typography with { FontSizePx=16 } } } } : node)] } };
        using SkiaRenderPipeline pipeline=new();
        var exporter=new VideoExporter(pipeline,new FfmpegMediaProbe(),new FfmpegMediaEncoder());
        string output=Path.Combine(_directory.Path,"lead-in-"+fps+".mp4");
        await exporter.ExportAsync(project,new VideoExportOptions { OutputPath=output,IncludeAudio=false });
        var snapshot=ProjectSnapshot.FromProject(project);
        foreach (double seconds in new double[] {.95,1.0,1.15,1.21,3.72,3.81,4.01,4.65})
        {
            int index=(int)Math.Round(seconds*fps); long at=index*1_000_000L/fps;
            using var preview=pipeline.Render(RenderRequest.At(snapshot,at));
            var scene=pipeline.GetSceneFrame(snapshot,at);
            string path=Path.Combine(_directory.Path,"lead-in-"+fps+"-"+index+".png");
            await ExtractFrameAsync(output,path,index); using var bitmap=SKBitmap.Decode(path)!;
            int expected=0,matching=0;
            for (int y=0;y<Height;y++) for (int x=0;x<Width;x++)
            {
                int offset=y*preview.Stride+x*4; var bytes=preview.PixelMemory.Span;
                if (bytes[offset+2]<60) continue; expected++; var actual=bitmap.GetPixel(x,y);
                if (Math.Abs(actual.Red-bytes[offset+2])+Math.Abs(actual.Green-bytes[offset+1])+Math.Abs(actual.Blue-bytes[offset])<100) matching++;
            }
            Assert.True(expected>40); Assert.True(matching>expected*.9,$"{fps}fps 帧 {index}: {matching}/{expected}");
            foreach (var mark in scene.TextMarks)
            {
                int markExpected=0,markMatching=0;
                for (int y=0;y<Height;y++) for (int x=0;x<Width;x++)
                {
                    int offset=y*preview.Stride+x*4; var bytes=preview.PixelMemory.Span;
                    if (bytes[offset+2]<70 || !mark.Geometry.Contains(new(x+.5,y+.5))) continue;
                    markExpected++; var actual=bitmap.GetPixel(x,y);
                    if (Math.Abs(actual.Red-bytes[offset+2])+Math.Abs(actual.Green-bytes[offset+1])+Math.Abs(actual.Blue-bytes[offset])<100) markMatching++;
                }
                Assert.True(markExpected>10); Assert.True(markMatching>markExpected*.9,$"标记 {mark.NodeId} 没有进入 {fps}fps 成片");
            }
        }
    }

    /// <summary>用 FFmpeg 抽出指定帧。</summary>
    private static async Task ExtractFrameAsync(string videoPath, string framePath, int frameIndex)
    {
        ProcessStartInfo startInfo = new("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (string argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error",
                     "-i", videoPath,
                     "-vf", string.Create(CultureInfo.InvariantCulture, $"select=eq(n\\,{frameIndex})"),
                     "-frames:v", "1",
                     "-y", framePath,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 ffmpeg。");

        string errors = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        Assert.True(process.ExitCode == 0, $"抽帧失败：{errors}");
    }

    private static LyricsDocument CreateLyrics()
    {
        long durationUs = (long)(DurationSeconds * 1_000_000d);

        return LyricsDocument.Create(
        [
            LyricLine.Create(
                "l1",
                "夜空中最亮的星",
                new TimeRange(0, durationUs),
                TimingStatus.Edited,
                [
                    LyricToken.Create(
                        "t1",
                        "夜空中最亮的星",
                        new TimeRange(0, durationUs),
                        TimingStatus.Edited),
                ]),
        ]);
    }
}
