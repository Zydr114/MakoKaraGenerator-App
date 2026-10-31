using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using CliWrap;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Processes;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Worker;

/// <summary>复用原完整离线算法；只约束授权、实际采样预算及任务生命周期。</summary>
internal static class StaticAudioAnalysisTask
{
    internal static async Task<WorkerMessage> ExecuteAsync(WorkerMessage request, bool guards,
        Func<WorkerMessage, Task> send, CancellationToken token)
    {
        bool capabilities = request.Kind == WorkerAnalysisContract.CapabilitiesKind;
        var input = WorkerAnalysisContract.DecodeInput(request.Payload, capabilities);
        WorkerAnalysisContract.ValidateCorrelation(request, request);
        if (request.TaskId != input.TaskId.ToString("N") || request.SourceSha256 != input.Metadata.Audio?.Sha256)
            throw Failure("WorkerAnalysisProtocolInvalid");
        if (!guards) throw Failure("WorkerAnalysisUnavailable");
        var metadata = input.Metadata;
        if (capabilities)
        {
            WorkerAnalysisCapabilitiesDto result;
            try
            {
                RequireTools(await ToolAsync(metadata.FfmpegPath, "-demuxers", token).ConfigureAwait(false), ["wav", "mp3", "flac", "aac", "mov", "ogg"]);
                RequireTools(await ToolAsync(metadata.FfmpegPath, "-decoders", token).ConfigureAwait(false), ["pcm_s16le", "mp3", "flac", "aac", "vorbis"], decoders: true);
                RequireTools(await ToolAsync(metadata.FfprobePath, "-demuxers", token).ConfigureAwait(false), ["wav", "mp3", "flac", "aac", "mov", "ogg"]);
                result = Capabilities(true, null);
            }
            catch (Exception failure) when (failure is IOException or System.ComponentModel.Win32Exception)
            { result = Capabilities(false, "WorkerAnalysisToolUnavailable"); }
            return Result(WorkerAnalysisContract.CapabilitiesResultKind, WorkerAnalysisContract.EncodeJson(result, 4096), 0);
        }
        var audio = metadata.Audio!;
        var channel = Channel.CreateBounded<(long Sequence, BackendAnalysisProgressDto Progress)>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        long sequence = 0, lastProgress = 0; Exception? primary = null;
        Task pump = PumpAsync();
        try
        {
            Report("validating-source", 0);
            await ValidateAudioAsync(audio, operation.Token).ConfigureAwait(false);
            var reader = new BoundedAnalysisReader(new RestrictedReader(metadata.FfmpegPath, BackendAudioInputProfiles.GetInputFormat(audio.AudioProfile)), samples =>
            {
                if (samples - lastProgress < WorkerAnalysisContract.ProgressSampleStep) return;
                lastProgress = samples; Report("decoding", samples);
            });
            var probe = new RestrictedProbe(new FfmpegMediaProbe(metadata.FfprobePath), BackendAudioInputProfiles.GetInputFormat(audio.AudioProfile), () => Report("probing", 0));
            var analysis = await new AudioFeatureAnalyzer(probe, reader).AnalyzeAsync(audio.Path, operation.Token).ConfigureAwait(false);
            if (analysis.Features.Availability != AudioAvailability.Available)
                throw Failure(analysis.DiagnosticCode ?? "AudioAnalysisFailed");
            if (analysis.SourceHash != audio.Sha256 || analysis.SampleCount != reader.SampleCount) throw Failure("WorkerAnalysisSourceMismatch");
            Report("finalizing-analysis", reader.SampleCount);
            var data = analysis.ToSourceData()?.Data ?? throw Failure("WorkerAnalysisResultInvalid");
            if (data.SampleCount > WorkerAnalysisContract.MaximumSamples || data.Points.Length > WorkerAnalysisContract.MaximumPoints)
                throw Failure("WorkerAnalysisBudgetExceeded");
            // Bounded samples imply at most 15k fixed-schema points (<3.1MiB with 32 chars per double).
            byte[] bytes = AudioAnalysisJson.Encode(data);
            if (bytes.Length > WorkerAnalysisContract.MaximumResultBytes || WorkerProtocol.ComputeSha256(bytes) != data.ContentHash)
                throw Failure("WorkerAnalysisBudgetExceeded");
            await ValidateAudioAsync(audio, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            Report("prepared", reader.SampleCount, prepared: true);
            channel.Writer.TryComplete(); await pump.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return Result(WorkerAnalysisContract.ResultKind, bytes, sequence);
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            operation.Cancel(); channel.Writer.TryComplete();
            try { await pump.ConfigureAwait(false); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["WorkerAnalysisPumpCleanupFailure"] = cleanup; }
        }
        WorkerMessage Result(string kind, byte[] payload, long finalSequence) => new(kind, request.RequestId, request.InputSha256, payload,
            outputSha256: WorkerProtocol.ComputeSha256(payload), taskId: request.TaskId, sourceSha256: request.SourceSha256, progressSequence: finalSequence);
        void Report(string stage, long samples, bool prepared = false)
        {
            channel.Writer.TryWrite((++sequence, new(stage, Text(samples), prepared ? Text(samples) : null,
                Text(prepared ? WorkerAnalysisContract.DurationUs(samples) : samples * 1_000_000 / 48000))));
        }
        async Task PumpAsync()
        {
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                    await send(new(WorkerAnalysisContract.ProgressKind, request.RequestId, request.InputSha256,
                        taskId: request.TaskId, sourceSha256: request.SourceSha256, progressSequence: item.Sequence,
                        analysisProgress: item.Progress)).ConfigureAwait(false);
            }
            catch { operation.Cancel(); throw; }
        }
    }
    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static WorkerProtocolException Failure(string code) => new("固定离线分析未完成。", code);
    private static async Task ValidateAudioAsync(WorkerAnalysisAudioGrant audio, CancellationToken token)
    {
        var file = new FileInfo(audio.Path);
        if (file.LinkTarget is not null || !file.Exists || file.Length != audio.Size) throw Failure("WorkerAnalysisSourceMismatch");
        for (DirectoryInfo? parent = file.Directory; parent is not null; parent = parent.Parent)
            if (parent.LinkTarget is not null) throw Failure("WorkerAnalysisSourceMismatch");
        await using FileStream stream = new(audio.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        BackendAudioInputInfo info;
        if (audio.AudioProfile == BackendAudioInputProfiles.Pcm16Wave)
        {
            // Preserve the legacy WAV validation and stable rejection code.
            var wave = PcmWaveInput.Validate(stream, audio.Size);
            info = new(BackendAudioInputProfiles.Pcm16Wave, wave.SampleRate, wave.Channels, wave.SampleCount, wave.DurationUs);
        }
        else info = BackendAudioInputClassifier.Classify(stream, audio.Size);
        if (info.AudioProfile != audio.AudioProfile) throw Failure("WorkerAnalysisInputRejected");
        string sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        if (sha != audio.Sha256) throw Failure("WorkerAnalysisSourceMismatch");
        file = new(audio.Path);
        if (file.LinkTarget is not null || !file.Exists || file.Length != audio.Size) throw Failure("WorkerAnalysisSourceMismatch");
        for (DirectoryInfo? parent = file.Directory; parent is not null; parent = parent.Parent)
            if (parent.LinkTarget is not null) throw Failure("WorkerAnalysisSourceMismatch");
    }
    private sealed class RestrictedReader(string path, string format) : IResampledPcmSampleReader
    {
        public Task<long> ReadMonoSamplesAsync(string filePath, Func<ReadOnlyMemory<float>, ValueTask> onSamples, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<long> ReadMonoSamplesAtRateAsync(string filePath, int sampleRate, Func<ReadOnlyMemory<float>, ValueTask> onSamples,
            CancellationToken cancellationToken = default)
            => new FfmpegPcmReader(path).ReadMonoSamplesAtRateAsync(filePath, sampleRate, format, onSamples, cancellationToken);
    }
    private sealed class RestrictedProbe(FfmpegMediaProbe inner, string format, Action progress) : IMediaProbe
    {
        public async Task<AudioStreamInfo?> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
        {
            progress();
            var info = await inner.ProbeAsync(filePath, format, cancellationToken).ConfigureAwait(false);
            ValidateProbeInput(info, format);
            return info;
        }
        public Task<MediaFileInfo?> ProbeFileAsync(string filePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private static bool AllowedCodec(string codec, string format) => format switch
    {
        "mov" => codec is "aac" or "alac" or "pcm_s16le" or "mp3",
        "ogg" => codec is "vorbis" or "opus" or "flac",
        "aac" => codec is "aac",
        _ => codec == format,
    };
    private static bool AllowedContainer(string container, string format) => format switch
    {
        "mov" => container.Split(',', StringSplitOptions.None) is { Length: > 0 } members
            && members.All(static member => member is "mov" or "mp4" or "m4a" or "3gp" or "3g2" or "mj2"),
        _ => container == format,
    };
    internal static void ValidateProbeInput(AudioStreamInfo? info, string format)
    {
        if (info is null || info.SampleRate is < 1 or > 192000 || info.Channels is not (1 or 2)
            || (format == "wav" ? info.CodecName != "pcm_s16le" : !AllowedCodec(info.CodecName, format)) || !AllowedContainer(info.ContainerName, format))
            throw Failure("WorkerAnalysisInputRejected");
    }
    /// <summary>在原Builder接收采样前守限；回调成功后才公布实际进度。</summary>
    internal sealed class BoundedAnalysisReader(IResampledPcmSampleReader inner, Action<long> progress) : IResampledPcmSampleReader
    {
        public long SampleCount { get; private set; }
        public Task<long> ReadMonoSamplesAsync(string filePath, Func<ReadOnlyMemory<float>, ValueTask> onSamples, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<long> ReadMonoSamplesAtRateAsync(string filePath, int sampleRate, Func<ReadOnlyMemory<float>, ValueTask> onSamples,
            CancellationToken cancellationToken = default)
        {
            if (sampleRate != 48000) throw Failure("WorkerAnalysisProtocolInvalid");
            return inner.ReadMonoSamplesAtRateAsync(filePath, sampleRate, async samples =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                long next = checked(SampleCount + samples.Length);
                if (next > WorkerAnalysisContract.MaximumSamples || (next + 959) / 960 > WorkerAnalysisContract.MaximumPoints)
                    throw Failure("WorkerAnalysisBudgetExceeded");
                await onSamples(samples).ConfigureAwait(false); SampleCount = next; progress(next);
            }, cancellationToken);
        }
    }
    private static WorkerAnalysisCapabilitiesDto Capabilities(bool tools, string? failure) => new(true, tools, failure,
        StaticProjectSnapshotCodec.EngineIdentity,
        [BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac, BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg],
        [BackendAudioInputProfiles.LegacyAnalysisSource, BackendAudioInputProfiles.CompressedAnalysisSource]);
    internal static void RequireTools(string listing, string[] required, bool decoders = false)
    {
        var names = listing.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length >= 2 && (decoders
                ? fields[0].Length == 6 && fields[0][0] == 'A'
                : fields[0] is "D" or "DE"))
            .SelectMany(fields => fields[1].Split(',')).ToHashSet(StringComparer.Ordinal);
        if (!required.All(names.Contains)) throw new IOException("受信媒体工具缺少固定格式能力。");
    }
    private static async Task<string> ToolAsync(string path, string argument, CancellationToken token)
    {
        string output = string.Empty;
        var result = await Cli.Wrap(path).WithArguments(["-hide_banner", argument]).WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.Create(async (stream, cancellation) => { using StreamReader reader = new(stream);
                output = await BoundedTextReader.ReadToEndAsync(reader, 256 * 1024, true, cancellation).ConfigureAwait(false); }))
            .WithStandardErrorPipe(PipeTarget.Create(async (stream, cancellation) => { using StreamReader reader = new(stream);
                await BoundedTextReader.ReadToEndAsync(reader, 16 * 1024, false, cancellation).ConfigureAwait(false); }))
            .ExecuteAsync(token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("受信媒体工具无法启动。");
        return output;
    }
}
