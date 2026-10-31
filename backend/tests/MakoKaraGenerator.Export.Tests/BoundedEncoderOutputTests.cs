using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Export.Tests;

public sealed class BoundedEncoderOutputTests
{
    [Fact]
    public async Task 无换行编码诊断保持有界且排水后原帧所有权已释放()
    {
        if (!OperatingSystem.IsLinux()) return;
        using TemporaryDirectory directory = new();
        string executable = directory.FilePath("controlled-encoder");
        await File.WriteAllTextAsync(executable, """
            #!/usr/bin/env python3
            import sys
            sys.stderr.write('x' * (4*1024*1024))
            sys.stderr.flush()
            data=sys.stdin.buffer.read()
            open(sys.argv[-1], 'wb').write(b'x')
            print('drop_frames=0')
            """);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await using IVideoEncodingSession session = await new FfmpegMediaEncoder(executable).StartAsync(new()
        {
            OutputPath = directory.FilePath("candidate.mp4"), Width = 2, Height = 2,
            FrameRate = FrameRate.Fps30, TotalFrames = 1,
        }, deadline.Token);
        RenderFrame frame = new(new byte[16], new RenderTarget { PixelWidth = 2, PixelHeight = 2 }, PixelFormat.Bgra8888Premultiplied, 0);
        await session.WriteFrameAsync(frame, deadline.Token);
        VideoEncodingResult result = await session.CompleteAsync(deadline.Token);
        using (result.Artifact)
        {
            Assert.Equal(1, result.FramesWritten);
            Assert.All(result.Log.Skip(1), line => Assert.True(line.Length <= 2048));
            Assert.Contains(result.Log, line => line.EndsWith(" [truncated]", StringComparison.Ordinal));
            Assert.Throws<ObjectDisposedException>(() => frame.PixelMemory);
            Assert.False(File.Exists(directory.FilePath("candidate.mp4")));
        }
    }
}
