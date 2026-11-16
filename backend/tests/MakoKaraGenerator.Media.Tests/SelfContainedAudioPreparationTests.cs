using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CliWrap;
using static MakoKaraGenerator.Media.Tests.SelfContainedMovInputValidatorTests;

namespace MakoKaraGenerator.Media.Tests;

public sealed class SelfContainedAudioPreparationTests
{
    [Theory]
    [InlineData("wav", "pcm_s16le", 44100, 1)]
    [InlineData("wav", "pcm_f32le", 48000, 2)]
    [InlineData("mp3", "libmp3lame", 44100, 2)]
    [InlineData("flac", "flac", 48000, 2)]
    [InlineData("adts", "aac", 44100, 1)]
    [InlineData("ipod", "aac", 48000, 2)]
    [InlineData("ipod", "alac", 44100, 1)]
    [InlineData("ogg", "libvorbis", 48000, 2)]
    [InlineData("ogg", "libopus", 48000, 1)]
    public async Task 九种普通真实输入保完整probe源采样PCM及所有金字塔层(string muxer, string codec, int rate, int channels)
    {
        using TemporaryDirectory temp = new(); using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        string path = await EncodeAsync(temp, muxer, codec, rate, channels, deadline.Token);
        AudioStreamInfo? ordinaryInfo = await new FfmpegMediaProbe().ProbeAsync(path, deadline.Token);
        AudioStreamInfo? restrictedInfo = await new FfmpegMediaProbe().ProbeSelfContainedAsync(path, deadline.Token);
        Assert.Equal(ordinaryInfo, restrictedInfo); Assert.Equal(rate, restrictedInfo!.SampleRate); Assert.Equal(channels, restrictedInfo.Channels);
        using MemoryStream ordinary = new(); using MemoryStream restricted = new(); FfmpegPcmReader reader = new();
        long expected = await reader.ReadMonoSamplesAsync(path, block => Append(ordinary, block), deadline.Token);
        long actual = await reader.ReadSelfContainedSamplesAsync(path, SelfContainedAudioInput.DetectedFormat(restrictedInfo.ContainerName),
            block => Append(restricted, block), deadline.Token);
        Assert.True(expected > 0); Assert.Equal(expected, actual); Assert.Equal(ordinary.ToArray(), restricted.ToArray());
        LoadedWaveform? original = await AudioWaveformLoader.CreateDefault().LoadAsync(path, cancellationToken: deadline.Token);
        LoadedWaveform? secured = await AudioWaveformLoader.CreateForSelfContainedFiles().LoadAsync(path, cancellationToken: deadline.Token);
        Assert.Equal(original!.Info, secured!.Info); Assert.Equal(expected, secured.Peaks.SampleCount);
        Assert.Equal(original.Peaks.SampleRate, secured.Peaks.SampleRate); Assert.Equal(rate, secured.Peaks.SampleRate);
        Assert.Equal(original.Peaks.DurationUs, secured.Peaks.DurationUs); Assert.Equal(original.Peaks.LevelCount, secured.Peaks.LevelCount);
        for (int level = 0; level < original.Peaks.LevelCount; level++)
        {
            Assert.Equal(original.Peaks.BucketCount(level), secured.Peaks.BucketCount(level));
            Assert.Equal(original.Peaks.BucketSamples(level), secured.Peaks.BucketSamples(level));
            for (int bucket = 0; bucket < original.Peaks.BucketCount(level); bucket++)
                Assert.Equal(original.Peaks.GetBucketPeak(level, bucket), secured.Peaks.GetBucketPeak(level, bucket));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 自有concat和HLS默认会跟随而Backend入口零候选拒绝(bool hls)
    {
        using TemporaryDirectory temp = new(); using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        string target = await EncodeAsync(temp, hls ? "adts" : "wav", hls ? "aac" : "pcm_s16le", 44100, 1, deadline.Token);
        string reference = temp.FilePath(hls ? "owned.aac" : "owned.wav"); File.Copy(target, reference);
        string manifest = temp.FilePath(hls ? "owned.m3u8" : "owned-concat.wav");
        await File.WriteAllTextAsync(manifest, hls
            ? "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXTINF:0.213,\nowned.aac\n#EXT-X-ENDLIST\n"
            : "ffconcat version 1.0\nfile owned.wav\n", deadline.Token);
        Assert.True((await AudioWaveformLoader.CreateDefault().LoadAsync(manifest, cancellationToken: deadline.Token))!.Peaks.SampleCount > 0);
        await Assert.ThrowsAsync<IOException>(() => AudioWaveformLoader.CreateForSelfContainedFiles().LoadAsync(manifest, cancellationToken: deadline.Token));
    }

    [Fact]
    public async Task 实际MOV外部别名不因为native跳过轨道而当作正常音频()
    {
        using TemporaryDirectory temp = new(); using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        string original = await EncodeAsync(temp, "ipod", "aac", 48000, 2, deadline.Token);
        string referenced = temp.FilePath("owned-reference.m4a"); File.Copy(original, referenced);
        byte[] name = Encoding.ASCII.GetBytes(Path.GetFileName(referenced)); byte[] fullPath = Encoding.UTF8.GetBytes(referenced.Replace('\\', '/'));
        using MemoryStream body = new();
        body.Write(new byte[10 + 28 + 12]); body.WriteByte((byte)name.Length); body.Write(name); body.Write(new byte[63 - name.Length]);
        body.Write(new byte[16]); body.Write(new byte[] { 0, 1, 0, 1 }); body.Write(new byte[16]);
        byte[] pathHeader = new byte[4]; BinaryPrimitives.WriteUInt16BigEndian(pathHeader, 2); BinaryPrimitives.WriteUInt16BigEndian(pathHeader.AsSpan(2), (ushort)fullPath.Length);
        body.Write(pathHeader); body.Write(fullPath); if (fullPath.Length % 2 != 0) body.WriteByte(0); body.Write(new byte[] { 255, 255, 0, 0 });
        byte[] external = References(Entry("alis", 0, body.ToArray()));
        string movie = temp.FilePath("owned-reference-movie.m4a");
        await File.WriteAllBytesAsync(movie, ReplaceReferences(await File.ReadAllBytesAsync(original, deadline.Token), external), deadline.Token);
        using MemoryStream opened = new();
        CommandResult optedIn = await Cli.Wrap(TestMediaToolPaths.Ffmpeg).WithArguments(["-v", "error", "-enable_drefs", "1", "-use_absolute_path", "0",
            "-i", movie.Replace('\\', '/'), "-vn", "-ac", "1", "-f", "f32le", "-acodec", "pcm_f32le", "-"])
            .WithStandardOutputPipe(PipeTarget.ToStream(opened)).ExecuteAsync(deadline.Token);
        Assert.Equal(0, optedIn.ExitCode); Assert.True(opened.Length > 0);
        await Assert.ThrowsAsync<IOException>(() => AudioWaveformLoader.CreateForSelfContainedFiles(
            TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe).LoadAsync(movie, cancellationToken: deadline.Token));
    }

    [Fact]
    public async Task 外部声明在native前拒绝且实际MOV复扫未识别开场仍拒绝()
    {
        using TemporaryDirectory temp = new(); string marker = temp.FilePath("probe-args.json");
        string probe = await ExecutableAsync(temp, "probe", "import json,sys\njson.dump(sys.argv[1:],open(" + JsonSerializer.Serialize(marker) + ",'w'))\n" +
            "print('{\"streams\":[{\"codec_name\":\"aac\",\"sample_rate\":\"48000\",\"channels\":1}],\"format\":{\"format_name\":\"mov,mp4,m4a,3gp,3g2,mj2\"}}')\n");
        string input = temp.FilePath("owned.input"); byte[] references = Box("moov", References(Entry("url ", 0)));
        await File.WriteAllBytesAsync(input, references);
        await Assert.ThrowsAsync<IOException>(() => AudioWaveformLoader.CreateForSelfContainedFiles("never-start-decoder", probe).LoadAsync(input));
        Assert.False(File.Exists(marker));
        await File.WriteAllBytesAsync(input, [.. Box("zzzz", []), .. references]);
        await Assert.ThrowsAsync<IOException>(() => AudioWaveformLoader.CreateForSelfContainedFiles("never-start-decoder", probe).LoadAsync(input));
        Assert.False(File.Exists(marker));
        await File.WriteAllBytesAsync(input, [.. "not-a-box"u8.ToArray(), .. references]);
        await Assert.ThrowsAsync<IOException>(() => AudioWaveformLoader.CreateForSelfContainedFiles("never-start-decoder", probe).LoadAsync(input));
        Assert.True(File.Exists(marker));
    }

    [Theory]
    [InlineData("uuid", "dref")]
    [InlineData("uuid", "rmra")]
    [InlineData("junk", "dref")]
    [InlineData("junk", "rmra")]
    public async Task 未知前置合法box后的外部声明在native探测前明确拒绝(string prefix, string reference)
    {
        using TemporaryDirectory temp = new(); string marker = temp.FilePath("native-called");
        string probe = await ExecutableAsync(temp, "probe", "open(" + JsonSerializer.Serialize(marker) +
            ",'w').write('called')\nprint('{\"streams\":[],\"format\":{}}')\n");
        byte[] references = reference == "dref" ? References(Entry("url ", 0)) : Box("rmra", []);
        string input = temp.FilePath("owned.input");
        await File.WriteAllBytesAsync(input, [.. Box(prefix, prefix == "uuid" ? new byte[16] : []),
            .. Box("ftyp", "M4A \0\0\0\0M4A "u8.ToArray()), .. Box("moov", references)]);
        await Assert.ThrowsAsync<IOException>(() => AudioWaveformLoader.CreateForSelfContainedFiles("never-start-decoder", probe).LoadAsync(input));
        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData("wav")]
    [InlineData("mov")]
    public async Task 新策略参数明确而默认保持且非法enum不spawn(string format)
    {
        using TemporaryDirectory temp = new(); string probeArgs = temp.FilePath("probe.json"), readArgs = temp.FilePath("reader.json");
        string probe = await ExecutableAsync(temp, "probe", "import json,sys\njson.dump(sys.argv[1:],open(" + JsonSerializer.Serialize(probeArgs) + ",'w'))\n" +
            "print('{\"streams\":[{\"codec_name\":\"pcm_s16le\",\"sample_rate\":\"44100\",\"channels\":1}],\"format\":{\"format_name\":\"" + format + "\"}}')\n");
        string decoder = await ExecutableAsync(temp, "reader", "import json,struct,sys\njson.dump(sys.argv[1:],open(" + JsonSerializer.Serialize(readArgs) + ",'w'))\nsys.stdout.buffer.write(struct.pack('<f',.25))\n");
        string input = temp.FilePath("owned.input"); await File.WriteAllBytesAsync(input, Box("mdat", []));
        await new FfmpegMediaProbe(probe).ProbeAsync(input);
        string[] ordinaryProbe = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(probeArgs))!;
        Assert.DoesNotContain("-format_whitelist", ordinaryProbe); Assert.DoesNotContain("-enable_drefs", ordinaryProbe);
        await new FfmpegPcmReader(decoder).ReadMonoSamplesAsync(input, _ => ValueTask.CompletedTask);
        Assert.Equal(new[] { "-v", "error", "-i", input, "-vn", "-ac", "1", "-f", "f32le", "-acodec", "pcm_f32le", "-" },
            JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(readArgs)));
        Assert.Equal(1L, (await AudioWaveformLoader.CreateForSelfContainedFiles(decoder, probe).LoadAsync(input))!.Peaks.SampleCount);
        string[] actualProbe = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(probeArgs))!;
        string[] actualRead = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(readArgs))!;
        Assert.Equal(SelfContainedAudioInput.FormatWhitelist, actualProbe[Array.IndexOf(actualProbe, "-format_whitelist") + 1]);
        Assert.Equal("0", actualProbe[Array.IndexOf(actualProbe, "-enable_drefs") + 1]);
        Assert.Equal("0", actualProbe[Array.IndexOf(actualProbe, "-use_absolute_path") + 1]);
        Assert.Equal(format, actualRead[Array.IndexOf(actualRead, "-f") + 1]); Assert.Contains("file,pipe", actualRead);
        Assert.Equal("0:a:0", actualRead[Array.IndexOf(actualRead, "-map") + 1]); Assert.DoesNotContain("-ar", actualRead);
        Assert.DoesNotContain("-xerror", actualRead); Assert.DoesNotContain("-err_detect", actualRead);
        Assert.Equal(format == "mov", actualRead.Contains("-enable_drefs", StringComparer.Ordinal));
        File.Delete(readArgs);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new FfmpegPcmReader(decoder).ReadSelfContainedSamplesAsync(input,
            (SelfContainedAudioFormat)999, _ => ValueTask.CompletedTask)); Assert.False(File.Exists(readArgs));
    }

    [Fact]
    public async Task 新入口零实际样本拒绝但公有注入默认保持()
    {
        using TemporaryDirectory temp = new(); string path = temp.FilePath("empty.input"); await File.WriteAllBytesAsync(path, []);
        string probe = await ExecutableAsync(temp, "probe", "print('{\"streams\":[{\"sample_rate\":\"44100\",\"channels\":1}],\"format\":{\"format_name\":\"wav\"}}')\n");
        string decoder = await ExecutableAsync(temp, "reader", "pass\n");
        Assert.Equal(0L, (await new AudioWaveformLoader(new FfmpegMediaProbe(probe), new FfmpegPcmReader(decoder)).LoadAsync(path))!.Peaks.SampleCount);
        await Assert.ThrowsAsync<IOException>(() => AudioWaveformLoader.CreateForSelfContainedFiles(decoder, probe).LoadAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 新原采样率策略消费者失败和取消保主因并排水真实子进程(bool cancel)
    {
        using TemporaryDirectory temp = new(); string pidFile = temp.FilePath("pid");
        string decoder = await ExecutableAsync(temp, "reader", "import os,struct,sys,time\nopen(" + JsonSerializer.Serialize(pidFile) +
            ",'w').write(str(os.getpid()))\nsys.stdout.buffer.write(struct.pack('<f',.25));sys.stdout.buffer.flush()\ntime.sleep(30)\n");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(8));
        Exception? error = await Record.ExceptionAsync(() => new FfmpegPcmReader(decoder).ReadSelfContainedSamplesAsync("owned-unused.input",
            SelfContainedAudioFormat.Wave, _ => { if (!cancel) throw new IOException("owned-primary-consumer"); deadline.Cancel(); return ValueTask.CompletedTask; }, deadline.Token));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal("owned-primary-consumer", Assert.IsType<IOException>(error).Message);
        int pid = int.Parse(await File.ReadAllTextAsync(pidFile), CultureInfo.InvariantCulture);
        for (int i = 0; i < 50 && IsProcessAlive(pid); i++) await Task.Delay(20);
        Assert.False(IsProcessAlive(pid));
    }

    private static ValueTask Append(MemoryStream output, ReadOnlyMemory<float> samples)
    { output.Write(WavWriter.ToFloat32LittleEndian(samples.Span)); return ValueTask.CompletedTask; }

    private static async Task<string> EncodeAsync(TemporaryDirectory temp, string muxer, string codec, int rate, int channels, CancellationToken token)
    {
        string source = temp.FilePath("owned-source.wav"); WavWriter.Write(source, WavWriter.BuildSegmentedTone(rate, (.213, .7f)), rate);
        string output = temp.FilePath("owned-codec.input"); StringBuilder errors = new();
        CommandResult result = await Cli.Wrap(TestMediaToolPaths.Ffmpeg).WithArguments(["-v", "error", "-i", source, "-ac", channels.ToString(CultureInfo.InvariantCulture),
            "-c:a", codec, "-f", muxer, output]).WithValidation(CommandResultValidation.None).WithStandardErrorPipe(PipeTarget.ToStringBuilder(errors)).ExecuteAsync(token);
        Assert.True(result.ExitCode == 0, errors.ToString()); return output;
    }

    private static byte[] ReplaceReferences(byte[] source, byte[] references)
    {
        using MemoryStream output = new(); int offset = 0;
        while (offset < source.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(offset)));
            string type = Encoding.ASCII.GetString(source, offset + 4, 4); byte[] body = source[(offset + 8)..(offset + length)];
            output.Write(type switch
            {
                "dref" => references,
                "moov" or "trak" or "mdia" or "minf" or "dinf" => Box(type, ReplaceReferences(body, references)),
                "mdat" => Box(type, new byte[body.Length]),
                _ => source[offset..(offset + length)],
            }); offset += length;
        }
        return output.ToArray();
    }

    private static async Task<string> ExecutableAsync(TemporaryDirectory temp, string name, string source)
    {
        if (OperatingSystem.IsWindows())
        {
            string pyPath = temp.FilePath(name + ".py");
            await File.WriteAllTextAsync(pyPath, source);
            string cmdPath = temp.FilePath(name + ".cmd");
            await File.WriteAllTextAsync(cmdPath, string.Create(CultureInfo.InvariantCulture, $"@echo off\r\n\"{TestMediaToolPaths.Python}\" \"{pyPath}\" %*\r\n"));
            return cmdPath;
        }
        string path = temp.FilePath(name); await File.WriteAllTextAsync(path, "#!/usr/bin/env python3\n" + source);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
