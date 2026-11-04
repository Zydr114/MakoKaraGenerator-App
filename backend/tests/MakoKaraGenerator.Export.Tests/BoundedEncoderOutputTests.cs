using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Export.Tests;

public sealed class BoundedEncoderOutputTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 队列满时等待并在取消或编码失败后释放所有帧(bool cancel)
    {
        if (!OperatingSystem.IsLinux()) return;
        using TemporaryDirectory directory = new();
        string executable = directory.FilePath("blocked-encoder");
        string release = directory.FilePath("release");
        await File.WriteAllTextAsync(executable, $"""
            #!/usr/bin/env python3
            import os, time
            while not os.path.exists({System.Text.Json.JsonSerializer.Serialize(release)}):
                time.sleep(0.01)
            raise SystemExit(17)
            """);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        IVideoEncodingSession session = await new FfmpegMediaEncoder(executable).StartAsync(new()
        {
            OutputPath = directory.FilePath("candidate.mp4"), Width = 1024, Height = 1024,
            FrameRate = FrameRate.Fps30, TotalFrames = 4,
        }, deadline.Token);
        var target = new RenderTarget { PixelWidth = 1024, PixelHeight = 1024 };
        List<RenderFrame> frames = [];
        try
        {
            for (int i = 0; i < 4; i++) frames.Add(new RenderFrame(new byte[1024 * 1024 * 4], target, PixelFormat.Bgra8888Premultiplied, i));
            for (int i = 0; i < 3; i++) await session.WriteFrameAsync(frames[i], deadline.Token);
            using CancellationTokenSource writeCancellation = new();
            Task waiting = session.WriteFrameAsync(frames[3], writeCancellation.Token);
            Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(200, deadline.Token)));
            if (cancel)
            {
                writeCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(deadline.Token));
            }
            else
            {
                await File.WriteAllTextAsync(release, "exit", deadline.Token);
                await Assert.ThrowsAsync<IOException>(() => waiting.WaitAsync(deadline.Token));
            }
        }
        finally
        {
            await session.DisposeAsync();
        }
        foreach (var frame in frames) Assert.Throws<ObjectDisposedException>(() => frame.PixelMemory);
    }

    [Fact]
    public async Task 完成编码按顺序写出并释放所有帧()
    {
        if (!OperatingSystem.IsLinux()) return;
        using TemporaryDirectory directory = new();
        string executable = directory.FilePath("ordered-encoder");
        await File.WriteAllTextAsync(executable, "#!/usr/bin/env python3\nimport sys\nopen(sys.argv[-1], 'wb').write(sys.stdin.buffer.read())\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await using IVideoEncodingSession session = await new FfmpegMediaEncoder(executable).StartAsync(new()
        {
            OutputPath = directory.FilePath("candidate.mp4"), Width = 2, Height = 2,
            FrameRate = FrameRate.Fps30, TotalFrames = 4,
        }, deadline.Token);
        var target = new RenderTarget { PixelWidth = 2, PixelHeight = 2 };
        List<RenderFrame> frames = [];
        for (byte i = 1; i <= 4; i++)
        {
            var frame = new RenderFrame(Enumerable.Repeat(i, 16).ToArray(), target, PixelFormat.Bgra8888Premultiplied, 0);
            frames.Add(frame);
            await session.WriteFrameAsync(frame, deadline.Token);
        }
        var result = await session.CompleteAsync(deadline.Token);
        using (result.Artifact)
        {
            Assert.Equal(4, result.FramesWritten);
            byte[] expected = Enumerable.Range(1, 4).SelectMany(i => Enumerable.Repeat((byte)i, 16)).ToArray();
            Assert.Equal(expected, await File.ReadAllBytesAsync(result.Artifact.TemporaryPath, deadline.Token));
            foreach (var frame in frames) Assert.Throws<ObjectDisposedException>(() => frame.PixelMemory);
        }
    }

    [Fact]
    public async Task 已释放会话拒绝帧时仍释放帧()
    {
        if (!OperatingSystem.IsLinux()) return;
        using TemporaryDirectory directory = new();
        string executable = directory.FilePath("controlled-encoder");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\ncat >/dev/null\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        IVideoEncodingSession session = await new FfmpegMediaEncoder(executable).StartAsync(new()
        {
            OutputPath = directory.FilePath("candidate.mp4"), Width = 2, Height = 2,
            FrameRate = FrameRate.Fps30, TotalFrames = 1,
        });
        await session.DisposeAsync();
        RenderFrame frame = new(new byte[16], new RenderTarget { PixelWidth = 2, PixelHeight = 2 }, PixelFormat.Bgra8888Premultiplied, 0);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.WriteFrameAsync(frame));
        Assert.Throws<ObjectDisposedException>(() => frame.PixelMemory);
    }

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
