using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting.Authoring;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed record AuthorPreviewCapture(PreviewSourceDto Source, ProjectSnapshot Snapshot,
    BackendPreparedProjectRuntimeInput PreparedInput);

public sealed partial class ProjectSessionRegistry
{
    public Task<BackendOperationResult<AuthorPreviewCapture>> CaptureAuthorPreviewAsync(
        string clientId, PreviewSourceDto source, CancellationToken cancellationToken = default) =>
        CaptureAuthorPreviewAsync(clientId, source, null, cancellationToken);

    /// <summary>按可信组合根已解析的设备选择，在作者输入准备前核验 SDK 后端要求。</summary>
    public Task<BackendOperationResult<AuthorPreviewCapture>> CaptureAuthorPreviewAsync(
        string clientId, PreviewSourceDto source, string? renderBackend, CancellationToken cancellationToken = default) =>
        CaptureAuthorPreviewAsync(clientId, source, renderBackend, transparentOutput: false, cancellationToken);

    /// <summary>
    /// 同上，并按预览的 alpha 模式固定媒体输入：透明预览不读背景，媒体依赖固定为透明无媒体输入；
    /// 不透明预览经宿主分析试样背景，最多等待试样上限后退回分析中输入。
    /// </summary>
    public Task<BackendOperationResult<AuthorPreviewCapture>> CaptureAuthorPreviewAsync(string clientId, PreviewSourceDto source,
        string? renderBackend, bool transparentOutput, CancellationToken cancellationToken = default)
    {
        if (source is null || source.Kind != "authorDraft")
            return Task.FromResult(BackendOperationResult<AuthorPreviewCapture>.Rejected(
                new(BackendErrorCode.InvalidRequest, "作者预览来源必须是authorDraft。", "source.kind")));
        string backend = renderBackend ?? _renderBackends?.DefaultBackend ?? "cpu";
        if (backend is not ("cpu" or "gpu"))
            return Task.FromResult(BackendOperationResult<AuthorPreviewCapture>.Rejected(InvalidRenderBackend()));
        return WithAuthorPreviewDraftAsync(clientId, source.SourceId,
            document => CaptureAuthorPreviewCoreAsync(document, source, backend, transparentOutput), cancellationToken);
    }

    private async Task<BackendOperationResult<T>> WithAuthorPreviewDraftAsync<T>(string clientId, string draftId,
        Func<AuthorDraftDocument, Task<BackendOperationResult<T>>> action, CancellationToken cancellationToken = default)
    {
        if (!AuthorReady(out BackendErrorResponse? unavailable)) return BackendOperationResult<T>.Rejected(unavailable!);
        SemaphoreSlim gate = _authorDraftLocks.GetOrAdd(draftId, static _ => new(1, 1));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            _authorStop?.Token ?? CancellationToken.None);
        bool entered = false;
        try
        {
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            if (!TryReadAuthorDraft(clientId, draftId, out AuthorDraftDocument? document, out BackendErrorResponse? error))
                return BackendOperationResult<T>.Rejected(error!);
            return await action(document!).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_authorStop?.IsCancellationRequested == true)
        { return AuthorReject<T>(BackendErrorCode.SessionClosing, "作者工作流正在停止。"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return AuthorReject<T>(BackendErrorCode.ProjectLoadFailed, "作者草稿或固定闭包无法读取。", "source", "AuthorDraftStorageFailed"); }
        finally { if (entered) gate.Release(); }
    }

    private async Task<BackendPreviewSessionResult> RegisterPreparedAuthorPreviewAsync(string clientId,
        BackendPreviewPinnedInput input, Func<BackendPreviewSessionResult> register)
    {
        BackendOperationResult<BackendPreviewSessionResult> result = await WithAuthorPreviewDraftAsync(clientId,
            input.Source.SourceId, document =>
            {
                if (input.CapturedProjectSession is not null || input.PreparedInput is null
                    || document.DraftRevision != input.Source.SourceRevision
                    || (document.Configuration ?? DefaultConfiguration(document)).SourceRevision != input.Source.AuthorSourceRevision)
                    return Task.FromResult(AuthorReject<BackendPreviewSessionResult>(BackendErrorCode.RevisionConflict,
                        "作者草稿在固定预览输入期间已改变。", "source.sourceRevision"));
                return Task.FromResult(BackendOperationResult<BackendPreviewSessionResult>.Success(register()));
            }).ConfigureAwait(false);
        return result.Accepted ? result.Response! : new(null, result.Error!);
    }

