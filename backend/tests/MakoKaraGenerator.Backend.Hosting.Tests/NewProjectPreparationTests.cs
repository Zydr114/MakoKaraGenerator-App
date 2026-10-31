using System.Collections.Immutable;
using System.Text;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class NewProjectPreparationTests
{
    [Fact]
    public async Task 成功候选保持合成与默认基线并用实际采样时长解析固定歌词()
    {
        LoadedWaveform waveform = Sample();
        var request = Request("[offset:12]\r\n[00:00.10]星空\r\n[00:00.50]歌声", "lyrics.lrc") with
        {
            Composition = Composition.Create(641, 361, FrameRate.NtscVideo, new RgbaColor(1, 2, 3, 4), 2_345_678),
            LyricsOptions = new() { ImporterId = "lrc.plain", EncodingName = "utf-8" },
        };
        int calls = 0;
        var service = new NewProjectPreparationService((path, token) =>
        {
            Assert.Equal(request.AudioPath, path);
            Assert.Equal(CancellationToken.None, token);
            calls++;
            return Task.FromResult<LoadedWaveform?>(waveform);
        });

        NewProjectPreparationResult result = await service.PrepareAsync(request);

        Assert.True(result.Accepted);
        Assert.Null(result.Error);
        PreparedNewProjectData candidate = Assert.IsType<PreparedNewProjectData>(result.Candidate);
        Assert.Equal(1, calls);
        Assert.Same(waveform, candidate.Waveform);
        Assert.Equal("新工程", candidate.Project.Name);
        Assert.Same(request.Composition, candidate.Project.Composition);
        Assert.Equal(request.AudioPath, candidate.Project.AudioPath);
        Assert.Null(candidate.Project.AudioMedia);
        Assert.Null(candidate.Project.AudioAnalysis);
        Assert.Equal(Project.CurrentSchemaVersion, candidate.Project.SchemaVersion);
        Assert.Equal(LyricStyle.Default, candidate.Project.LyricStyle);
        Assert.Null(candidate.Project.LyricStylePresetId);
        Assert.Equal(["星空", "歌声"], candidate.Project.Lyrics.Lines.Select(line => line.Text));
        Assert.Equal(112_000, candidate.Project.Lyrics.Lines[0].Time!.Value.StartUs);
        Assert.Equal(1_000_000, candidate.Project.Lyrics.Lines[1].Time!.Value.EndUs);
        Assert.Equal(12_000, candidate.Report.OffsetUs);
        Assert.Equal(2, candidate.Report.LineCount);
        Assert.Equal(0, candidate.Report.FallbackLineEndCount);
        Assert.Equal(1, candidate.Report.DerivedLineEndCount);
        Assert.IsType<ImmutableArray<MakoKaraGenerator.Core.Lyrics.Import.LyricsImportIssue>>(candidate.Report.Issues);
        Assert.IsType<ImmutableArray<string>>(candidate.Report.CandidateNames);
        Assert.IsType<ImmutableArray<string>>(candidate.Report.DiscardedFeatures);
    }

    [Fact]
    public async Task 固定歌词候选不重读后来替换或删除的歌词文件并保留Unicode与切分选择()
    {
        using TemporaryDirectory directory = new();
        string lyricsPath = directory.FilePath("lyrics.txt");
        byte[] original = Encoding.UTF8.GetBytes("夜空 🏳️‍🌈 café\n歌声");
        File.WriteAllBytes(lyricsPath, original);
        var source = new LyricsImportSource(Path.GetFileName(lyricsPath), File.ReadAllBytes(lyricsPath));
        Array.Fill(original, (byte)'x');
        File.WriteAllText(lyricsPath, "改写");
        File.Delete(lyricsPath);
        var request = Request() with
        {
            LyricsSource = source,
            LyricsOptions = new() { SplitRule = TokenSplitRule.None },
        };

        var candidate = (await Service().PrepareAsync(request)).Candidate!;

        Assert.Equal(["夜空 🏳️‍🌈 café", "歌声"], candidate.Project.Lyrics.Lines.Select(line => line.Text));
        Assert.Contains(candidate.Project.Lyrics.Lines[0].Tokens, token => token.Text == "🏳️‍🌈");
        Assert.Equal("text.plain", candidate.Report.ImporterId);
        Assert.Equal(0, candidate.Report.TimedTokenCount);
        Assert.Equal(candidate.Report.TokenCount, candidate.Report.MissingTokenCount);
    }

    [Theory]
    [InlineData("Name", 0)]
    [InlineData("Name", 1)]
    [InlineData("AudioPath", 0)]
    [InlineData("AudioPath", 1)]
    [InlineData("Composition", 0)]
    [InlineData("Composition", 1)]
    [InlineData("Composition", 2)]
    [InlineData("Composition", 3)]
    [InlineData("Composition", 4)]
    [InlineData("LyricsSource", 0)]
    [InlineData("LyricsOptions", 0)]
    public async Task 非法必要输入在媒体准备前按字段拒绝(string field, int variant)
    {
        var request = (field, variant) switch
        {
            ("Name", 0) => Request() with { Name = string.Empty },
            ("Name", 1) => Request() with { Name = " \t" },
            ("AudioPath", 0) => Request() with { AudioPath = string.Empty },
            ("AudioPath", 1) => Request() with { AudioPath = " \t" },
            ("Composition", 0) => Request() with { Composition = null! },
            ("Composition", 1) => Request() with { Composition = new() { CanvasWidth = 0 } },
            ("Composition", 2) => Request() with { Composition = new() { CanvasHeight = -1 } },
            ("Composition", 3) => Request() with { Composition = new() { OutputDurationUs = 0 } },
            ("Composition", 4) => Request() with { Composition = new() { FrameRate = default } },
            ("LyricsSource", _) => Request() with { LyricsSource = null! },
            ("LyricsOptions", _) => Request() with { LyricsOptions = null! },
            _ => throw new InvalidOperationException(),
        };
        int calls = 0;
        var service = new NewProjectPreparationService((_, _) =>
        {
            calls++;
            return Task.FromResult<LoadedWaveform?>(Sample());
        });

        NewProjectPreparationResult result = await service.PrepareAsync(request);

        AssertRejected(result, NewProjectPreparationErrorCode.InvalidRequest, field);
        Assert.NotEmpty(result.Error!.Detail!);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 没有音轨或解码没有采样均拒绝候选(bool emptySamples)
    {
        LoadedWaveform? waveform = emptySamples ? Sample(0) : null;
        var service = new NewProjectPreparationService((_, _) => Task.FromResult(waveform));

        var result = await service.PrepareAsync(Request());

        AssertRejected(result, NewProjectPreparationErrorCode.NoAudioStream, "AudioPath");
    }

    [Fact]
    public async Task 空歌词拒绝而不将空工程作为成功候选()
    {
        var result = await Service().PrepareAsync(Request("\r\n \t"));

        AssertRejected(result, NewProjectPreparationErrorCode.EmptyLyrics, "LyricsSource");
    }

    [Fact]
    public async Task 无法识别的固定歌词返回结构化拒绝并可再次成功准备()
    {
        var service = Service();
        var invalid = Request() with { LyricsSource = new("song.bin", [0, 1, 2, 127, 0]) };

        var rejected = await service.PrepareAsync(invalid);
        var next = await service.PrepareAsync(Request());

        AssertRejected(rejected, NewProjectPreparationErrorCode.LyricsImportFailed, "LyricsSource");
        Assert.NotEmpty(rejected.Error!.Detail!);
        Assert.True(next.Accepted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task 媒体输入失败保留可信详情并返回中立字段错误(int kind)
    {
        Exception failure = kind switch
        {
            0 => new IOException("decode detail"),
            1 => new UnauthorizedAccessException("read detail"),
            2 => new ArgumentException("media detail"),
            _ => new NotSupportedException("format detail"),
        };
        var service = new NewProjectPreparationService((_, _) => Task.FromException<LoadedWaveform?>(failure));

        var result = await service.PrepareAsync(Request());

        AssertRejected(result, NewProjectPreparationErrorCode.AudioPreparationFailed, "AudioPath");
        Assert.Equal(failure.Message, result.Error!.Detail);
    }

    [Fact]
    public async Task 同步媒体输入失败也被结构化而未知实现故障继续抛出()
    {
        var inputFailure = new NewProjectPreparationService((_, _) => throw new IOException("同步读取失败"));
        var programmingFailure = new NewProjectPreparationService((_, _) => throw new InvalidOperationException("内部故障"));

        AssertRejected(await inputFailure.PrepareAsync(Request()), NewProjectPreparationErrorCode.AudioPreparationFailed, "AudioPath");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => programmingFailure.PrepareAsync(Request()));
        Assert.Equal("内部故障", error.Message);
    }

    [Fact]
    public async Task 已取消请求不调用媒体依赖()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int calls = 0;
        var service = new NewProjectPreparationService((_, _) =>
        {
            calls++;
            return Task.FromResult<LoadedWaveform?>(Sample());
        });

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(Request(), cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task 忽略取消的媒体依赖仍等待结束并拒绝迟到成功()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<LoadedWaveform?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new NewProjectPreparationService((_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            entered.SetResult();
            return release.Task;
        });
        Task<NewProjectPreparationResult> pending = service.PrepareAsync(Request(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        Assert.False(pending.IsCompleted);

        release.SetResult(Sample());
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(release.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task 取消后迟到的媒体输入错误不会转成普通失败结果()
    {
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource<LoadedWaveform?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new NewProjectPreparationService((_, _) => release.Task);
        Task<NewProjectPreparationResult> pending = service.PrepareAsync(Request(), cancellation.Token);
        cancellation.Cancel();

        release.SetException(new IOException("late failure"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task 媒体依赖自身的取消不被普通失败捕获()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new NewProjectPreparationService((_, _) => Task.FromCanceled<LoadedWaveform?>(cancellation.Token));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(Request()));

        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task 并发准备各自消费捕获输入且不共享成功候选()
    {
        var firstRelease = new TaskCompletionSource<LoadedWaveform?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new NewProjectPreparationService((path, _) => path == "first.wav"
            ? firstRelease.Task : Task.FromResult<LoadedWaveform?>(Sample(16_000)));
        var firstRequest = Request("第一份") with { Name = "first", AudioPath = "first.wav" };
        var secondRequest = Request("第二份") with { Name = "second", AudioPath = "second.wav" };

        Task<NewProjectPreparationResult> first = service.PrepareAsync(firstRequest);
        var second = await service.PrepareAsync(secondRequest);
        Assert.False(first.IsCompleted);
        firstRelease.SetResult(Sample());
        var firstResult = await first;

        Assert.NotSame(firstResult.Candidate, second.Candidate);
        Assert.Equal("first", firstResult.Candidate!.Project.Name);
        Assert.Equal("第一份", firstResult.Candidate.Project.Lyrics.Lines[0].Text);
        Assert.Equal(1_000_000, firstResult.Candidate.Waveform.Peaks.DurationUs);
        Assert.Equal("second", second.Candidate!.Project.Name);
        Assert.Equal("第二份", second.Candidate.Project.Lyrics.Lines[0].Text);
        Assert.Equal(2_000_000, second.Candidate.Waveform.Peaks.DurationUs);
    }

    [Fact]
    public async Task 新建元素切分与估算选择完整复用共享歌词准备()
    {
        var request = Request("[00:00.10]夜空中\n[00:00.50]星光", "lyrics.lrc") with
        {
            LyricsOptions = new() { ImporterId = "lrc.plain", SplitRule = TokenSplitRule.PerTextElement, EstimateSplitTimes = true },
        };
        var expected = LyricsImportEditUseCase.Prepare(request.LyricsSource, request.LyricsOptions, 1_000_000);

        var candidate = (await Service().PrepareAsync(request)).Candidate!;

        Assert.Equal(expected.Document.Lines.Select(line => (line.Id, line.Text, line.Time)),
            candidate.Project.Lyrics.Lines.Select(line => (line.Id, line.Text, line.Time)));
        Assert.Equal(expected.Document.Lines.SelectMany(line => line.Tokens), candidate.Project.Lyrics.Lines.SelectMany(line => line.Tokens));
        Assert.Equal(expected.Report.SplitLineCount, candidate.Report.SplitLineCount);
        Assert.Equal(expected.Report.SplitCreatedTokenCount, candidate.Report.SplitCreatedTokenCount);
        Assert.Equal(expected.Report.SplitEstimatedTokenCount, candidate.Report.SplitEstimatedTokenCount);
        Assert.Equal(expected.Report.Issues, candidate.Report.Issues);
        Assert.Equal(expected.Report.CandidateNames, candidate.Report.CandidateNames);
    }

    [Fact]
    public async Task 空请求或空依赖是调用合同错误()
    {
        Assert.Throws<ArgumentNullException>(() => new NewProjectPreparationService(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Service().PrepareAsync(null!));
    }

    private static NewProjectPreparationRequest Request(string content = "星空\n歌声", string fileName = "lyrics.txt") =>
        new("新工程", Composition.Create(640, 360, FrameRate.Fps30), "new.wav",
            new(fileName, Encoding.UTF8.GetBytes(content)), new());

    private static NewProjectPreparationService Service() => new((_, _) => Task.FromResult<LoadedWaveform?>(Sample()));

    private static LoadedWaveform Sample(int count = 8_000)
    {
        var builder = new WaveformPeakBuilder();
        builder.Append(new float[count]);
        // 故意使探测时长与实际采样不同；新建歌词末尾必须消费已解码波形时长。
        return new(new AudioStreamInfo(9_000_000, 8_000, 1, "pcm", "wav"), builder.Build(8_000));
    }

    private static void AssertRejected(NewProjectPreparationResult result, NewProjectPreparationErrorCode code, string field)
    {
        Assert.False(result.Accepted);
        Assert.Null(result.Candidate);
        Assert.NotNull(result.Error);
        Assert.Equal(code, result.Error.Code);
        Assert.Equal(field, result.Error.Field);
    }
}
