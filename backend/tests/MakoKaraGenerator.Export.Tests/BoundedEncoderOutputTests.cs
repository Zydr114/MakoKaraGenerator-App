using System.Globalization;
using System.Text.Json;
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
        using TemporaryDirectory directory = new();
        string release = directory.FilePath("release");
        string executable = CreatePythonWrapper(directory, "blocked-encoder", $$"""
            import os, time
            while not os.path.exists({{JsonSerializer.Serialize(release)}}):
                time.sleep(0.01)
            raise SystemExit(17)
            """);
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
        using TemporaryDirectory directory = new();
        string executable = CreatePythonWrapper(directory, "ordered-encoder", """
            import sys
            with open(sys.argv[-1], 'wb') as out:
                out.write(sys.stdin.buffer.read())
            """);
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
        using TemporaryDirectory directory = new();
        string executable = CreatePythonWrapper(directory, "controlled-encoder", """
            import sys
            sys.stdin.buffer.read()
            """);
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
        using TemporaryDirectory directory = new();
        string executable = CreatePythonWrapper(directory, "controlled-encoder", """
            import sys
            sys.stderr.write('x' * (4*1024*1024))
            sys.stderr.flush()
            data=sys.stdin.buffer.read()
            with open(sys.argv[-1], 'wb') as out:
                out.write(b'x')
            print('drop_frames=0')
            """);
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

    [Fact]
    public async Task 编码器零退出码超限写盘绝不静默截断成功且清理临时残片()
    {
        using TemporaryDirectory directory = new();
        // Simulate FFmpeg hitting -fs and exiting gracefully with exit code 0 while producing maxBytes + 1.
        string executable = CreatePythonWrapper(directory, "fs-zero-exit-encoder", """
            import sys
            fs_index = sys.argv.index('-fs')
            fs_value = int(sys.argv[fs_index + 1])
            assert fs_value == 1024 + 65536
            sys.stdin.buffer.read()
            with open(sys.argv[-1], 'wb') as out:
                out.write(b'A' * 1025)
            sys.exit(0)
            """);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await using IVideoEncodingSession session = await new FfmpegMediaEncoder(executable).StartAsync(new()
        {
            OutputPath = directory.FilePath("candidate.mp4"),
            Width = 2,
            Height = 2,
            FrameRate = FrameRate.Fps30,
            TotalFrames = 1,
            MaximumOutputBytes = 1024,
        }, deadline.Token);

        RenderFrame frame = new(new byte[16], new RenderTarget { PixelWidth = 2, PixelHeight = 2 }, PixelFormat.Bgra8888Premultiplied, 0);
        await session.WriteFrameAsync(frame, deadline.Token);

        OutputSizeLimitExceededException error = await Assert.ThrowsAsync<OutputSizeLimitExceededException>(
            () => session.CompleteAsync(deadline.Token));
        Assert.Equal(1024, error.MaximumBytes);
        Assert.Equal(1025, error.ActualBytes);
        Assert.False(File.Exists(directory.FilePath("candidate.mp4")));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.encoding.*"));
    }

    [Fact]
    public async Task 运行期流式监控通过共享句柄读取真实长度超限即杀并清理残片()
    {
        using TemporaryDirectory directory = new();
        // Keep the output handle open without closing or exiting; FileStream.Length (GetFileSizeEx) must detect > maxBytes.
        string executable = CreatePythonWrapper(directory, "streaming-overrun-encoder", """
            import sys, time
            with open(sys.argv[-1], 'wb') as out:
                out.write(b'B' * 4096)
                out.flush()
                time.sleep(30)
            """);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await using IVideoEncodingSession session = await new FfmpegMediaEncoder(executable).StartAsync(new()
        {
            OutputPath = directory.FilePath("candidate.mp4"),
            Width = 2,
            Height = 2,
            FrameRate = FrameRate.Fps30,
            TotalFrames = 1,
            MaximumOutputBytes = 1024,
        }, deadline.Token);

        RenderFrame frame = new(new byte[16], new RenderTarget { PixelWidth = 2, PixelHeight = 2 }, PixelFormat.Bgra8888Premultiplied, 0);
        try
        {
            await session.WriteFrameAsync(frame, deadline.Token);
            await Assert.ThrowsAsync<OutputSizeLimitExceededException>(() => session.CompleteAsync(deadline.Token));
        }
        catch (OutputSizeLimitExceededException)
        {
            // Also acceptable if detected during WriteFrameAsync.
        }
        await session.DisposeAsync();
        Assert.False(File.Exists(directory.FilePath("candidate.mp4")));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.encoding.*"));
    }

    [Fact]
    public async Task 真实FFmpeg超限触发防截断报错并彻底清理临时残片()
    {
        using TemporaryDirectory directory = new();
        string output = directory.FilePath("overrun.mp4");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await using IVideoEncodingSession session = await new FfmpegMediaEncoder(TestMediaToolPaths.Ffmpeg).StartAsync(new()
        {
            OutputPath = output,
            Width = 128,
            Height = 128,
            FrameRate = FrameRate.Fps30,
            TotalFrames = 30,
            MaximumOutputBytes = 4096,
        }, deadline.Token);

        var target = new RenderTarget { PixelWidth = 128, PixelHeight = 128 };
        Random rng = new(42);
        try
        {
            for (int i = 0; i < 30; i++)
            {
                byte[] pixels = new byte[128 * 128 * 4];
                rng.NextBytes(pixels);
                for (int p = 3; p < pixels.Length; p += 4) pixels[p] = 255;
                await session.WriteFrameAsync(new RenderFrame(pixels, target, PixelFormat.Bgra8888Premultiplied, i * 33_333L), deadline.Token);
            }
            await Assert.ThrowsAsync<OutputSizeLimitExceededException>(() => session.CompleteAsync(deadline.Token));
        }
        catch (OutputSizeLimitExceededException)
        {
            // Exceedance detected mid-stream while feeding frames.
        }

        await session.DisposeAsync();
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.encoding*"));
    }

    private static string CreatePythonWrapper(TemporaryDirectory directory, string name, string pythonSource)
    {
        if (OperatingSystem.IsWindows())
        {
            string pyPath = directory.FilePath(name + ".py");
            File.WriteAllText(pyPath, pythonSource);
            string cmdPath = directory.FilePath(name + ".cmd");
            File.WriteAllText(cmdPath, string.Create(CultureInfo.InvariantCulture, $"@echo off\r\n\"{TestMediaToolPaths.Python}\" \"{pyPath}\" %*\r\n"));
            return cmdPath;
        }

        string scriptPath = directory.FilePath(name);
        File.WriteAllText(scriptPath, "#!/usr/bin/env python3\n" + pythonSource.Replace("\r\n", "\n", StringComparison.Ordinal));
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return scriptPath;
    }
}
