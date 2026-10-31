using System.Diagnostics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class PersistedMediaRenderTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 内容地址媒体在移除原图或视频后仍由真实解码器绘制并保持透明输出规则(bool video)
    {
        using TemporaryDirectory temp = new(); string path = temp.FilePath(video ? "原背景.mp4" : "原背景.png");
        if (video)
        {
            var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
            foreach (string argument in new[] { "-v", "error", "-f", "lavfi", "-i", "color=c=0x28d850:size=64x48:rate=10:duration=1", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-y", path }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; string error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, error);
        }
        else
        {
            using SKBitmap bitmap = new(64, 48); bitmap.Erase(new(40, 216, 80)); using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(path, data.ToArray());
        }
        var project = Project.Create("真实受管背景", Composition.Create(320, 180, FrameRate.Fps30),
            background: video ? BackgroundSource.ForVideo(path, BackgroundFit.Cover) : BackgroundSource.ForImage(path, BackgroundFit.Cover));
        string directory = temp.FilePath("project"); ProjectStore.Save(project, directory); File.Delete(path);
        var restored = ProjectStore.Load(directory); Assert.True(restored.Loaded, restored.Report.ToSummary()); var snapshot = ProjectSnapshot.FromProject(restored.Project!);
        Assert.EndsWith(".media", restored.Project!.Background.Path); Assert.Equal(Path.GetFileName(path), restored.Project.Background.Media!.OriginalName);
        using FfmpegVideoFrameProvider provider = new(); using var renderer = new SkiaRenderPipeline(null, new BackgroundFrameSourceResolver(provider));
        using var frame = renderer.Render(RenderRequest.At(snapshot, 500000)); Assert.True(frame.TryGetPixel(160, 90, out var pixel));
        Assert.InRange(pixel.Red, 37, 43); Assert.InRange(pixel.Green, 213, 219); Assert.InRange(pixel.Blue, 77, 83); Assert.Equal(255, pixel.Alpha);
        using var transparent = renderer.Render(RenderRequest.At(snapshot, 500000) with { TransparentBackground = true });
        Assert.True(transparent.TryGetPixel(160, 90, out var clear)); Assert.Equal(0, clear.Alpha);
    }
}