    private Task<BackendOperationResult<T>> RegisterPreparedAuthorInputAsync<T>(string clientId,
        BackendPreviewPinnedInput input, Func<BackendOperationResult<T>> register) =>
        WithAuthorPreviewDraftAsync(clientId, input.Source.SourceId, document =>
        {
            if (input.CapturedProjectSession is not null || input.PreparedInput is null ||
                document.DraftRevision != input.Source.SourceRevision ||
                (document.Configuration ?? DefaultConfiguration(document)).SourceRevision != input.Source.AuthorSourceRevision)
                return Task.FromResult(AuthorReject<T>(BackendErrorCode.RevisionConflict,
                    "作者草稿在固定预览输入期间已改变。", "source.sourceRevision"));
            return Task.FromResult(register());
        });

    private async Task<BackendOperationResult<AuthorPreviewCapture>> CaptureAuthorPreviewCoreAsync(
        AuthorDraftDocument document, PreviewSourceDto source, string renderBackend, bool transparentOutput)
    {
        if (!string.Equals(document.DraftRevision, source.SourceRevision, StringComparison.Ordinal))
            return AuthorReject<AuthorPreviewCapture>(BackendErrorCode.RevisionConflict,
                "作者草稿修订已经改变，请重新读取草稿。", "source.sourceRevision");
        if (source.AuthorSourceRevision is null || source.TemplateSample is not { } sample)
            return AuthorReject<AuthorPreviewCapture>(BackendErrorCode.InvalidRequest,
                "作者预览必须提供固定源码修订和样本选择。", "source");
        if (sample.SampleId is not (TemplatePreviewLimits.SingleLineSampleId or TemplatePreviewLimits.MultiLineSampleId)
            || string.IsNullOrWhiteSpace(sample.TargetKey)
            || sample.MaterialSlot is not (null or "unsungFill" or "sungFill" or "stroke"))
            return AuthorReject<AuthorPreviewCapture>(BackendErrorCode.InvalidRequest,
                "作者预览样本、目标或材质槽位无效。", "source.templateSample");
        try
        {
            AuthorDraftConfigurationDocument saved = document.Configuration ?? DefaultConfiguration(document);
            if (!string.Equals(saved.SourceRevision, source.AuthorSourceRevision, StringComparison.Ordinal))
                return AuthorReject<AuthorPreviewCapture>(BackendErrorCode.RevisionConflict,
                    "作者配置源码修订已经改变，请重新读取草稿。", "source.authorSourceRevision");
            EffectPackageContents package = await LoadPackageAsync(document.DraftId, saved.SourceRevision,
                _authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
            ScriptConfiguration configuration = BuildConfiguration(package, saved);
            if (renderBackend != "gpu" && EffectPackageProfiles.IsV2(package.Profile))
                return AuthorReject<AuthorPreviewCapture>(BackendErrorCode.CommandNotSupported,
                    "SDK v2 效果仅支持 Vulkan；请显式选择 GPU 后端后再预览。", "renderBackend", "PackageBackendUnsupported");
            TemplateMaterialSlot slot = ParseMaterialSlot(sample.MaterialSlot, configuration);
            bool multiLine = sample.SampleId == TemplatePreviewLimits.MultiLineSampleId;
            TemplateSampleTarget? target = ScriptSampleBuilder.Targets(configuration, slot, multiLine)
                .FirstOrDefault(item => item.Key == sample.TargetKey);
            if (target is null)
                return AuthorReject<AuthorPreviewCapture>(BackendErrorCode.InvalidRequest,
                    "作者预览目标不属于固定样本。", "source.templateSample.targetKey");
            ScriptSampleRequest request = new(configuration, target, slot, multiLine);
            BackendPreparedProjectRuntimeInput prepared = await AuthorSampleRuntimeInput.PrepareAsync(request, MediaFeatures,
                transparentOutput, waitForCompletion: false, _authorStop?.Token ?? CancellationToken.None).ConfigureAwait(false);
            return BackendOperationResult<AuthorPreviewCapture>.Success(new(source, prepared.Snapshot, prepared));
        }
        catch (DirectoryNotFoundException)
        { return AuthorReject<AuthorPreviewCapture>(BackendErrorCode.ResourceNotFound, "作者配置固定源码不存在。", "source.authorSourceRevision"); }
        catch (EffectPackageException error)
        { return AuthorReject<AuthorPreviewCapture>(BackendErrorCode.CommandRejected, error.Message, error.Path, error.Code); }
        catch (TemplateSampleException error)
        { return AuthorReject<AuthorPreviewCapture>(BackendErrorCode.InvalidRequest, "作者试样选区不受支持。", "source.templateSample", error.Code); }
        catch (AuthorSamplePreparationException error)
        { return BackendOperationResult<AuthorPreviewCapture>.Rejected(error.Error); }
        catch (ArgumentException error)
        { return AuthorReject<AuthorPreviewCapture>(BackendErrorCode.InvalidRequest, error.Message, "source", "AuthorConfigurationInvalid"); }
    }
}
