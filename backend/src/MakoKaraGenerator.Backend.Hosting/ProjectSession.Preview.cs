using System.Globalization;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed record BackendPreviewInputCaptureResult(BackendPreviewPinnedInput? Input, BackendErrorResponse? Error,
    BackendProjectImageReadLease? ImageSource = null, BackendProjectRuntimeInputLease? RuntimeInput = null);

public sealed partial class ProjectSession
{
    /// <summary>在工程唯一写者闸门内核对来源修订并捕获不可变快照，不暴露工作区。资源回调仅可同步取得引用，不得重入 Writer 或执行编码/Worker 操作。</summary>
    public Task<BackendPreviewInputCaptureResult> CapturePreviewInputAsync(string clientId,
        string expectedProjectRevision, IRenderFrameSessionFactory factory, CancellationToken cancellationToken = default) =>
        CapturePreviewInputAsync(clientId, expectedProjectRevision, factory, null, cancellationToken);

    /// <summary>同写者内捕获固定音频身份和同步资源引用；回调不得重入写者。</summary>
    public Task<BackendPreviewInputCaptureResult> CapturePreviewInputAsync(string clientId,
        string expectedProjectRevision, IRenderFrameSessionFactory factory,
        Func<string, IRenderFrameSessionFactory>? captureResourceFactory, CancellationToken cancellationToken = default) =>
        CapturePreviewInputAsync(clientId, expectedProjectRevision, factory, captureResourceFactory, false, cancellationToken);

    public Task<BackendPreviewInputCaptureResult> CapturePreviewInputAsync(string clientId,
        string expectedProjectRevision, IRenderFrameSessionFactory factory,
        Func<string, IRenderFrameSessionFactory>? captureResourceFactory, bool captureImageSource,
        CancellationToken cancellationToken = default) =>
        CapturePreviewInputAsync(clientId, expectedProjectRevision, factory, captureResourceFactory, captureImageSource, false, cancellationToken);

