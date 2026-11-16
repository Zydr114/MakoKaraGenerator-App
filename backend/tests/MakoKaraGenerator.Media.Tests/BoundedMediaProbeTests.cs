using System.Globalization;
using System.Text.Json;

namespace MakoKaraGenerator.Media.Tests;

public sealed class BoundedMediaProbeTests
{
    [Theory]
    [InlineData("wav", "pcm_s16le")]
    [InlineData("mp3", "mp3")]
    [InlineData("flac", "flac")]
    [InlineData("aac", "aac")]
    [InlineData("mov", "aac")]
    [InlineData("ogg", "vorbis")]
    public async Task 默认音轨探测参数保持而受限六格式返回实际音流信息(string format, string codec)
    {
        using TemporaryDirectory directory = new();
        string argumentFile = directory.FilePath("audio-arguments.json");
        string json = JsonSerializer.Serialize(new
        {
            streams = new[] { new { codec_name = codec, sample_rate = "44100", channels = 2, duration = "1.013" } },
            format = new { duration = "1.013", format_name = format },
        });
        string script = "import sys,json\njson.dump(sys.argv[1:],open(" +
            JsonSerializer.Serialize(argumentFile) + ", 'w'))\nprint(" + JsonSerializer.Serialize(json) + ")\n";
        FfmpegMediaProbe probe = new(WriteExecutable(directory, script, "audio-args-probe"));
        string input = directory.FilePath("owned.input");
        if (format == "mov") await File.WriteAllBytesAsync(input, [0, 0, 0, 8, (byte)'m', (byte)'d', (byte)'a', (byte)'t']);
        else WavWriter.Write(input, [.25f], 44_100);
        string[] expected = ["-v", "error", "-select_streams", "a:0", "-show_entries",
            "stream=codec_name,sample_rate,channels,duration", "-show_entries", "format=duration,format_name", "-of", "json", input];
        AudioStreamInfo? ordinary = await probe.ProbeAsync(input);
        Assert.Equal(expected, JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(argumentFile)));
        AudioStreamInfo? restricted = await probe.ProbeAsync(input, format);
        Assert.Equal(expected[..^1].Concat(["-format_whitelist", "wav,mp3,flac,aac,mov,ogg", "-protocol_whitelist", "file,pipe",
                "-enable_drefs", "0", "-use_absolute_path", "0", "-f", format, input]),
            JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(argumentFile)));
        Assert.Equal(new AudioStreamInfo(1_013_000, 44_100, 2, codec, format), restricted);
        Assert.Equal(ordinary, restricted);
        File.Delete(argumentFile);
        await Assert.ThrowsAsync<ArgumentException>(() => probe.ProbeAsync(input, "hls"));
        Assert.False(File.Exists(argumentFile));
    }

    [Fact]
    public void 显式实际解码帧数和分数帧率保留精确值()
    {
        const string json = """
            {"streams":[{"codec_type":"video","codec_name":"prores","width":320,"height":180,
            "r_frame_rate":"30000/1001","duration":"3.003","pix_fmt":"yuva444p12le","nb_read_frames":"90"}],
            "format":{"duration":"3.003","format_name":"mov,mp4,m4a,3gp,3g2,mj2"}}
            """;
        MediaStreamInfo stream = Assert.Single(FfmpegMediaProbe.ParseFile(json)!.Streams);
        Assert.Equal(90L, stream.DecodedFrameCount);
        Assert.Equal(30_000, stream.FrameRateNumerator);
        Assert.Equal(1001, stream.FrameRateDenominator);
        Assert.True(stream.HasAlpha);
    }

    [Fact]
    public async Task 无换行巨型错误流可排水而超预算元数据被拒绝()
    {
        using TemporaryDirectory directory = new();
        string executable = WriteExecutable(directory, """
            import sys
            sys.stderr.write('x' * (4*1024*1024))
            print('{"streams":[{"codec_name":"pcm_s16le","sample_rate":"44100","channels":1,"duration":"3"}],"format":{"duration":"3","format_name":"wav"}}')
            """, "stderr-probe");
        using CancellationTokenSource bounded = new(TimeSpan.FromSeconds(10));
        string input = directory.FilePath("owned.wav");
        WavWriter.Write(input, [.25f], 44_100);
        AudioStreamInfo? info = await new FfmpegMediaProbe(executable).ProbeAsync(input, "wav", bounded.Token);
        Assert.Equal("pcm_s16le", info!.CodecName);
        string oversized = WriteExecutable(directory, """
            import sys
            while True:
                sys.stdout.write('x' * 4096)
                sys.stdout.flush()
            """, "metadata-probe");
        await Assert.ThrowsAsync<IOException>(() => new FfmpegMediaProbe(oversized).ProbeFileAsync(directory.FilePath("unused.mov"), bounded.Token));
    }

    [Fact]
    public async Task 默认探测不增加解码成本而强策略只允许明确容器和文件协议()
    {
        using TemporaryDirectory directory = new();
        string arguments = directory.FilePath("args.json");
        string script = "import sys,json\njson.dump(sys.argv[1:],open(" +
            System.Text.Json.JsonSerializer.Serialize(arguments) + ", 'w'))\nprint('{\"streams\":[],\"format\":{}}')\n";
        FfmpegMediaProbe probe = new(WriteExecutable(directory, script, "args-probe"));
        await probe.ProbeFileAsync(directory.FilePath("unused.mov"));
        string[] ordinary = System.Text.Json.JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(arguments))!;
        Assert.DoesNotContain("-count_frames", ordinary);
        Assert.DoesNotContain("-protocol_whitelist", ordinary);
        await probe.ProbeFileAsync(directory.FilePath("unused.mov"), true, "mov");
        string[] strict = System.Text.Json.JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(arguments))!;
        Assert.Contains("-count_frames", strict);
        Assert.Contains("file,pipe", strict);
        int format = Array.IndexOf(strict, "-f"); Assert.Equal("mov", strict[format + 1]);
        await Assert.ThrowsAsync<ArgumentException>(() => probe.ProbeFileAsync(directory.FilePath("x"), true, "hls"));
    }

    private static string WriteExecutable(TemporaryDirectory directory, string content, string name)
    {
        if (OperatingSystem.IsWindows())
        {
            string pyPath = directory.FilePath(name + ".py");
            File.WriteAllText(pyPath, content);
            string cmdPath = directory.FilePath(name + ".cmd");
            File.WriteAllText(cmdPath, string.Create(CultureInfo.InvariantCulture, $"@echo off\r\n\"{TestMediaToolPaths.Python}\" \"{pyPath}\" %*\r\n"));
            return cmdPath;
        }
        string path = directory.FilePath(name); File.WriteAllText(path, "#!/usr/bin/env python3\n" + content.Replace("\r\n", "\n", StringComparison.Ordinal));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
