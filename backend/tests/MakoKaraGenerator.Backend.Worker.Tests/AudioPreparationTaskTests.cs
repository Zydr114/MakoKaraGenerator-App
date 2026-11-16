using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Processes;

namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed class AudioPreparationTaskTests
{
    private static readonly string[] ExpectedFamilies = ["wav", "mp3", "flac", "aac", "mov", "ogg"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Theory]
    [InlineData("wave16", 44100, 1)]
    [InlineData("wavefloat", 48000, 2)]
    [InlineData("mp3", 44100, 2)]
    [InlineData("flac", 48000, 2)]
    [InlineData("aac", 44100, 1)]
    [InlineData("m4aaac", 48000, 2)]
    [InlineData("m4aalac", 44100, 1)]
    [InlineData("oggvorbis", 48000, 2)]
    [InlineData("oggopus", 48000, 1)]
    public async Task 九种完整准备保原Info源采样时长与原071发布体完整pyramid位hash(string name, int rate, int channels)
    {
        if (!ProcessGuards.IsSupported) return;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AudioPreparationBaseline");
        string input = Path.Combine(directory, name + ".input");
        using JsonDocument baseline = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, name + ".waveform.json"), deadline.Token));
        JsonElement golden = baseline.RootElement;
        var grant = await GrantAsync(input);
        WorkerMessage request = Request(grant);
        WorkerMessage outcome = await ExecuteRealWorkerAsync(request, deadline.Token);
        WorkerAudioPreparationContract.ValidateCorrelation(request, outcome); WorkerProtocol.Validate(outcome);
        var transported = WorkerAudioPreparationContract.DecodeResult(outcome.Payload, grant.Sha256);
        var original = (await AudioWaveformLoader.CreateDefault().LoadAsync(input, cancellationToken: deadline.Token))!;
        Assert.Equal(new WorkerPreparedAudioInfo(original.Info.DurationUs, original.Info.SampleRate, original.Info.Channels,
            original.Info.CodecName, original.Info.ContainerName), transported.Info);
        var restored = WaveformPeakPyramid.FromBaseBuckets(transported.Info.SampleRate, transported.SampleCount,
            transported.BaseBucketSamples, transported.Minimums, transported.Maximums);
        Assert.Equal(golden.GetProperty("sourceHash").GetString(), grant.Sha256);
        Assert.Equal(golden.GetProperty("info").Deserialize<WorkerPreparedAudioInfo>(Json), transported.Info);
        Assert.Equal(channels, transported.Info.Channels);
        Assert.Equal(golden.GetProperty("sampleCount").GetInt64(), restored.SampleCount);
        Assert.Equal(golden.GetProperty("durationUs").GetInt64(), restored.DurationUs);
        Assert.Equal(golden.GetProperty("levelCount").GetInt32(), restored.LevelCount);
        Assert.Equal(golden.GetProperty("peakCounts").EnumerateArray().Select(value => value.GetInt32()),
            Enumerable.Range(0, restored.LevelCount).Select(restored.BucketCount));
        // 立体声有损 AAC 解码与混音浮点尾数在不同 FFmpeg 版本/平台间存在 ULP 级差异，非 Linux 下仅对其他 8 种格式校对跨机录制金值哈希；
        // 所有格式仍在下方与本机 AudioWaveformLoader.CreateDefault() 基准逐峰逐 bit 严格比对。
        if (OperatingSystem.IsLinux() || name != "m4aaac")
        {
            using IncrementalHash pyramidHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] pair = new byte[8];
            for (int level = 0; level < restored.LevelCount; level++)
                for (int bucket = 0; bucket < restored.BucketCount(level); bucket++)
                {
                    PeakRange peak = restored.GetBucketPeak(level, bucket);
                    BinaryPrimitives.WriteSingleLittleEndian(pair, peak.Min);
                    BinaryPrimitives.WriteSingleLittleEndian(pair.AsSpan(4), peak.Max); pyramidHash.AppendData(pair);
                }
            Assert.Equal(golden.GetProperty("fullPyramidHash").GetString(), Convert.ToHexStringLower(pyramidHash.GetHashAndReset()));
        }
        Assert.Equal(original.Peaks.SampleCount, restored.SampleCount); Assert.Equal(rate, restored.SampleRate);
        Assert.Equal(original.Peaks.DurationUs, restored.DurationUs); Assert.Equal(original.Peaks.LevelCount, restored.LevelCount);
        for (int level = 0; level < original.Peaks.LevelCount; level++)
        {
            Assert.Equal(original.Peaks.BucketCount(level), restored.BucketCount(level));
            Assert.Equal(original.Peaks.BucketSamples(level), restored.BucketSamples(level));
            for (int bucket = 0; bucket < original.Peaks.BucketCount(level); bucket++)
            {
                PeakRange expected = original.Peaks.GetBucketPeak(level, bucket), actual = restored.GetBucketPeak(level, bucket);
                Assert.Equal(BitConverter.SingleToInt32Bits(expected.Min), BitConverter.SingleToInt32Bits(actual.Min));
                Assert.Equal(BitConverter.SingleToInt32Bits(expected.Max), BitConverter.SingleToInt32Bits(actual.Max));
            }
        }
    }

    [Fact]
    public async Task 真实工具能力保当前引擎六家族与独立完整波形容量()
    {
        var result = await AudioPreparationTask.ExecuteAsync(Request(null), true, CancellationToken.None);
        var caps = WorkerAudioPreparationContract.DecodeCapabilities(result.Payload);
        Assert.True(caps.GuardAvailable); Assert.True(caps.ToolStartupVerified); Assert.Null(caps.FailureCode);
        Assert.Equal(StaticProjectSnapshotCodec.EngineIdentity, caps.EngineIdentity);
        Assert.Equal(WorkerAudioPreparationContract.Profile, caps.Profile);
        Assert.Equal(ExpectedFamilies, caps.Families);
        Assert.Equal(500000, caps.MaximumBaseBuckets); Assert.Equal(64, caps.BaseBucketSamples); Assert.Equal(4194304, caps.MaximumResultBytes);
        var disabled = WorkerAudioPreparationContract.DecodeCapabilities((await AudioPreparationTask.ExecuteAsync(Request(null), false, CancellationToken.None)).Payload);
        Assert.False(disabled.GuardAvailable); Assert.False(disabled.ToolStartupVerified);
        Assert.Throws<IOException>(() => AudioPreparationTask.RequireTools(" D wav\n D mp3\n", WorkerAudioPreparationContract.SupportedFamilies, 'D'));
        Assert.Throws<IOException>(() => AudioPreparationTask.RequireTools(" V....D pcm_f32le fake\n", ["pcm_f32le"], 'A'));
        Assert.Throws<IOException>(() => AudioPreparationTask.RequireTools(" D f32le wrong-direction\n", ["f32le"], 'E'));
    }

    [Fact]
    public void 完整尾桶IEEE754与探测时长独立保真且坏封包明确拒绝()
    {
        var result = new WorkerAudioPreparationResult(new('a', 64), new(987654, 48000, 6, "pcm_f32le", "wav"), 65, 64,
            [BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), -2.5f], [0, 4.25f]);
        byte[] bytes = WorkerAudioPreparationContract.EncodeResult(result);
        var decoded = WorkerAudioPreparationContract.DecodeResult(bytes, result.SourceSha256);
        Assert.Equal(987654, decoded.Info.DurationUs); Assert.Equal(6, decoded.Info.Channels);
        Assert.Equal(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(decoded.Minimums[0]));
        Assert.Equal(4.25f, decoded.Maximums[1]); Assert.Equal(65, decoded.SampleCount);
        Assert.Equal("WorkerAudioPreparationProtocolInvalid", Assert.Throws<WorkerProtocolException>(() =>
            WorkerAudioPreparationContract.DecodeResult(bytes, new('b', 64))).Code);
        byte[] badCount = [.. bytes]; BinaryPrimitives.WriteInt32LittleEndian(badCount.AsSpan(52, 4), 1);
        Assert.Throws<WorkerProtocolException>(() => WorkerAudioPreparationContract.DecodeResult(badCount, result.SourceSha256));
        byte[] badFloat = [.. bytes]; BinaryPrimitives.WriteSingleLittleEndian(badFloat.AsSpan(^4), float.NaN);
        Assert.Throws<WorkerProtocolException>(() => WorkerAudioPreparationContract.DecodeResult(badFloat, result.SourceSha256));
    }

    [Fact]
    public void 生产完整容量五十万桶可传且多一采样明确拒绝()
    {
        var result = new WorkerAudioPreparationResult(new('a', 64), new(0, 384000, 6, "pcm_f32le", "wav"),
            32_000_000, 64, new float[500000], new float[500000]);
        byte[] bytes = WorkerAudioPreparationContract.EncodeResult(result);
        Assert.True(bytes.Length <= 4194304);
        var complete = WorkerAudioPreparationContract.DecodeResult(bytes, result.SourceSha256);
        Assert.Equal(500000, complete.Minimums.Length); Assert.Equal(32_000_000, complete.SampleCount);
        Assert.Equal(384000, complete.Info.SampleRate); Assert.Equal(6, complete.Info.Channels);
        Assert.Equal("WorkerAudioPreparationBudgetExceeded", Assert.Throws<WorkerProtocolException>(() =>
            WorkerAudioPreparationContract.EncodeResult(result with { SampleCount = 32_000_001 })).Code);
    }

    [Fact]
    public void 输入封包拒未知重复字段路径非规范与混入其他领域回执()
    {
        string ownedAudio = OperatingSystem.IsWindows() ? @"C:\owned\audio" : "/owned/audio";
        string traversalAudio = OperatingSystem.IsWindows() ? @"C:\owned\..\audio" : "/owned/../audio";
        var request = Request(new(ownedAudio, 5, new('a', 64)));
        var input = WorkerAudioPreparationContract.DecodeInput(request.Payload);
        Assert.Equal(request.TaskId, input.TaskId.ToString("N"));
        Assert.Throws<WorkerProtocolException>(() => WorkerAudioPreparationContract.EncodeInput(Guid.NewGuid(), input.Metadata with
            { Audio = input.Metadata.Audio! with { Path = traversalAudio } }));
        foreach (string extra in new[] { ",\"unknown\":1", ",\"profile\":\"different\"", ",\"Profile\":\"self-contained-native-waveform-v1\"" })
        {
            byte[] json = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input.Metadata, Json)[..^1] + extra + "}");
            byte[] payload = new byte[WorkerAudioPreparationContract.InputHeaderBytes + json.Length];
            request.Payload.AsSpan(0, WorkerAudioPreparationContract.InputHeaderBytes).CopyTo(payload);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24, 4), json.Length); json.CopyTo(payload.AsSpan(WorkerAudioPreparationContract.InputHeaderBytes));
            Assert.Throws<WorkerProtocolException>(() => WorkerAudioPreparationContract.DecodeInput(payload));
        }
        Assert.Throws<WorkerProtocolException>(() => WorkerAudioPreparationContract.ValidateCorrelation(request, request with { FrameSequence = 1 }));
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Validate(request with { InputSha256 = new('b', 64) }));
    }

    [Fact]
    public async Task grant错配前后实际源变化无流与普通媒体拒绝有不同稳定原因()
    {
        using TemporaryDirectory temporary = new(); string input = temporary.FilePath("owned.input"); await File.WriteAllBytesAsync(input, "owned"u8.ToArray());
        string probe = await ScriptAsync(temporary, "probe", "print('{\"streams\":[{\"sample_rate\":\"44100\",\"channels\":1,\"codec_name\":\"pcm_f32le\"}],\"format\":{\"format_name\":\"wav\"}}')\n");
        string decoder = await ScriptAsync(temporary, "decoder", "import struct,sys\nsys.stdout.buffer.write(struct.pack('<f',.5))\n");
        var grant = await GrantAsync(input);
        await AssertCodeAsync(Request(grant with { Sha256 = new('a', 64) }, decoder, probe), "WorkerAudioPreparationSourceChanged");
        string mutator = await ScriptAsync(temporary, "mutator", "import struct,sys\nopen(" + JsonSerializer.Serialize(input)
            + ",'wb').write(b'other')\nsys.stdout.buffer.write(struct.pack('<f',.5))\n");
        await AssertCodeAsync(Request(grant, mutator, probe), "WorkerAudioPreparationSourceChanged");
        await File.WriteAllBytesAsync(input, "owned"u8.ToArray());
        string noStream = await ScriptAsync(temporary, "no-stream", "print('{\"streams\":[]}')\n");
        await AssertCodeAsync(Request(grant, decoder, noStream), "WorkerAudioPreparationNoAudioStream");
        string rejected = await ScriptAsync(temporary, "rejected", "import sys\nsys.stderr.write('owned-private-path');sys.exit(1)\n");
        await AssertCodeAsync(Request(grant, rejected, probe), "WorkerAudioPreparationInputRejected");
        async Task AssertCodeAsync(WorkerMessage request, string code)
        {
            var failure = await Assert.ThrowsAsync<WorkerProtocolException>(() => AudioPreparationTask.ExecuteAsync(request, true, CancellationToken.None));
            Assert.Equal(code, failure.Code); Assert.DoesNotContain(input, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("owned-private-path", failure.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 实际Worker准备与能力探测取消回执保各自kind关联且native已退出(bool capabilities)
    {
        if (!ProcessGuards.IsSupported) return;
        using TemporaryDirectory temporary = new(); string input = temporary.FilePath("owned.input");
        await File.WriteAllBytesAsync(input, "owned"u8.ToArray());
        string pidFile = temporary.FilePath("native.pid");
        string native = await ScriptAsync(temporary, "owned-native", "import os,time\nopen(" + JsonSerializer.Serialize(pidFile)
            + ",'w').write(str(os.getpid()))\ntime.sleep(30)\n");
        var request = Request(capabilities ? null : await GrantAsync(input), native, native);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        var outcome = await ExecuteRealWorkerAsync(request, deadline.Token, pidFile);
        WorkerAudioPreparationContract.ValidateCorrelation(request, outcome);
        Assert.True(outcome.Cancelled); Assert.Empty(outcome.Payload); Assert.Null(outcome.OutputSha256);
        int pid = int.Parse(await File.ReadAllTextAsync(pidFile, deadline.Token), System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 0; i < 50 && IsProcessAlive(pid); i++) await Task.Delay(20, deadline.Token);
        Assert.False(IsProcessAlive(pid));
    }

    private static WorkerMessage Request(WorkerAudioPreparationGrant? grant, string? ffmpeg = null, string? ffprobe = null)
    {
        ffmpeg ??= TestMediaToolPaths.Ffmpeg;
        ffprobe ??= TestMediaToolPaths.Ffprobe;
        Guid id = Guid.NewGuid(); bool capabilities = grant is null;
        byte[] bytes = WorkerAudioPreparationContract.EncodeInput(id, new(WorkerAudioPreparationContract.Profile, grant, ffmpeg, ffprobe), capabilities);
        return new(capabilities ? WorkerAudioPreparationContract.CapabilitiesKind : WorkerAudioPreparationContract.RequestKind,
            Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(bytes), bytes, taskId: id.ToString("N"), sourceSha256: grant?.Sha256);
    }
    private static async Task<WorkerAudioPreparationGrant> GrantAsync(string path)
        => new(path, new FileInfo(path).Length, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
    private static async Task<WorkerMessage> ExecuteRealWorkerAsync(WorkerMessage request, CancellationToken token,
        string? cancelAfterNativeStarts = null)
    {
        Assert.True(ProcessGuards.TryEnableChildReaping());
        string workerPath = Path.Combine(AppContext.BaseDirectory, "MakoKaraGenerator.Backend.Worker" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        ProcessStartInfo startInfo = new(workerPath)
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        startInfo.Environment.Remove("MAKO_BACKEND_WORKER_MEDIA_LIMIT_BYTES");

        WindowsControlledProcess? controlled = null;
        Process? legacyProcess = null;
        Stream stdin;
        Stream stdout;
        StreamReader stderrReader;
        if (WindowsJobObjectGuard.IsSupported)
        {
            controlled = WindowsJobObjectGuard.StartProcess(startInfo);
            legacyProcess = controlled.ManagedProcess;
            stdin = controlled.StandardInput;
            stdout = controlled.StandardOutput;
            stderrReader = controlled.StandardError;
        }
        else
        {
            legacyProcess = new Process { StartInfo = startInfo };
            Assert.True(legacyProcess.Start());
            stdin = legacyProcess.StandardInput.BaseStream;
            stdout = legacyProcess.StandardOutput.BaseStream;
            stderrReader = legacyProcess.StandardError;
        }

        Task<string> stderr = BoundedTextReader.ReadToEndAsync(stderrReader, 32768, false, CancellationToken.None);
        OwnedProcessGroup? group = null; Exception? primary = null;
        try
        {
            var hello = new WorkerMessage("hello", Guid.NewGuid().ToString("N"));
            await WorkerProtocol.WriteAsync(stdin, hello, token);
            WorkerMessage greeting = (await WorkerProtocol.ReadAsync(stdout, token))!;
            Assert.Equal("hello", greeting.Kind); Assert.Equal(hello.RequestId, greeting.RequestId);
            Assert.Equal(legacyProcess.Id, greeting.ProcessGroupId);
            if (controlled is not null)
            {
                controlled.Guard.VerifyIdentity();
            }
            else
            {
                group = OwnedProcessGroup.TryCapture(legacyProcess);
                Assert.NotNull(group);
            }
            await WorkerProtocol.WriteAsync(stdin, request, token);
            if (cancelAfterNativeStarts is not null)
            {
                while (!File.Exists(cancelAfterNativeStarts)) await Task.Delay(10, token);
                await WorkerProtocol.WriteAsync(stdin, new("cancel", request.RequestId), token);
            }
            WorkerMessage result = (await WorkerProtocol.ReadAsync(stdout, token))!;
            Assert.Equal(request.Kind == WorkerAudioPreparationContract.CapabilitiesKind
                ? WorkerAudioPreparationContract.CapabilitiesResultKind : WorkerAudioPreparationContract.ResultKind, result.Kind);
            var shutdown = new WorkerMessage("shutdown", Guid.NewGuid().ToString("N"));
            await WorkerProtocol.WriteAsync(stdin, shutdown, token);
            WorkerMessage stopped = (await WorkerProtocol.ReadAsync(stdout, token))!;
            Assert.Equal("shutdown", stopped.Kind); Assert.Equal(shutdown.RequestId, stopped.RequestId);
            await legacyProcess.WaitForExitAsync(token);
            if (controlled is not null)
            {
                Assert.True(controlled.Guard.TryGetLeaderExitCode(out int code));
                Assert.Equal(0, code);
            }
            else
            {
                Assert.Equal(0, legacyProcess.ExitCode);
            }
            return result;
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            try
            {
                if (controlled is not null)
                {
                    await controlled.Guard.KillAndDrainAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
                    await legacyProcess.WaitForExitAsync(CancellationToken.None);
                    await stderr;
                    Assert.Equal(0, controlled.Guard.ObserveActiveProcessCount());
                    stdin.Dispose(); stdout.Dispose(); stderrReader.Dispose();
                    await controlled.Guard.DisposeAsync();
                    legacyProcess.Dispose();
                }
                else
                {
                    if (group is not null) await group.KillAndDrainAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
                    else if (!legacyProcess.HasExited) legacyProcess.Kill(entireProcessTree: true);
                    await legacyProcess.WaitForExitAsync(CancellationToken.None); await stderr;
                    if (group is not null) Assert.Empty(group.ObserveMembers());
                    legacyProcess.Dispose();
                }
            }
            catch (Exception cleanup) when (primary is not null) { primary.Data["AuthorWorkerCleanupFailure"] = cleanup; }
        }
    }
    private static async Task<string> ScriptAsync(TemporaryDirectory temporary, string name, string source)
    {
        if (OperatingSystem.IsWindows())
        {
            string pyPath = temporary.FilePath(name + ".py");
            await File.WriteAllTextAsync(pyPath, source);
            string cmdPath = temporary.FilePath(name + ".cmd");
            await File.WriteAllTextAsync(cmdPath, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"@echo off\r\n\"{TestMediaToolPaths.Python}\" \"{pyPath}\" %*\r\n"));
            return cmdPath;
        }
        string path = temporary.FilePath(name); await File.WriteAllTextAsync(path, "#!/usr/bin/env python3\n" + source);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
