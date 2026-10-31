using System.Security.Cryptography;
using CliWrap;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Processes;

namespace MakoKaraGenerator.Backend.Worker;

/// <summary>Original six-family native preparation, with bounded complete waveform transport.</summary>
internal static class AudioPreparationTask
{
    internal static async Task<WorkerMessage> ExecuteAsync(WorkerMessage request, bool guards, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool capabilities = request.Kind == WorkerAudioPreparationContract.CapabilitiesKind;
        if (!capabilities && request.Kind != WorkerAudioPreparationContract.RequestKind) throw Failure("WorkerAudioPreparationProtocolInvalid");
        WorkerProtocol.Validate(request);
        var input = WorkerAudioPreparationContract.DecodeInput(request.Payload, capabilities);
        WorkerAudioPreparationContract.ValidateCorrelation(request, request);
        if (request.TaskId != input.TaskId.ToString("N") || request.SourceSha256 != input.Metadata.Audio?.Sha256)
            throw Failure("WorkerAudioPreparationProtocolInvalid");
        if (capabilities)
        {
            var result = await ProbeCapabilitiesAsync(input.Metadata, guards, token).ConfigureAwait(false);
            return Result(WorkerAudioPreparationContract.CapabilitiesResultKind, WorkerAudioPreparationContract.EncodeCapabilities(result));
        }
        if (!guards) throw Failure("WorkerAudioPreparationUnavailable");
        var audio = input.Metadata.Audio!;
        await ValidateAudioAsync(audio, token).ConfigureAwait(false);
        LoadedWaveform? loaded;
        try
        {
            loaded = await AudioWaveformLoader.CreateForSelfContainedFiles(WorkerAudioPreparationContract.MaximumBaseBuckets,
                input.Metadata.FfmpegPath, input.Metadata.FfprobePath).LoadAsync(audio.Path,
                    WorkerAudioPreparationContract.BaseBucketSamples, token).ConfigureAwait(false);
        }
        catch (WaveformPeakBudgetExceededException) { throw Failure("WorkerAudioPreparationBudgetExceeded"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            token.ThrowIfCancellationRequested();
            throw Failure("WorkerAudioPreparationInputRejected");
        }
        token.ThrowIfCancellationRequested();
        await ValidateAudioAsync(audio, token).ConfigureAwait(false);
        if (loaded is null) throw Failure("WorkerAudioPreparationNoAudioStream");
        int count = loaded.Peaks.BucketCount(0);
        if (count > WorkerAudioPreparationContract.MaximumBaseBuckets) throw Failure("WorkerAudioPreparationBudgetExceeded");
        float[] minimums = new float[count], maximums = new float[count];
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            PeakRange peak = loaded.Peaks.GetBucketPeak(0, index);
            minimums[index] = peak.Min; maximums[index] = peak.Max;
        }
        var info = loaded.Info;
        byte[] payload = WorkerAudioPreparationContract.EncodeResult(new(audio.Sha256,
            new(info.DurationUs, info.SampleRate, info.Channels, info.CodecName, info.ContainerName),
            loaded.Peaks.SampleCount, loaded.Peaks.BaseBucketSamples, minimums, maximums));
        token.ThrowIfCancellationRequested();
        return Result(WorkerAudioPreparationContract.ResultKind, payload);

        WorkerMessage Result(string kind, byte[] payload) => new(kind, request.RequestId, request.InputSha256, payload,
            outputSha256: WorkerProtocol.ComputeSha256(payload), taskId: request.TaskId, sourceSha256: request.SourceSha256);
    }

