using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>组合现有媒体加载与共享歌词准备，在无窗口、无声卡条件下建立新工程候选。</summary>
/// <remarks>
/// 不读取或改写当前工作区，不创建播放设备。媒体依赖必须在任务结束前释放其进程和读取资源；
/// 即使依赖忽略取消，本服务仍等待其结束并拒绝迟到结果，不以丢弃未完成任务模拟释放。
/// </remarks>
public sealed class NewProjectPreparationService
{
    private readonly Func<string, CancellationToken, Task<LoadedWaveform?>> _decode;

    /// <summary>注入完成探测、解码及波形聚合的媒体服务。</summary>
    /// <param name="decode">遵守完成前释放读取资源的异步媒体依赖。</param>
    public NewProjectPreparationService(Func<string, CancellationToken, Task<LoadedWaveform?>> decode)
    {
        ArgumentNullException.ThrowIfNull(decode);
        _decode = decode;
    }

    /// <summary>组合既有 FFprobe／FFmpeg 波形加载器，不创建桌面播放器。</summary>
    /// <returns>可在无 GUI 进程使用的准备服务。</returns>
    public static NewProjectPreparationService CreateDefault()
    {
        AudioWaveformLoader loader = AudioWaveformLoader.CreateForSelfContainedFiles();
        return new((path, token) => loader.LoadAsync(path, cancellationToken: token));
    }

    /// <summary>从固定歌词字节和受信音频路径准备候选，完成后不自动发布工程。</summary>
    /// <param name="request">不可变输入；歌词识别与解析消费同一份固定字节。</param>
    /// <param name="cancellationToken">取消标记；所有依赖结束后才完成本任务。</param>
    /// <returns>成功候选或中立拒绝原因，不包含播放器或工作区状态。</returns>
    /// <exception cref="ArgumentNullException">请求对象为空。</exception>
    /// <exception cref="OperationCanceledException">请求取消，或媒体依赖报告取消。</exception>
    public async Task<NewProjectPreparationResult> PrepareAsync(
        NewProjectPreparationRequest request, CancellationToken cancellationToken = default)
        => await PrepareUsingAsync(request, _decode, cancellationToken).ConfigureAwait(false);

    internal static async Task<NewProjectPreparationResult> PrepareUsingAsync(NewProjectPreparationRequest request,
        Func<string, CancellationToken, Task<LoadedWaveform?>> decode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (Validate(request) is { } invalid) return new(invalid);

        OptionalNewProjectPreparationResult prepared = await PrepareOptionalUsingAsync(request.Name, request.Composition,
            request.AudioPath, request.LyricsSource, request.LyricsOptions, decode, cancellationToken).ConfigureAwait(false);
        return prepared.Error is { } error ? new(error)
            : new(new PreparedNewProjectData(prepared.Project!, prepared.Waveform!, prepared.Report!));
    }

    internal Task<OptionalNewProjectPreparationResult> PrepareOptionalAsync(string name, Composition composition,
        string? audioPath, LyricsImportSource? lyricsSource, LyricsImportOptions lyricsOptions, CancellationToken cancellationToken)
        => PrepareOptionalUsingAsync(name, composition, audioPath, lyricsSource, lyricsOptions, _decode, cancellationToken);

    // The public preparation use case retains its required-input contract. HTTP can instead start
    // with the real default document and prepare only the material supplied by its caller.
    internal static async Task<OptionalNewProjectPreparationResult> PrepareOptionalUsingAsync(string name, Composition composition,
        string? audioPath, LyricsImportSource? lyricsSource, LyricsImportOptions lyricsOptions,
        Func<string, CancellationToken, Task<LoadedWaveform?>> decode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadedWaveform? loaded = null;
        if (audioPath is not null)
        {
            try
            {
                loaded = await decode(audioPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (IsInputFailure(error))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new(Error: new(NewProjectPreparationErrorCode.AudioPreparationFailed, nameof(NewProjectPreparationRequest.AudioPath), error.Message));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (loaded is null || loaded.Peaks.DurationUs <= 0)
                return new(Error: new(NewProjectPreparationErrorCode.NoAudioStream, nameof(NewProjectPreparationRequest.AudioPath)));
        }

        LyricsDocument? document = null;
        LyricsImportReport? report = null;
        if (lyricsSource is not null)
        {
            LyricsImportResult lyrics;
            try
            {
                lyrics = await Task.Run(() => LyricsImportEditUseCase.Prepare(
                    lyricsSource, lyricsOptions, loaded?.Peaks.DurationUs ?? 0), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (IsInputFailure(error))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new(Error: new(NewProjectPreparationErrorCode.LyricsImportFailed, nameof(NewProjectPreparationRequest.LyricsSource), error.Message));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (lyrics.Document.Lines.IsEmpty)
                return new(Error: new(NewProjectPreparationErrorCode.EmptyLyrics, nameof(NewProjectPreparationRequest.LyricsSource)));
            document = lyrics.Document;
            report = lyrics.Report;
        }

        Project project = Project.Create(name, composition, document);
        var presentation = PresentationValidator.Validate(project);
        if (presentation.HasErrors)
            return new(Error: new(NewProjectPreparationErrorCode.LyricsImportFailed, nameof(NewProjectPreparationRequest.LyricsSource), presentation.ToSummary()));
        if (audioPath is not null) project = ProjectMediaEditor.SetAudioPath(project, audioPath);
        cancellationToken.ThrowIfCancellationRequested();
        return new(project, loaded, report);
    }

    private static NewProjectPreparationError? Validate(NewProjectPreparationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Invalid(nameof(request.Name), "Project name is required.");
        if (request.Composition is null || !request.Composition.IsValid
            || request.Composition.FrameRate.Numerator <= 0 || request.Composition.FrameRate.Denominator <= 0)
            return Invalid(nameof(request.Composition), "Composition dimensions, frame rate and any specified duration must be positive.");
        if (string.IsNullOrWhiteSpace(request.AudioPath)) return Invalid(nameof(request.AudioPath), "Audio input is required.");
        if (request.LyricsSource is null) return Invalid(nameof(request.LyricsSource), "Fixed lyrics input is required.");
        if (request.LyricsOptions is null) return Invalid(nameof(request.LyricsOptions), "Lyrics import options are required.");
        return null;
    }

    private static NewProjectPreparationError Invalid(string field, string detail) =>
        new(NewProjectPreparationErrorCode.InvalidRequest, field, detail);

    private static bool IsInputFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}

internal sealed record OptionalNewProjectPreparationResult(Project? Project = null, LoadedWaveform? Waveform = null,
    LyricsImportReport? Report = null, NewProjectPreparationError? Error = null);
