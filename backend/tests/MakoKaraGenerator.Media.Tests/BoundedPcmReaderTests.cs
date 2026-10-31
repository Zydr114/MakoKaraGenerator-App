using System.Text.Json;

namespace MakoKaraGenerator.Media.Tests;

public sealed class BoundedPcmReaderTests
{
    [Fact]
    public async Task 巨型无换行stderr只保留有限诊断但完整PCM回调仍正确()
    {
        if (!OperatingSystem.IsLinux()) return;
        using TemporaryDirectory directory = new();
        string executable = await ExecutableAsync(directory, """
            import os,struct,sys
            sys.stderr.write('x'*(4*1024*1024));sys.stderr.flush()
            sys.stdout.buffer.write(struct.pack('<fff',.25,-.5,1.25));sys.stdout.buffer.flush()
            sys.exit(7)
            """);
        List<float> samples = [];
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        IOException failure = await Assert.ThrowsAsync<IOException>(() => new FfmpegPcmReader(executable)
            .ReadMonoSamplesAtRateAsync("owned-unused.wav", 48_000, block =>
            { samples.AddRange(block.ToArray()); return ValueTask.CompletedTask; }, deadline.Token));
        Assert.Equal(new float[] { .25f, -.5f, 1.25f }, samples);
        Assert.True(failure.Message.Length < 34_000, $"stderr retention must stay bounded; actual={failure.Message.Length}");
        Assert.Contains("[truncated]", failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 消费者失败和取消保持原原因并等实际解码进程退出(bool cancel)
    {
        if (!OperatingSystem.IsLinux()) return;
        using TemporaryDirectory directory = new();
        string pidFile = directory.FilePath("owned-pid");
        string executable = await ExecutableAsync(directory, "import os,struct,sys,time\n" +
            "open(" + JsonSerializer.Serialize(pidFile) + ", 'w').write(str(os.getpid()))\n" +
            "sys.stdout.buffer.write(struct.pack('<f',.25));sys.stdout.buffer.flush()\ntime.sleep(30)\n");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(8));
        Exception? failure = await Record.ExceptionAsync(() => new FfmpegPcmReader(executable).ReadMonoSamplesAtRateAsync(
            "owned-unused.wav", 48_000, _ =>
            {
                if (!cancel) throw new IOException("primary PCM consumer failure");
                deadline.Cancel(); return ValueTask.CompletedTask;
            }, deadline.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.Equal("primary PCM consumer failure", Assert.IsType<IOException>(failure).Message);
        int pid = int.Parse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(Directory.Exists("/proc/" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("wav")]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("aac")]
    [InlineData("mov")]
    [InlineData("ogg")]
    public async Task 默认解码参数完整保持而受信入口明确格式严格解码唯一音流和本地协议(string format)
    {
        if (!OperatingSystem.IsLinux()) return;
        using TemporaryDirectory directory = new();
        string argumentFile = directory.FilePath("arguments.json");
        string input = directory.FilePath("owned.input");
        if (format == "mov") await File.WriteAllBytesAsync(input, [0, 0, 0, 8, (byte)'m', (byte)'d', (byte)'a', (byte)'t']);
        else WavWriter.Write(input, [.25f], 48_000);
        string executable = await ExecutableAsync(directory, "import json,struct,sys\n" +
            "json.dump(sys.argv[1:],open(" + JsonSerializer.Serialize(argumentFile) + ", 'w'))\n" +
            "sys.stdout.buffer.write(struct.pack('<f',.25))\n");
        FfmpegPcmReader reader = new(executable);
        string[] expected = ["-v", "error", "-i", input, "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", "-acodec", "pcm_f32le", "-"];
        Assert.Equal(1L, await reader.ReadMonoSamplesAtRateAsync(input, 48_000, _ => ValueTask.CompletedTask));
        Assert.Equal(expected, JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(argumentFile)));
        Assert.Equal(1L, await reader.ReadMonoSamplesAtRateAsync(input, 48_000, format, _ => ValueTask.CompletedTask));
        Assert.Equal(expected[..2].Concat(["-xerror", "-err_detect", "explode", "-format_whitelist", "wav,mp3,flac,aac,mov,ogg",
                "-protocol_whitelist", "file,pipe", "-f", format])
            .Concat(format == "mov" ? ["-enable_drefs", "0", "-use_absolute_path", "0"] : Array.Empty<string>())
            .Concat(expected[2..4]).Concat(["-map", "0:a:0"]).Concat(expected[4..]),
            JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(argumentFile)));
        File.Delete(argumentFile);
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadMonoSamplesAtRateAsync(input, 48_000,
            "hls", _ => ValueTask.CompletedTask));
        Assert.False(File.Exists(argumentFile));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task 跨读取块float样本完整恢复而残缺EOF不能成功(int tailBytes)
    {
        if (!OperatingSystem.IsLinux()) return;
        using TemporaryDirectory directory = new();
        string executable = await ExecutableAsync(directory, "import struct,sys,time\n" +
            "data=struct.pack('<fff',.25,-.5,1.25)+b'x'*" + tailBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" +
            "for i in range(0,len(data),3):\n sys.stdout.buffer.write(data[i:i+3]);sys.stdout.buffer.flush();time.sleep(.005)\n");
        List<float> values = [];
        Task<long> operation = new FfmpegPcmReader(executable).ReadMonoSamplesAtRateAsync("owned-unused.wav", 48_000, "wav",
            block => { values.AddRange(block.ToArray()); return ValueTask.CompletedTask; });
        if (tailBytes == 0) Assert.Equal(3L, await operation);
        else Assert.Contains("不完整的 float", (await Assert.ThrowsAsync<IOException>(() => operation)).Message);
        Assert.Equal(new float[] { .25f, -.5f, 1.25f }, values);
    }

    private static async Task<string> ExecutableAsync(TemporaryDirectory directory, string source)
    {
        string path = directory.FilePath("owned-decoder");
        await File.WriteAllTextAsync(path, "#!/usr/bin/env python3\n" + source);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