    /// <summary>显式捕获图片时，调用者必须读取或释放返回的 ImageSource；短期引用不执行文件 IO。</summary>
    public async Task<BackendPreviewInputCaptureResult> CapturePreviewInputAsync(string clientId,
        string expectedProjectRevision, IRenderFrameSessionFactory factory,
        Func<string, IRenderFrameSessionFactory>? captureResourceFactory, bool captureImageSource, bool captureRuntimeSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        await Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (OwnerClientId != clientId) return new(null, new(BackendErrorCode.SessionNotFound, "工程会话不存在。"));
            if (MutationBlocked) return new(null, new(BackendErrorCode.SessionClosing, "工程会话正在关闭。"));
            string revision = ProjectRevision.ToString(CultureInfo.InvariantCulture);
            if (expectedProjectRevision != revision)
                return new(null, new(BackendErrorCode.RevisionConflict, "工程来源修订已经改变。", "expectedProjectRevision"));
            ProjectSnapshot snapshot = ProjectSnapshot.FromProject(Workspace.Project);
            IRenderFrameSessionFactory capturedFactory = captureResourceFactory?.Invoke(AudioAssetId) ?? factory;
            BackendProjectImageReadLease? image = captureImageSource && !captureRuntimeSource && snapshot.Background.Kind == BackgroundKind.Image
                ? AcquireProjectImageRead(snapshot) : null;
            return new(new(new("project", ProjectSessionId, revision), snapshot, capturedFactory) { CapturedProjectSession = this }, null, image,
                captureRuntimeSource ? AcquireProjectRuntimeInput(snapshot) : null);
        }
        finally { Writer.Release(); }
    }

    /// <summary>
    /// 在工程唯一写者内捕获候选工程，但只在独立工作区上运行纯准备逻辑。
    /// 资源回调仅可同步取得引用，不得重入 Writer 或执行编码/Worker 操作。
    /// 该路径不调用 CommitPrepared，不发布事件，也不改变当前工程历史、dirty 或 redo。
    /// </summary>
    public Task<BackendPreviewInputCaptureResult> CapturePresetCandidatePreviewInputAsync(
        string clientId, string expectedProjectRevision, StyleTemplate template,
        PresetApplicationTargetDto[]? targets, IReadOnlyDictionary<string, string>? branchInputs,
        IRenderFrameSessionFactory factory, CancellationToken cancellationToken = default) =>
        CapturePresetCandidatePreviewInputAsync(clientId, expectedProjectRevision, template, targets, branchInputs,
            factory, null, cancellationToken);

    /// <summary>同写者内固定候选快照和音频资源身份；回调不得重入写者。</summary>
    public async Task<BackendPreviewInputCaptureResult> CapturePresetCandidatePreviewInputAsync(
        string clientId, string expectedProjectRevision, StyleTemplate template,
        PresetApplicationTargetDto[]? targets, IReadOnlyDictionary<string, string>? branchInputs,
        IRenderFrameSessionFactory factory, Func<string, IRenderFrameSessionFactory>? captureResourceFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(factory);
        await Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (OwnerClientId != clientId) return new(null, new(BackendErrorCode.SessionNotFound, "工程会话不存在。"));
            if (MutationBlocked) return new(null, new(BackendErrorCode.SessionClosing, "工程会话正在关闭。"));
            string revision = ProjectRevision.ToString(CultureInfo.InvariantCulture);
            if (expectedProjectRevision != revision)
                return new(null, new(BackendErrorCode.RevisionConflict, "工程来源修订已经改变。", "expectedProjectRevision"));
            if (targets is null || targets.Length is < 1 or > 1000 || targets.Any(target => target is null))
                return new(null, new(BackendErrorCode.InvalidRequest, "试看目标必须包含1至1000个对象。", "targets"));
            try
            {
                using PresetIntentStream intent = new();
                System.Text.Json.JsonSerializer.Serialize(intent, new { targets, branchInputs }, ReadModelJson);
            }
            catch (PresetIntentBudgetExceededException)
            { return new(null, new(BackendErrorCode.ResourceTooLarge, "候选预设目标和输入映射超过64KiB预算。", "targets")); }
            catch (System.Text.Json.JsonException error)
            { return new(null, new(BackendErrorCode.InvalidRequest, error.Message, "targets")); }
            ProjectWorkspace candidateWorkspace = ProjectWorkspace.Adopt(Workspace.Project);
            ProjectEffectBinding[] bindings;
            try { bindings = ToBindings(candidateWorkspace.Project, targets); }
            catch (System.Text.Json.JsonException error)
            { return new(null, new(BackendErrorCode.InvalidRequest, error.Message, "targets")); }
            catch (ArgumentException error)
            { return new(null, new(BackendErrorCode.CommandRejected, error.Message, "targets")); }
            PreparedPresetApplicationsEdit prepared = PresetApplicationsEditUseCase.PrepareApply(
                candidateWorkspace, template, bindings, branchInputs);
            if (!prepared.Accepted)
                return new(null, new(BackendErrorCode.CommandRejected, prepared.Error ?? "候选预设应用被拒绝。", "targets"));
            try { ValidatePresetApplicationsProjection(prepared.Result.Project, revision); }
            catch (ReadModelBudgetExceededException)
            { return new(null, new(BackendErrorCode.ResourceTooLarge, "完整候选预设读投影超过预算。", "targets")); }
            ProjectSnapshot snapshot = ProjectSnapshot.FromProject(prepared.Result.Project);
            IRenderFrameSessionFactory capturedFactory = captureResourceFactory?.Invoke(AudioAssetId) ?? factory;
            return new(new(new("presetCandidate", ProjectSessionId, revision), snapshot, capturedFactory) { CapturedProjectSession = this }, null,
                RuntimeInput: AcquireProjectRuntimeInput(snapshot));
        }
        finally { Writer.Release(); }
    }
}