    private static async Task<WorkerAudioPreparationCapabilities> ProbeCapabilitiesAsync(
        WorkerAudioPreparationMetadata metadata, bool guards, CancellationToken token)
    {
        if (!guards) return Capabilities(false, false, "WorkerAudioPreparationUnavailable");
        try
        {
            RequireTools(await ToolAsync(metadata.FfmpegPath, "-demuxers", token).ConfigureAwait(false),
                WorkerAudioPreparationContract.SupportedFamilies, 'D');
            RequireTools(await ToolAsync(metadata.FfprobePath, "-demuxers", token).ConfigureAwait(false),
                WorkerAudioPreparationContract.SupportedFamilies, 'D');
            RequireTools(await ToolAsync(metadata.FfmpegPath, "-decoders", token).ConfigureAwait(false),
                ["pcm_s16le", "pcm_f32le", "mp3", "flac", "aac", "alac", "vorbis", "opus"], 'A');
            RequireTools(await ToolAsync(metadata.FfmpegPath, "-encoders", token).ConfigureAwait(false), ["pcm_f32le"], 'A');
            RequireTools(await ToolAsync(metadata.FfmpegPath, "-muxers", token).ConfigureAwait(false), ["f32le"], 'E');
            token.ThrowIfCancellationRequested();
            return Capabilities(true, true, null);
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            token.ThrowIfCancellationRequested();
            return Capabilities(true, false, "WorkerAudioPreparationToolUnavailable");
        }
    }

    private static WorkerAudioPreparationCapabilities Capabilities(bool guards, bool tools, string? failure)
        => new(guards, tools, failure, StaticProjectSnapshotCodec.EngineIdentity, WorkerAudioPreparationContract.Profile,
            WorkerAudioPreparationContract.SupportedFamilies, WorkerAudioPreparationContract.MaximumBaseBuckets,
            WorkerAudioPreparationContract.BaseBucketSamples, WorkerAudioPreparationContract.MaximumResultBytes);

    internal static void RequireTools(string listing, string[] required, char flag)
    {
        var names = listing.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length >= 2 && (flag == 'A' ? fields[0].Length == 6 && fields[0][0] == 'A'
                : fields[0] is "D" or "E" or "DE" && fields[0].Contains(flag)))
            .SelectMany(fields => fields[1].Split(',')).ToHashSet(StringComparer.Ordinal);
        if (!required.All(names.Contains)) throw new IOException("受信媒体工具缺少固定准备能力。");
    }

    private static async Task<string> ToolAsync(string path, string argument, CancellationToken token)
    {
        string output = string.Empty;
        var result = await Cli.Wrap(path).WithArguments(["-hide_banner", argument]).WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.Create(async (stream, cancellation) =>
            {
                using StreamReader reader = new(stream);
                output = await BoundedTextReader.ReadToEndAsync(reader, 256 * 1024, true, cancellation).ConfigureAwait(false);
            }))
            .WithStandardErrorPipe(PipeTarget.Create(async (stream, cancellation) =>
            {
                using StreamReader reader = new(stream);
                await BoundedTextReader.ReadToEndAsync(reader, 16 * 1024, false, cancellation).ConfigureAwait(false);
            })).ExecuteAsync(token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("受信媒体工具启动失败。");
        return output;
    }

    private static async Task ValidateAudioAsync(WorkerAudioPreparationGrant audio, CancellationToken token)
    {
        try
        {
            CheckPath(audio);
            await using FileStream stream = new(audio.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != audio.Size || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != audio.Sha256)
                throw Failure("WorkerAudioPreparationSourceChanged");
            CheckPath(audio);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            token.ThrowIfCancellationRequested();
            throw Failure("WorkerAudioPreparationSourceChanged");
        }
    }

    private static void CheckPath(WorkerAudioPreparationGrant audio)
    {
        FileInfo file = new(audio.Path);
        if (!file.Exists || file.LinkTarget is not null || file.Length != audio.Size) throw Failure("WorkerAudioPreparationSourceChanged");
        for (DirectoryInfo? parent = file.Directory; parent is not null; parent = parent.Parent)
            if (parent.LinkTarget is not null) throw Failure("WorkerAudioPreparationSourceChanged");
    }

    private static WorkerProtocolException Failure(string code) => new("固定音频准备未能完成。", code);
}
