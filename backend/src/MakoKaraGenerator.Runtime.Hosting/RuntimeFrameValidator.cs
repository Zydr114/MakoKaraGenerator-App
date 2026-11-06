using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Runtime.Hosting;

/// <summary>
/// 透明半程使用的独立快照与会话工厂：固定媒体输入的输出模式属于输入本身，透明输出须用自己的无媒体输入。
/// 快照只允许媒体输入不同，画布、帧率与时长须与普通输出一致。
/// </summary>
public sealed record RuntimeTransparentOutput(ProjectSnapshot Snapshot, IRenderFrameSessionFactory Sessions);

/// <summary>固定试样的完整双输出正式渲染；调用者必须等待取消和资源释放完成。</summary>
public sealed class RuntimeFrameValidator(IRenderFrameSessionFactory sessions, RuntimeTransparentOutput? transparentOutput = null)
{
    /// <summary>严格执行固定快照及帧计划，保留完整结构化诊断并等待会话释放。</summary>
    public Task<RuntimeValidationResult> ValidateAsync(ProjectSnapshot snapshot, RuntimeValidationPlan plan,
        IProgress<RuntimeValidationProgress>? progress = null, CancellationToken cancellationToken = default)
        => ValidateAsync(snapshot, plan, progress, null, cancellationToken);

