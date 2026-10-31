using System.Diagnostics;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media.Tests;

public sealed class VideoFrameCacheTests
{
    [Fact]
    public async Task 真实30fps背景以60fps取前六次只启动一个解码器且像素正确()
    {
        using var temp = new TemporaryDirectory();
        string input = temp.FilePath("thirty.mp4");
        var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=30:duration=3",
            "-pix_fmt", "yuv420p", "-c:v", "libx264", "-y", input }) info.ArgumentList.Add(arg);
        using (var generated = Process.Start(info)!)
        {
            using var reader = generated.StandardError;
            string errors = await reader.ReadToEndAsync();
            await generated.WaitForExitAsync(); Assert.True(generated.ExitCode == 0, errors);
        }
        await File.WriteAllTextAsync(input + ".forward", "ffmpeg");
        string worker = Path.Combine(AppContext.BaseDirectory, "media-worker",
            "MakoKaraGenerator.Media.Worker" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        using var counted = new FfmpegVideoFrameProvider(worker);
        using var source = counted.Open(input, out _)!;
        using var reference = new FfmpegVideoFrameProvider();
        using var expected = reference.Open(input, out _)!;
        List<byte[]> frames = [];
        foreach (long time in new[] { 0L, 50_000L, 83_333L })
        { Assert.True(expected.TryGetFrame(time, out var frame), expected.LastError); frames.Add([.. frame!.Pixels]); }
        Assert.NotEqual(frames[0], frames[1]);
        long[] requests = [0, 16_666, 33_333, 50_000, 66_666, 83_333];
        int[] indices = [0, 0, 0, 1, 1, 2];
        for (int index = 0; index < requests.Length; index++)
        { Assert.True(source.TryGetFrame(requests[index], out var frame), source.LastError); Assert.Equal(frames[indices[index]], frame!.Pixels); }
        Assert.Single(File.ReadAllLines(input + ".decoder.starts"));
        // Reach the last frame by genuine sequential requests; freeze then reuses it.
        foreach (long time in new[] { 900_000L, 1_800_000L, 2_700_000L, source.DurationUs - 1 })
            Assert.True(source.TryGetFrame(time, out _), source.LastError);
        Assert.Single(File.ReadAllLines(input + ".decoder.starts"));
        byte[] last;
        Assert.True(source.TryGetFrame(source.DurationUs - 1, out var frozen)); last = [.. frozen!.Pixels];
        for (int index = 0; index < 4; index++)
        { Assert.True(source.TryGetFrame(source.DurationUs - 1, out var repeated)); Assert.Equal(last, repeated!.Pixels); }
        Assert.Single(File.ReadAllLines(input + ".decoder.starts"));
        source.Dispose(); source.Dispose();
    }
}
