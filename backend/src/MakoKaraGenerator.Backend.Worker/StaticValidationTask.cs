using System.Globalization;
using System.Threading.Channels;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Worker;

/// <summary>独立固定静态验证任务，业务执行仅调用正式 Runtime 验证器。</summary>
internal static class StaticValidationTask
{
    public static Task<WorkerMessage> ExecuteAsync(WorkerMessage request, Func<WorkerMessage, Task> send, CancellationToken token, string? renderBackend = null)
        => ExecuteAsync(request, send, null, token, renderBackend: renderBackend);

    public static Task<WorkerMessage> ExecuteImageAsync(WorkerMessage request, Func<WorkerMessage, Task> send, CancellationToken token, string? renderBackend = null)
        => ExecuteAsync(request, send, null, token, imageValidation: true, renderBackend: renderBackend);

    public static Task<WorkerMessage> ExecuteRuntimeAsync(WorkerMessage request, Func<WorkerMessage, Task> send, CancellationToken token, string? renderBackend = null)
        => ExecuteAsync(request, send, null, token, runtimeValidation: true, renderBackend: renderBackend);

    internal static async Task<WorkerMessage> ExecuteAsync(WorkerMessage request, Func<WorkerMessage, Task> send,
        IRenderFrameSessionFactory? sessions, CancellationToken token, bool imageValidation = false, bool runtimeValidation = false,
        string? renderBackend = null)
    {
        WorkerValidationInput input = runtimeValidation ? RuntimeValidationWorkerContract.DecodeInput(request.Payload) : imageValidation
            ? ProjectImageValidationWorkerContract.DecodeInput(request.Payload)
            : WorkerValidationContract.DecodeInput(request.Payload);
        WorkerValidationContract.ValidateCorrelation(request, request);
        if (request.TaskId != input.TaskId.ToString("N") || request.SourceSha256 != input.SourceSha256
            || request.ValidationFramesPerOutput != input.FramesPerOutput)
            throw new WorkerProtocolException("验证消息与受哈希保护的封包不匹配。");
        var prepared = WorkerScriptInputs.Read(input.SnapshotBytes, token);
        using ProjectRuntimeInput? runtime = runtimeValidation
            ? await ProjectRuntimeInput.OpenAsync(prepared.Source, token, prepared.Compiler).ConfigureAwait(false) : null;
        ProjectSnapshot snapshot;
        bool packageProfile;
        Func<IBackgroundFrameSourceResolver>? imageResolverFactory = null;
        if (runtime is not null)
        {
            snapshot = runtime.Snapshot;
            packageProfile = true;
        }
        else if (imageValidation)
        {
            var image = ProjectImagePreviewSnapshotCodec.Decode(prepared.Source);
            snapshot = image.Snapshot;
            ProjectSnapshot basis = snapshot with { Background = BackgroundSource.Color };
            packageProfile = ProjectPreviewSnapshotProfile.Select(basis) == PackageProjectSnapshotCodec.Profile;
            imageResolverFactory = () =>
            {
                try { return new MemoryImageBackgroundFrameSourceResolver(image.LogicalPath, image.ImageBytes, token); }
                catch (ImageBackgroundDecodeException failure)
                {
                    throw new RenderSessionFailureException(failure.Code is "PackageImageBudgetExceeded"
                        ? "PackageImageBudgetExceeded" : "PackageImageDecodeFailed", failure.Message, innerException: failure);
                }
            };
        }
        else
        {
            packageProfile = input.Profile == PackageProjectSnapshotCodec.Profile;
            snapshot = packageProfile
                ? PackageProjectSnapshotCodec.Decode(prepared.Source)
                : StaticProjectSnapshotCodec.Decode(prepared.Source);
        }
        var composition = snapshot.Composition;
        WorkerValidationContract.ValidateGeometryAndTiming(composition.CanvasWidth, composition.CanvasHeight,
            composition.OutputDurationUs, composition.FrameRate.Numerator, composition.FrameRate.Denominator, input.FramesPerOutput);
        var factory = runtime?.CreateFactory(ReplayReason.Initial, renderBackend: renderBackend)
            ?? (imageValidation || sessions is null
            ? new ProjectRenderSessionFactory(prepared.Compiler, new FfmpegVideoFrameProvider(), ReplayReason.Initial, imageResolverFactory, renderBackend: renderBackend)
            : sessions);
        using var transparentRuntime = runtime is null ? null
            : await runtime.CreateTransparentOutputAsync(ReplayReason.Initial, renderBackend, token).ConfigureAwait(false);
        int reportBytes = 2048; // Complete report object, counts, arrays and separators are reserved before items.
        RuntimeValidationBudget budget = new(WorkerValidationContract.MaximumIssues, WorkerValidationContract.MaximumOccurrences,
            issue => Reserve(MapIssue(issue, packageProfile)), diagnostic => Reserve(MapDiagnostic(diagnostic)));
        var channel = Channel.CreateBounded<(long Sequence, RuntimeValidationProgress Progress)>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
        long sequence = 0;
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task pump = PumpAsync();
        Exception? primary = null;
        try
        {
            var result = await new RuntimeFrameValidator(factory, transparentRuntime?.Output).ValidateAsync(snapshot, new(input.FramesPerOutput),
                new InlineProgress(value => channel.Writer.TryWrite((++sequence, value))), budget, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            channel.Writer.TryComplete();
            await pump.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ValidationReportDto report = new(result.Succeeded, result.OpaqueFrames, result.TransparentFrames,
                [.. result.Issues.Select(value => MapIssue(value, packageProfile))], [.. result.Diagnostics.Select(MapDiagnostic)]);
            byte[] payload = WorkerValidationContract.EncodeReport(report, input.FramesPerOutput);
            return new(runtimeValidation ? RuntimeValidationWorkerContract.ResultKind : imageValidation ? ProjectImageValidationWorkerContract.ResultKind : WorkerValidationContract.ResultKind,
                request.RequestId, request.InputSha256, payload,
                outputSha256: WorkerProtocol.ComputeSha256(payload), sourceSha256: request.SourceSha256,
                taskId: request.TaskId, validationFramesPerOutput: input.FramesPerOutput, progressSequence: sequence);
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            channel.Writer.TryComplete();
            try { await pump.ConfigureAwait(false); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["WorkerProgressPumpFailure"] = cleanup; }
        }

        void Reserve<T>(T value)
        {
            try
            {
                int bytes = WorkerValidationContract.MeasureItem(value);
                if (bytes + 1 > WorkerValidationContract.MaximumReportBytes - reportBytes)
                    throw new RuntimeValidationBudgetExceededException();
                reportBytes += bytes + 1;
            }
            catch (WorkerProtocolException failure) when (failure.Code == "WorkerValidationReportBudgetExceeded")
            { throw new RuntimeValidationBudgetExceededException(); }
        }
        async Task PumpAsync()
        {
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    var value = item.Progress;
                    await send(new(runtimeValidation ? RuntimeValidationWorkerContract.ProgressKind : imageValidation ? ProjectImageValidationWorkerContract.ProgressKind : WorkerValidationContract.ProgressKind,
                        request.RequestId, request.InputSha256,
                        sourceSha256: request.SourceSha256, taskId: request.TaskId, validationFramesPerOutput: input.FramesPerOutput,
                        progressSequence: item.Sequence, validationProgress: new("rendering", value.CompletedFrames, value.TotalFrames,
                            value.TimeUs.ToString(CultureInfo.InvariantCulture), value.Transparent))).ConfigureAwait(false);
                }
            }
            catch { operation.Cancel(); throw; }
        }
    }

    private static ValidationIssueDto MapIssue(RuntimeValidationIssue value, bool packageProfile)
    {
        if (!packageProfile && (value.Material is not null || value.Graph is not null || value.Compilation is not null || value.SampleFailureCode is not null))
            throw new RenderSessionFailureException("WorkerUnsupportedValidationIssue", "静态验证出现未协商的完整来源，不能丢弃。");
        BackendDiagnosticDto? diagnostic = value.Compilation is { } source ? new(source.Source.Code, source.Source.IsError ? "error" : "warning", source.Source.Message,
            source.Source.Path, OriginKind: "compileSource", Source: new(source.PackageId, source.Revision, source.InstanceId, source.ExportId,
                source.Source.Code, source.Source.Message, source.Source.IsError, source.Source.Path, source.Source.Line, source.Source.Column))
            : value.Graph is { } graph ? new(graph.Code, "warning", value.Message ?? graph.Code, (string?)null, OriginKind: "imageEffect",
                ImageEffect: new(graph.InstanceId.Value, graph.ArtifactHash,
                    new(graph.Graph.Identity.PackageId, graph.Graph.Identity.Revision, graph.Graph.Identity.ExportId), graph.InputId, graph.Code))
            : value.Material is { } material ? MaterialDiagnostic(material) : null;
        return new(value.Transparent, value.TimeUs.ToString(CultureInfo.InvariantCulture), value.Code, value.Message, value.IsError,
            value.Location is null ? null : new(value.Location.Path, value.Location.Line, value.Location.Column), value.SampleFailureCode, diagnostic);

        static BackendDiagnosticDto MaterialDiagnostic(RenderMaterialDiagnostic value)
        {
            BackendDiagnosticMaterialOwnerDto owner = value.Owner switch
            {
                PreparedMaterialOwner.Script script => new("script", new(script.InstanceId.Value, script.ArtifactHash,
                    script.PackageIdentity is null ? null : new(script.PackageIdentity.PackageId, script.PackageIdentity.Revision, script.PackageIdentity.ExportId))),
                PreparedMaterialOwner.Source source => new("source", null, new(source.Reference.Path, source.Reference.Region,
                    (int)source.Slot, new(source.Reference.Material.PackageId, source.Reference.Material.Revision, source.Reference.Material.ExportId), source.Reference.Material.Parameters.Json)),
                _ => throw new RenderSessionFailureException("WorkerUnsupportedValidationIssue", "材质诊断来源无效。"),
            };
            return new(value.Code, "warning", value.Code, (string?)null, OriginKind: "material",
                Material: new(owner, new(value.Material.Identity.PackageId, value.Material.Identity.Revision, value.Material.Identity.ExportId), value.InputId, value.Code));
        }
    }
    private static ValidationDiagnosticDto MapDiagnostic(RuntimeValidationDiagnostic value)
    {
        RuntimeDiagnosticOccurrence occurrence = value.Occurrence;
        RuntimeDiagnostic runtime = occurrence.Diagnostic;
        EffectDiagnostic diagnostic = runtime.Diagnostic;
        return new(value.Transparent, new(diagnostic.Code, diagnostic.Severity.ToString().ToLowerInvariant(), diagnostic.Message,
            null, new(occurrence.Epoch, occurrence.Sequence.ToString(CultureInfo.InvariantCulture), runtime.InstanceId.Value,
                runtime.ArtifactHash, runtime.Stage, runtime.TimeUs.ToString(CultureInfo.InvariantCulture),
                diagnostic.Location is null ? null : new(diagnostic.Location.Path, diagnostic.Location.Line, diagnostic.Location.Column),
                runtime.PackageIdentity is null ? null : new(runtime.PackageIdentity.PackageId, runtime.PackageIdentity.Revision,
                    runtime.PackageIdentity.ExportId)), "runtime"));
    }
    private sealed class InlineProgress(Action<RuntimeValidationProgress> report) : IProgress<RuntimeValidationProgress>
    { public void Report(RuntimeValidationProgress value) => report(value); }
}