    /// <summary>严格执行固定计划，并在累积报告前检查可选宿主预算。</summary>
    public async Task<RuntimeValidationResult> ValidateAsync(ProjectSnapshot snapshot, RuntimeValidationPlan plan,
        IProgress<RuntimeValidationProgress>? progress, RuntimeValidationBudget? budget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(plan);
        if (transparentOutput is not null && transparentOutput.Snapshot.Composition != snapshot.Composition)
            throw new ArgumentException("透明半程快照的画布与帧计划必须与普通输出一致。", nameof(snapshot));
        int framesPerOutput = plan.FramesPerOutput;
        int totalFrames = plan.TotalFrames;
        int opaque = 0, transparent = 0;
        bool mode = false;
        long time = 0;
        List<RuntimeValidationIssue> issues = [];
        List<RuntimeValidationDiagnostic> reports = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        RuntimeDiagnosticCursor cursor = new();
        Exception? inspectionFailure = null;
        try
        {
            foreach (bool output in new[] { false, true })
            {
                mode = output;
                time = 0;
                cancellationToken.ThrowIfCancellationRequested();
                // 两个独立会话，不用普通输出的求值状态冒充透明输出。
                var (outputSnapshot, outputSessions) = output && transparentOutput is not null
                    ? (transparentOutput.Snapshot, transparentOutput.Sessions) : (snapshot, sessions);
                await using var lifetime = new ValidationSessionScope(await outputSessions.CreateAsync(outputSnapshot, cancellationToken).ConfigureAwait(false));
                var session = lifetime.Session;
                cursor = new();
                try
                {
                for (int index = 0; index < framesPerOutput; index++)
                {
                    // 与正式编码的帧起始时刻一致，整数除法向下取整。
                    time = (long)index * 1_000_000 * snapshot.Composition.FrameRate.Denominator
                        / snapshot.Composition.FrameRate.Numerator;
                    cancellationToken.ThrowIfCancellationRequested();
                    using RenderFrame frame = await session.RenderAsync(RenderRequest.At(outputSnapshot, time) with
                    { TransparentBackground = output, Quality = RenderQuality.Final }, cancellationToken).ConfigureAwait(false);
                    // 真实像素所有权和尺寸也须有效，不接受其他试样或已释放的帧。
                    if (frame.Width != snapshot.Composition.CanvasWidth || frame.Height != snapshot.Composition.CanvasHeight || frame.TimeUs != time
                        || frame.PixelMemory.Length != frame.Stride * frame.Height)
                        throw new PreparedFrameException("ScriptValidationFrameMismatch", "Unexpected frame dimensions or time.");
                    ReadDiagnostics(frame.DiagnosticSnapshot);
                    foreach (var diagnostic in frame.MaterialDiagnostics)
                        Add(diagnostic.Code, null, false, $"material:{diagnostic}", material: diagnostic);
                    foreach (var diagnostic in frame.ImageEffectDiagnostics)
                        Add(diagnostic.Code, null, false, $"graph:{diagnostic}", graph: diagnostic);
                    if (issues.Any(issue => issue.IsError)) return Result(false);
                    if (output) transparent++; else opaque++;
                    if (index % 10 == 0 || index == framesPerOutput - 1)
                        progress?.Report(new(opaque + transparent, totalFrames, output, time));
                }
                }
                catch (Exception failure) { lifetime.PrimaryFailure = failure; throw; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Result(true);
        }
        catch (OperationCanceledException) { throw; }
        catch (RuntimeValidationBudgetExceededException) { throw; }
        catch (Exception failure) when (ReferenceEquals(failure, inspectionFailure)) { throw; }
        catch (ProjectEffectCompilationException error)
        {
            foreach (var diagnostic in error.Diagnostics)
                Add(diagnostic.Source.Code,
                    null, diagnostic.Source.IsError, "compile:" + diagnostic, compilation: diagnostic);
            if (!issues.Any(issue => issue.IsError)) Add("ScriptValidationCompileFailed", error.Message, true, "compile");
            return Result(false);
        }
        catch (EffectFailureException error)
        {
            time = error.TimeUs;
            var failed = error.DiagnosticSnapshot ?? RuntimeDiagnosticSnapshot.Empty;
            if (!failed.HasError)
                failed = failed.Append([new(error.InstanceId, error.ArtifactHash, error.Stage, error.TimeUs,
                    error.Diagnostic?.Diagnostic ?? new(DiagnosticSeverity.Error, error.Code, error.Message)) { PackageIdentity = error.PackageIdentity }]);
            ReadDiagnostics(failed, reportErrors: false);
            Add(error.Code, error.Message, true, "failure", location: error.Diagnostic?.Diagnostic.Location);
            return Result(false);
        }
        catch (SourceMaterialFailureException error)
        {
            time = error.TimeUs;
            Add(error.Code, error.Message, true, "source-material");
            return Result(false);
        }
        catch (RenderSessionFailureException error)
        {
            if (error.DiagnosticSnapshot is { } actual) ReadDiagnostics(actual, reportErrors: false);
            Add(error.Code, error.Message, true, "session-failure");
            return Result(false);
        }
        catch (Exception error)
        {
            string code = error switch
            { PreparedFrameException prepared => prepared.Code, EffectPackageException package => package.Code,
                EffectOutputException output => output.Code, _ => error.GetType().Name };
            Add(code, error.Message, true, "failure");
            return Result(false);
        }
        void ReadDiagnostics(RuntimeDiagnosticSnapshot snapshot, bool reportErrors = true)
        {
            foreach (var occurrence in cursor.ReadNew(snapshot))
            {
                var report = new RuntimeValidationDiagnostic(mode, occurrence);
                if (budget is not null)
                {
                    if (reports.Count >= budget.MaximumOccurrences) throw new RuntimeValidationBudgetExceededException();
                    try { budget.InspectDiagnostic?.Invoke(report); }
                    catch (Exception failure) { inspectionFailure = failure; throw; }
                }
                reports.Add(report); var diagnostic = occurrence.Diagnostic;
                if (reportErrors || diagnostic.Diagnostic.Severity != DiagnosticSeverity.Error)
                    Add(diagnostic.Diagnostic.Code, diagnostic.Diagnostic.Message, diagnostic.Diagnostic.Severity == DiagnosticSeverity.Error,
                        $"scene:{diagnostic.InstanceId}:{diagnostic.Stage}:{diagnostic.Diagnostic}", diagnostic.TimeUs);
            }
        }
        RuntimeValidationResult Result(bool succeeded) => new(succeeded, opaque, transparent, [.. issues]) { Diagnostics = [.. reports] };

        void Add(string code, string? message, bool error, string identity, long? actualTimeUs = null,
            RenderMaterialDiagnostic? material = null, RenderImageEffectDiagnostic? graph = null,
            ProjectEffectSourceDiagnostic? compilation = null, SourceLocation? location = null)
        {
            string key = mode + ":" + identity;
            if (seen.Contains(key)) return;
            var issue = new RuntimeValidationIssue(mode, actualTimeUs ?? time, code, message, error)
                { Material = material, Graph = graph, Compilation = compilation, Location = location };
            if (budget is not null)
            {
                if (issues.Count >= budget.MaximumIssues) throw new RuntimeValidationBudgetExceededException();
                try { budget.InspectIssue?.Invoke(issue); }
                catch (Exception failure) { inspectionFailure = failure; throw; }
            }
            seen.Add(key); issues.Add(issue);
        }
    }

    // Preserve the operation's failure (especially cancellation) while still awaiting cleanup.
    private sealed class ValidationSessionScope(IRenderFrameSession session) : IAsyncDisposable
    {
        public IRenderFrameSession Session { get; } = session;
        public Exception? PrimaryFailure { get; set; }
        public async ValueTask DisposeAsync()
        {
            try { await Session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (PrimaryFailure is { } primary)
            { primary.Data["RenderSessionCleanupFailure"] = cleanup; }
        }
    }
}
