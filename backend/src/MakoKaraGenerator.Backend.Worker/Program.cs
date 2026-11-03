using System.Text.Json;
using System.Collections.Concurrent;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Processes;

namespace MakoKaraGenerator.Backend.Worker;

/// <summary>受 Hosting 管理的 Worker；render-frame-v1 使用固定输入合同调用真实 Runtime.Hosting。</summary>
internal static class Program
{
    private static readonly JsonSerializerOptions FontJson = FontWorkerContract.JsonOptions;
    private static async Task<int> Main()
    {
        PerformanceTelemetry.ConfigureFromEnvironment(Console.Error.WriteLine);
        // Fixed before any author code runs; later environment changes cannot change session devices.
        string renderBackend = RuntimeRenderBackendProbe.ResolveEnvironment();
        bool enableGpuPreview = Environment.GetEnvironmentVariable("MAKO_BACKEND_GPU_PREVIEW") == "1";
        bool enableGpuPreviewBenchmark = enableGpuPreview && Environment.GetEnvironmentVariable("MAKO_BACKEND_GPU_PREVIEW_BENCHMARK") == "1";
        bool ownsGroup = LinuxProcessGuards.InitializeCurrentProcessGroup();
        long? fileSizeLimit = null;
        if (Environment.GetEnvironmentVariable("MAKO_BACKEND_WORKER_MEDIA_LIMIT_BYTES") is { } configuredMediaLimit)
        {
            if (!long.TryParse(configuredMediaLimit, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long maximum) || maximum <= 0)
                return 1;
            fileSizeLimit = LinuxProcessGuards.ApplyCurrentFileSizeLimit(maximum) ? maximum : 0;
        }
        using CancellationTokenSource shutdown = new();
        ConcurrentDictionary<string, CancellationTokenSource> active = new(StringComparer.Ordinal);
        SemaphoreSlim outputGate = new(1, 1);
        ConcurrentDictionary<string, Task> operations = new(StringComparer.Ordinal);
        await using StaticRenderSession staticSession = new(renderBackend);
        await using PackageRenderSession packageSession = new(renderBackend, enableGpuPreview, enableGpuPreviewBenchmark);
        using SemaphoreSlim renderSessionGate = new(1, 1);
        string? renderSessionOwner = null;
        using SemaphoreSlim validationGate = new(1, 1);
        using SemaphoreSlim exportGate = new(1, 1);
        using SemaphoreSlim analysisGate = new(1, 1);
        using SemaphoreSlim preparationGate = new(1, 1);
        try
        {
            while (true)
            {
                WorkerMessage? message;
                try { message = await WorkerProtocol.ReadAsync(Console.OpenStandardInput(), shutdown.Token).ConfigureAwait(false); }
                catch (WorkerProtocolException error)
                {
                    await ReplyAsync(new("error", Guid.NewGuid().ToString("N"), error: error.Message)).ConfigureAwait(false);
                    // Framing/JSON failure has no trustworthy request boundary to resume from.
                    break;
                }
                if (message is null) break;
                switch (message.Kind)
                {
                    case "hello":
                        await ReplyAsync(new("hello", message.RequestId, processGroupId: ownsGroup ? Environment.ProcessId : null,
                            mediaFileSizeLimitBytes: fileSizeLimit)).ConfigureAwait(false);
                        break;
                    case "cancel":
                        lock (active)
                        {
                            if (active.TryGetValue(message.RequestId, out CancellationTokenSource? cancellation)) cancellation.Cancel();
                        }
                        break;
                    case "render":
                    case "render-frame-v1":
                    case WorkerRenderBackendContract.RequestKind:
                    case WorkerSceneMeasurementContract.CapabilitiesKind:
                    case WorkerSceneMeasurementContract.RequestKind:
                    case StaticRenderWorkerContract.CapabilitiesKind:
                    case StaticRenderWorkerContract.OpenKind:
                    case StaticRenderWorkerContract.RenderKind:
                    case StaticRenderWorkerContract.CloseKind:
                    case PackageRenderWorkerContract.CapabilitiesKind:
                    case PackageRenderWorkerContract.ValidationCapabilitiesKind:
                    case ProjectRuntimeInputWorkerContract.CapabilitiesKind:
                    case ProjectImagePreviewWorkerContract.CapabilitiesKind:
                    case ProjectImageValidationWorkerContract.CapabilitiesKind:
                    case ProjectRuntimeInputWorkerContract.OpenKind:
                    case RuntimeValidationWorkerContract.CapabilitiesKind:
                    case ProjectImagePreviewWorkerContract.OpenKind:
                    case PackageRenderWorkerContract.OpenKind:
                    case PackageRenderWorkerContract.RenderKind:
                    case PackageRenderWorkerContract.CloseKind:
                    case GpuStreamWorkerContract.OpenKind:
                    case GpuStreamWorkerContract.CloseKind:
                    case GpuStreamWorkerContract.ReadKind:
                    case PackageResourceWorkerContract.CapabilitiesKind:
                    case PackageResourceWorkerContract.ValidateKind:
                    case WorkerValidationContract.RequestKind:
                    case ProjectImageValidationWorkerContract.RequestKind:
                    case RuntimeValidationWorkerContract.RequestKind:
                    case ProjectImageExportWorkerContract.RequestKind:
                    case ProjectImageExportWorkerContract.CapabilitiesKind:
                    case WorkerExportContract.RequestKind:
                    case WorkerExportContract.CapabilitiesKind:
                    case WorkerAnalysisContract.RequestKind:
                    case WorkerAnalysisContract.CapabilitiesKind:
                    case WorkerAudioPreparationContract.RequestKind:
                    case FontWorkerContract.ListKind:
                    case FontWorkerContract.InspectKind:
                    case WorkerAudioPreparationContract.CapabilitiesKind:
                        CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                        bool added;
                        // One serialized call plus a just-published acknowledgement may overlap.
                        // Raw callers cannot accumulate work beyond that bounded handoff window.
                        lock (active) { added = active.Count < 2 && active.TryAdd(message.RequestId, operationCancellation); }
                        if (!added)
                        {
                            operationCancellation.Dispose();
                            await ReplyAsync(new("error", message.RequestId, message.InputSha256,
                                error: "Worker 已达到活动操作上限或 requestId 已在执行。", errorCode: "WorkerBusy",
                                sessionId: message.SessionId, sourceSha256: message.SourceSha256,
                                frameSequence: message.FrameSequence, taskId: message.TaskId,
                                validationFramesPerOutput: message.ValidationFramesPerOutput, exportFormat: message.ExportFormat,
                                exportIncludeAudio: message.ExportIncludeAudio)).ConfigureAwait(false);
                            break;
                        }
                        Task operation;
                        using (PerformanceTelemetry.BeginRequest(message.RequestId))
                        operation = message.Kind switch
                        {
                            "render" => RunRenderAsync(message, operationCancellation.Token),
                            "render-frame-v1" => RunRenderFrameAsync(message, operationCancellation.Token),
                            WorkerRenderBackendContract.RequestKind => Task.Run(() => RunRenderBackendVerificationAsync(message, operationCancellation.Token)),
                            WorkerSceneMeasurementContract.CapabilitiesKind or WorkerSceneMeasurementContract.RequestKind => Task.Run(() => RunSceneMeasurementAsync(message, operationCancellation.Token)),
                            StaticRenderWorkerContract.CapabilitiesKind => RunStaticCapabilitiesAsync(message, operationCancellation.Token),
                            WorkerValidationContract.RequestKind => RunValidationAsync(message, operationCancellation.Token),
                            ProjectImageValidationWorkerContract.RequestKind => RunImageValidationAsync(message, operationCancellation.Token),
                            RuntimeValidationWorkerContract.RequestKind => RunRuntimeValidationAsync(message, operationCancellation.Token),
                            ProjectImageExportWorkerContract.RequestKind or ProjectImageExportWorkerContract.CapabilitiesKind => RunImageExportAsync(message, operationCancellation.Token),
                            WorkerExportContract.RequestKind or WorkerExportContract.CapabilitiesKind => RunExportAsync(message, operationCancellation.Token),
                            WorkerAnalysisContract.RequestKind or WorkerAnalysisContract.CapabilitiesKind => RunAnalysisAsync(message, operationCancellation.Token),
                            WorkerAudioPreparationContract.RequestKind or WorkerAudioPreparationContract.CapabilitiesKind => RunPreparationAsync(message, operationCancellation.Token),
                            FontWorkerContract.ListKind or FontWorkerContract.InspectKind => RunFontsAsync(message, operationCancellation.Token),
                            PackageRenderWorkerContract.CapabilitiesKind or PackageRenderWorkerContract.ValidationCapabilitiesKind
                                or ProjectRuntimeInputWorkerContract.CapabilitiesKind or ProjectImagePreviewWorkerContract.CapabilitiesKind or ProjectImageValidationWorkerContract.CapabilitiesKind
                                or RuntimeValidationWorkerContract.CapabilitiesKind
                                or ProjectRuntimeInputWorkerContract.OpenKind or ProjectImagePreviewWorkerContract.OpenKind or PackageRenderWorkerContract.OpenKind
                                or PackageRenderWorkerContract.RenderKind or PackageRenderWorkerContract.CloseKind
                                or GpuStreamWorkerContract.OpenKind or GpuStreamWorkerContract.CloseKind or GpuStreamWorkerContract.ReadKind
                                => Task.Run(() => RunPackageAsync(message, operationCancellation.Token)),
                            PackageResourceWorkerContract.CapabilitiesKind or PackageResourceWorkerContract.ValidateKind
                                => Task.Run(() => RunPackageResourceAsync(message, operationCancellation.Token)),
                            _ => RunStaticAsync(message, operationCancellation.Token),
                        };
                        operations[message.RequestId] = operation;
                        _ = operation.ContinueWith(completed =>
                        {
                            // Observe failed pipe writes and retain only active operations during playback.
                            _ = completed.Exception;
                            operations.TryRemove(message.RequestId, out _);
                            lock (active)
                            {
                                active.TryRemove(message.RequestId, out CancellationTokenSource? removed);
                                removed?.Dispose();
                            }
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        break;
                    case "shutdown":
                        shutdown.Cancel();
                        lock (active) { foreach (CancellationTokenSource item in active.Values) item.Cancel(); }
                        await ReplyAsync(new("shutdown", message.RequestId)).ConfigureAwait(false);
                        goto finished;
                    default:
                        await ReplyAsync(new("error", message.RequestId, error: $"未知 Worker kind：{message.Kind}。")).ConfigureAwait(false);
                        break;
                }
            }
        }
        finally
        {
            shutdown.Cancel();
            lock (active) { foreach (CancellationTokenSource item in active.Values) item.Cancel(); }
        }

    finished:
        try { await Task.WhenAll(operations.Values).ConfigureAwait(false); } catch { }
        await PerformanceTelemetry.ShutdownAsync().ConfigureAwait(false);
        outputGate.Dispose();
        return 0;

        async Task RunRenderBackendVerificationAsync(WorkerMessage request, CancellationToken token)
        {
            try { await ReplyAsync(RenderBackendVerificationTask.Execute(request, renderBackend, token)).ConfigureAwait(false); }
            catch (OperationCanceledException)
            { await ReplyAsync(new(WorkerRenderBackendContract.ResultKind, request.RequestId, request.InputSha256, cancelled: true)).ConfigureAwait(false); }
            catch (Exception error)
            {
                string code = error is WorkerProtocolException protocol
                    ? WorkerRenderBackendContract.SafeFailureCode(protocol.Code) : WorkerRenderBackendContract.UnavailableCode;
                await ReplyAsync(new("error", request.RequestId, request.InputSha256,
                    error: "所选渲染后端未通过实际设备检查。", errorCode: code)).ConfigureAwait(false);
            }
        }

        async Task RunFontsAsync(WorkerMessage request, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested(); byte[] payload;
                if (request.Kind == FontWorkerContract.ListKind)
                { if (!request.Payload.AsSpan().SequenceEqual(FontWorkerContract.ListPayload())) throw new WorkerProtocolException("字体目录请求无效。","FontWorkerInvalid"); payload = JsonSerializer.SerializeToUtf8Bytes(new FontListResponse([.. SkiaFontInspector.AvailableFamilies()], "worker-skia"), FontJson); }
                else { var input=FontWorkerContract.DecodeInspect(request.Payload); var inspection=SkiaFontInspector.Inspect(input.Text,new MakoKaraGenerator.Text.TextLayoutOptions { FamilyName=input.Family, FontSizePx=input.FontSizePx, FontWeight=input.FontWeight, IsItalic=input.Italic, FallbackFamilyNames=input.FallbackFamilies is null ? [] : [.. input.FallbackFamilies] }); payload=JsonSerializer.SerializeToUtf8Bytes(new FontInspectResponse(inspection.RequestedFamilyAvailable, inspection.ResolvedFamily,[.. inspection.AvailableWeights],inspection.SupportsItalic,[.. inspection.Elements.Select(x=>new FontElementDiagnosticDto(x.ElementIndex,x.Utf16Start,x.Text,x.ActualFamily,x.IsFallback,x.HasMissingGlyph))],"worker-skia"),FontJson); }
                await ReplyAsync(new("result",request.RequestId,request.InputSha256,payload,outputSha256:WorkerProtocol.ComputeSha256(payload))).ConfigureAwait(false);
            } catch(Exception error) { await ReplyAsync(new("error",request.RequestId,request.InputSha256,error:"字体 Worker 执行失败。",errorCode:error is WorkerProtocolException w ? w.Code : "FontWorkerFailed")).ConfigureAwait(false); }
        }

        async Task RunSceneMeasurementAsync(WorkerMessage request, CancellationToken cancellationToken)
        {
            try
            {
                byte[] payload;
                if (request.Kind == WorkerSceneMeasurementContract.CapabilitiesKind)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!request.Payload.AsSpan().SequenceEqual(WorkerSceneMeasurementContract.CapabilitiesRequest()))
                        throw new WorkerProtocolException("测量能力查询输入无效。", "SceneMeasurementUnavailable");
                    payload = SceneMeasurementContract.EncodeBounded(new WorkerSceneMeasurementCapabilities(SceneMeasurementContract.Profile,
                        StaticProjectSnapshotCodec.EngineIdentity, [StaticProjectSnapshotCodec.Profile, StaticProjectSnapshotCodec.AnalysisProfile,
                            StaticProjectSnapshotCodec.PresetsProfile, PackageProjectSnapshotCodec.Profile, PackagePresetProjectSnapshotCodec.Profile, ProjectRuntimeInputWorkerContract.Profile]));
                }
                else payload = await SceneMeasurementWorker.ExecuteAsync(request.Payload, cancellationToken, renderBackend).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await ReplyAsync(new("result", request.RequestId, request.InputSha256, payload,
                    outputSha256: WorkerProtocol.ComputeSha256(payload))).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            { await ReplyAsync(new("result", request.RequestId, request.InputSha256, cancelled: true)).ConfigureAwait(false); }
            catch (Exception error)
            {
                string code = error switch { WorkerProtocolException failure => failure.Code, SnapshotContractException failure => failure.Code,
                    PreparedFrameException failure => failure.Code, _ => "SceneMeasurementWorkerFailed" };
                await ReplyAsync(new("error", request.RequestId, request.InputSha256, error: "静态同帧测量失败。", errorCode: code)).ConfigureAwait(false);
            }
        }

        async Task RunStaticAsync(WorkerMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await ReplyAsync(await ExecuteRenderSessionAsync(request, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                string kind = request.Kind == StaticRenderWorkerContract.RenderKind ? StaticRenderWorkerContract.FrameResultKind : StaticRenderWorkerContract.SessionResultKind;
                await ReplyAsync(new(kind, request.RequestId, request.InputSha256, cancelled: true,
                    sessionId: request.SessionId, sourceSha256: request.SourceSha256, frameSequence: request.FrameSequence)).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Exception failure = error;
                WorkerDiagnosticsSnapshotDto? diagnostics = null;
                try
                {
                    diagnostics = error is WorkerProtocolException protocol ? protocol.Diagnostics
                        : error is EffectFailureException { DiagnosticSnapshot: { } actual } ? StaticRenderSession.EncodeDiagnostics(actual) : null;
                }
                catch (WorkerProtocolException budget) { failure = budget; }
                string code = failure switch
                {
                    WorkerProtocolException rejected => rejected.Code,
                    SnapshotContractException rejected => rejected.Code,
                    EffectFailureException rejected => rejected.Code,
                    PreparedFrameException rejected => rejected.Code,
                    _ => "WorkerRuntimeFailed",
                };
                await ReplyAsync(new("error", request.RequestId, request.InputSha256, error: failure.Message,
                    sessionId: request.SessionId, sourceSha256: request.SourceSha256, frameSequence: request.FrameSequence,
                    diagnostics: diagnostics, errorCode: code)).ConfigureAwait(false);
            }
        }

        async Task RunStaticCapabilitiesAsync(WorkerMessage request, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!request.Payload.AsSpan().SequenceEqual(StaticRenderWorkerContract.EncodeCapabilitiesRequest()))
                    throw new WorkerProtocolException("静态 Worker 能力查询输入无效。", "WorkerStaticProfileMismatch");
                byte[] payload = StaticRenderWorkerContract.EncodeCapabilitiesResult(new([
                    BackendSnapshotProfiles.StaticLyricsScene,
                    BackendSnapshotProfiles.StaticLyricsSceneAnalysis,
                    BackendSnapshotProfiles.StaticLyricsScenePresets]));
                await ReplyAsync(new(StaticRenderWorkerContract.CapabilitiesResultKind, request.RequestId,
                    request.InputSha256, payload, outputSha256: WorkerProtocol.ComputeSha256(payload))).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ReplyAsync(new(StaticRenderWorkerContract.CapabilitiesResultKind, request.RequestId,
                    request.InputSha256, cancelled: true)).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                await ReplyAsync(new("error", request.RequestId, request.InputSha256, error: failure.Message,
                    errorCode: failure is WorkerProtocolException protocol ? protocol.Code : "WorkerStaticProfileMismatch")).ConfigureAwait(false);
            }
        }

        async Task RunValidationAsync(WorkerMessage request, CancellationToken cancellationToken)
        {
            try
            {
                if (!validationGate.Wait(0, cancellationToken)) throw new WorkerProtocolException("Worker 已在执行正式验证任务。", "WorkerBusy");
                WorkerMessage result;
                try { result = await StaticValidationTask.ExecuteAsync(request, ReplyAsync, cancellationToken, renderBackend).ConfigureAwait(false); }
                finally { validationGate.Release(); }
                await ReplyAsync(result).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ReplyAsync(new(WorkerValidationContract.ResultKind, request.RequestId, request.InputSha256,
                    cancelled: true, taskId: request.TaskId, sourceSha256: request.SourceSha256,
                    validationFramesPerOutput: request.ValidationFramesPerOutput)).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                string code = failure switch
                {
                    RuntimeValidationBudgetExceededException => "WorkerValidationReportBudgetExceeded",
                    WorkerProtocolException rejected => rejected.Code,
                    SnapshotContractException rejected => rejected.Code,
                    RenderSessionFailureException rejected => rejected.Code,
                    _ => "WorkerRuntimeFailed",
                };
                await ReplyAsync(new("error", request.RequestId, request.InputSha256, error: "固定静态正式验证失败。",
                    errorCode: code, taskId: request.TaskId, sourceSha256: request.SourceSha256,
                    validationFramesPerOutput: request.ValidationFramesPerOutput)).ConfigureAwait(false);
            }
        }

        async Task RunImageValidationAsync(WorkerMessage request, CancellationToken cancellationToken)
        {
            try
            {
                if (!validationGate.Wait(0, cancellationToken)) throw new WorkerProtocolException("Worker 已在执行正式验证任务。", "WorkerBusy");
                WorkerMessage result;
                try { result = await StaticValidationTask.ExecuteImageAsync(request, ReplyAsync, cancellationToken, renderBackend).ConfigureAwait(false); }
                finally { validationGate.Release(); }
                await ReplyAsync(result).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ReplyAsync(new(ProjectImageValidationWorkerContract.ResultKind, request.RequestId, request.InputSha256,
                    cancelled: true, taskId: request.TaskId, sourceSha256: request.SourceSha256,
                    validationFramesPerOutput: request.ValidationFramesPerOutput)).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                string code = failure switch
                {
                    RuntimeValidationBudgetExceededException => "WorkerValidationReportBudgetExceeded",
                    WorkerProtocolException rejected => rejected.Code,
                    SnapshotContractException rejected => rejected.Code,
                    RenderSessionFailureException rejected => rejected.Code,
                    _ => "WorkerRuntimeFailed",
                };
                await ReplyAsync(new("error", request.RequestId, request.InputSha256, error: "图片背景正式验证失败。",
                    errorCode: code, taskId: request.TaskId, sourceSha256: request.SourceSha256,
                    validationFramesPerOutput: request.ValidationFramesPerOutput)).ConfigureAwait(false);
            }
        }

        async Task RunRuntimeValidationAsync(WorkerMessage request, CancellationToken cancellationToken)
        {
            try
            {
                if (!validationGate.Wait(0, cancellationToken)) throw new WorkerProtocolException("Worker 已在执行正式验证任务。", "WorkerBusy");
                WorkerMessage result;
                try { result = await StaticValidationTask.ExecuteRuntimeAsync(request, ReplyAsync, cancellationToken, renderBackend).ConfigureAwait(false); }
                finally { validationGate.Release(); }
                await ReplyAsync(result).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ReplyAsync(new(RuntimeValidationWorkerContract.ResultKind, request.RequestId, request.InputSha256,
                    cancelled: true, taskId: request.TaskId, sourceSha256: request.SourceSha256,
                    validationFramesPerOutput: request.ValidationFramesPerOutput)).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                string code = failure switch
                {
                    RuntimeValidationBudgetExceededException => "WorkerValidationReportBudgetExceeded",
                    WorkerProtocolException rejected => rejected.Code,
                    SnapshotContractException rejected => rejected.Code,
                    RenderSessionFailureException rejected => rejected.Code,
                    _ => "WorkerRuntimeFailed",
                };
                await ReplyAsync(new("error", request.RequestId, request.InputSha256, error: "固定Runtime正式验证失败。",
                    errorCode: code, taskId: request.TaskId, sourceSha256: request.SourceSha256,
                    validationFramesPerOutput: request.ValidationFramesPerOutput)).ConfigureAwait(false);
            }
        }

        async Task RunExportAsync(WorkerMessage request, CancellationToken cancellationToken)
        {
            try
            {
                if (!exportGate.Wait(0, cancellationToken)) throw new WorkerProtocolException("Worker 已执行导出任务。", "WorkerBusy");
                WorkerMessage result;
                try { result = await StaticExportTask.ExecuteAsync(request, ownsGroup && fileSizeLimit != 0,
                    ReplyAsync, cancellationToken, renderBackend).ConfigureAwait(false); }
                finally { exportGate.Release(); }
                await ReplyAsync(result).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ReplyAsync(new(WorkerExportContract.ResultKind, request.RequestId, request.InputSha256, cancelled: true,
                    taskId: request.TaskId, sourceSha256: request.SourceSha256, exportFormat: request.ExportFormat,
                    exportIncludeAudio: request.ExportIncludeAudio)).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                string code = failure switch
                { RenderSessionFailureException value => value.Code, WorkerProtocolException value => value.Code,
                    SnapshotContractException value => value.Code, _ => "WorkerExportFailed" };
                WorkerDiagnosticsSnapshotDto? diagnostics = null; string? diagnosticsFailure = null;
                if (failure is RenderSessionFailureException { DiagnosticSnapshot: { } actual })
                {
                    try { diagnostics = StaticExportTask.EncodeDiagnostics(actual); }
                    catch (WorkerProtocolException budget) when (budget.Code == "WorkerExportReportBudgetExceeded")
                    { diagnosticsFailure = budget.Code; }
                }
                await ReplyAsync(new("error", request.RequestId, request.InputSha256, error: "固定静态导出未能完成。", errorCode: code,
                    exportDiagnostics: diagnostics, exportDiagnosticsFailureCode: diagnosticsFailure,
                    taskId: request.TaskId, sourceSha256: request.SourceSha256, exportFormat: request.ExportFormat,
                    exportIncludeAudio: request.ExportIncludeAudio)).ConfigureAwait(false);
            }
        }

        async Task RunImageExportAsync(WorkerMessage request, CancellationToken cancellationToken)
        {
            try
            {
                if (!exportGate.Wait(0, cancellationToken)) throw new WorkerProtocolException("Worker 已执行导出任务。", "WorkerBusy");
                WorkerMessage result;
                try { result = await StaticExportTask.ExecuteImageAsync(request, ownsGroup && fileSizeLimit != 0,
                    ReplyAsync, cancellationToken, renderBackend).ConfigureAwait(false); }
                finally { exportGate.Release(); }
                await ReplyAsync(result).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ReplyAsync(new(ProjectImageExportWorkerContract.ResultKind, request.RequestId, request.InputSha256, cancelled: true,
                    taskId: request.TaskId, sourceSha256: request.SourceSha256, exportFormat: request.ExportFormat,
                    exportIncludeAudio: request.ExportIncludeAudio)).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                string code = failure switch
                { RenderSessionFailureException value => value.Code, WorkerProtocolException value => value.Code,
                    SnapshotContractException value => value.Code, _ => "WorkerImageExportFailed" };
                WorkerDiagnosticsSnapshotDto? diagnostics = null; string? diagnosticsFailure = null;
                if (failure is RenderSessionFailureException { DiagnosticSnapshot: { } actual })
                {
                    try { diagnostics = StaticExportTask.EncodeDiagnostics(actual); }
                    catch (WorkerProtocolException budget) when (budget.Code == "WorkerExportReportBudgetExceeded")
                    { diagnosticsFailure = budget.Code; }
                }
                await ReplyAsync(new("error", request.RequestId, request.InputSha256, error: "图片背景导出未能完成。", errorCode: code,
                    exportDiagnostics: diagnostics, exportDiagnosticsFailureCode: diagnosticsFailure,
                    taskId: request.TaskId, sourceSha256: request.SourceSha256, exportFormat: request.ExportFormat,
                    exportIncludeAudio: request.ExportIncludeAudio)).ConfigureAwait(false);
            }
        }

        async Task RunAnalysisAsync(WorkerMessage request, CancellationToken cancellationToken)
        {
            try
            {
                if (!analysisGate.Wait(0, cancellationToken)) throw new WorkerProtocolException("Worker 已执行离线分析。", "WorkerBusy");
                WorkerMessage result;
                try { result = await StaticAudioAnalysisTask.ExecuteAsync(request, ownsGroup, ReplyAsync, cancellationToken).ConfigureAwait(false); }
                finally { analysisGate.Release(); }
                await ReplyAsync(result).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ReplyAsync(new(WorkerAnalysisContract.ResultKind, request.RequestId, request.InputSha256, cancelled: true,
                    taskId: request.TaskId, sourceSha256: request.SourceSha256)).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                string code = failure is WorkerProtocolException protocol ? protocol.Code : "WorkerAnalysisFailed";
                await ReplyAsync(new("error", request.RequestId, request.InputSha256, error: "固定离线音频分析未能完成。", errorCode: code,
                    taskId: request.TaskId, sourceSha256: request.SourceSha256)).ConfigureAwait(false);
            }
        }

        async Task RunPreparationAsync(WorkerMessage request, CancellationToken cancellationToken)
        {
            try
            {
                if (!preparationGate.Wait(0, cancellationToken))
                    throw new WorkerProtocolException("Worker 已在准备音频。", "WorkerBusy");
                WorkerMessage result;
                try { result = await AudioPreparationTask.ExecuteAsync(request, ownsGroup, cancellationToken).ConfigureAwait(false); }
                finally { preparationGate.Release(); }
                await ReplyAsync(result).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                string kind = request.Kind == WorkerAudioPreparationContract.CapabilitiesKind
                    ? WorkerAudioPreparationContract.CapabilitiesResultKind : WorkerAudioPreparationContract.ResultKind;
                await ReplyAsync(new(kind, request.RequestId, request.InputSha256,
                    cancelled: true, taskId: request.TaskId, sourceSha256: request.SourceSha256)).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                string code = failure is WorkerProtocolException protocol ? protocol.Code : "WorkerAudioPreparationFailed";
                await ReplyAsync(new("error", request.RequestId, request.InputSha256, error: "固定音频准备未能完成。",
                    errorCode: code, taskId: request.TaskId, sourceSha256: request.SourceSha256)).ConfigureAwait(false);
            }
        }

        async Task RunPackageAsync(WorkerMessage request, CancellationToken cancellationToken)
        {
            try
            {
                if (request.Kind is PackageRenderWorkerContract.CapabilitiesKind or PackageRenderWorkerContract.ValidationCapabilitiesKind
                    or ProjectRuntimeInputWorkerContract.CapabilitiesKind or ProjectImagePreviewWorkerContract.CapabilitiesKind or ProjectImageValidationWorkerContract.CapabilitiesKind
                    or RuntimeValidationWorkerContract.CapabilitiesKind)
                {
                    if (request.Payload.Length != 0)
                        throw new WorkerProtocolException("能力请求不能携带业务输入。", request.Kind switch
                        {
                            RuntimeValidationWorkerContract.CapabilitiesKind => "WorkerRuntimeValidationUnavailable",
                            ProjectImageValidationWorkerContract.CapabilitiesKind => "WorkerImageValidationCapabilitiesInvalid",
                            _ => "WorkerImagePreviewCapabilitiesInvalid",
                        });
                    cancellationToken.ThrowIfCancellationRequested();
                    string referenceDirectory = Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0");
                    ScriptReferenceIdentity references;
                    try { references = ScriptReferenceIdentity.Read(referenceDirectory); }
                    catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
                    { throw new WorkerProtocolException("包 Worker 的实际脚本参考集不可用。", "WorkerPackageUnavailable"); }
                    PackageRenderWorkerCapabilities capabilities = new(StaticProjectSnapshotCodec.EngineIdentity,
                        PackageProjectSnapshotCodec.Profile, ProjectEffectsJson.PackageProfile.PackageVersion,
                        ProjectEffectsJson.PackageProfile.SdkVersion, ProjectEffectsJson.PackageProfile.ShaderProfile,
                        references.Identity, references.Count,
                        ValidationProfiles: [PackageRenderWorkerContract.ValidationProfile],
                        SourceProfiles: [PackageProjectSnapshotCodec.Profile, PackagePresetProjectSnapshotCodec.Profile],
                        RuntimeInputProfile: request.Kind == ProjectRuntimeInputWorkerContract.CapabilitiesKind ? ProjectRuntimeInputWorkerContract.Profile : null);
                    byte[] capabilityPayload = request.Kind == RuntimeValidationWorkerContract.CapabilitiesKind
                        ? RuntimeValidationWorkerContract.EncodeCapabilities(new(RuntimeValidationWorkerContract.Profile,
                            RuntimeValidationWorkerContract.MaximumInputBytes, WorkerValidationContract.MaximumReportBytes, capabilities))
                        : request.Kind == ProjectImagePreviewWorkerContract.CapabilitiesKind
                        ? ProjectImagePreviewWorkerContract.EncodeCapabilities(new(StaticProjectSnapshotCodec.EngineIdentity,
                            BackendSnapshotProfiles.ProjectImagePreview,
                            [StaticProjectSnapshotCodec.Profile, StaticProjectSnapshotCodec.AnalysisProfile, PackageProjectSnapshotCodec.Profile],
                            ProjectImagePreviewWorkerContract.ImageDecoderProfile, MemoryImageBackgroundFrameSourceResolver.MaximumEncodedBytes,
                            MemoryImageBackgroundFrameSourceResolver.MaximumDimension, MemoryImageBackgroundFrameSourceResolver.MaximumDecodedBytes,
                            ProjectImagePreviewSnapshotCodec.MaximumBytes, StaticProjectSnapshotCodec.MaximumBytes,
                            StaticProjectSnapshotCodec.MaximumSourceValues, WorkerPackageDiagnosticsContract.Version, capabilities))
                        : request.Kind == ProjectImageValidationWorkerContract.CapabilitiesKind
                        ? ProjectImageValidationWorkerContract.EncodeCapabilities(new(StaticProjectSnapshotCodec.EngineIdentity,
                            ProjectImageValidationWorkerContract.ValidationProfile,
                            [StaticProjectSnapshotCodec.Profile, StaticProjectSnapshotCodec.AnalysisProfile, PackageProjectSnapshotCodec.Profile],
                            ProjectImageValidationWorkerContract.ImageDecoderProfile, ProjectImageValidationWorkerContract.MaximumInputBytes,
                            MemoryImageBackgroundFrameSourceResolver.MaximumEncodedBytes, MemoryImageBackgroundFrameSourceResolver.MaximumDimension,
                            MemoryImageBackgroundFrameSourceResolver.MaximumDecodedBytes, ProjectImageValidationWorkerContract.MaximumBaseSnapshotBytes,
                            StaticProjectSnapshotCodec.MaximumSourceValues, WorkerValidationContract.MaximumReportBytes,
                            WorkerPackageDiagnosticsContract.Version, capabilities))
                        : PackageRenderWorkerContract.EncodeCapabilities(capabilities);
                    string resultKind = request.Kind == ProjectRuntimeInputWorkerContract.CapabilitiesKind
                        ? ProjectRuntimeInputWorkerContract.CapabilitiesResultKind
                        : request.Kind == RuntimeValidationWorkerContract.CapabilitiesKind
                        ? RuntimeValidationWorkerContract.CapabilitiesResultKind
                        : request.Kind == ProjectImagePreviewWorkerContract.CapabilitiesKind
                        ? ProjectImagePreviewWorkerContract.CapabilitiesResultKind
                        : request.Kind == ProjectImageValidationWorkerContract.CapabilitiesKind
                        ? ProjectImageValidationWorkerContract.CapabilitiesResultKind
                        : request.Kind == PackageRenderWorkerContract.ValidationCapabilitiesKind
                        ? PackageRenderWorkerContract.ValidationCapabilitiesResultKind
                        : PackageRenderWorkerContract.CapabilitiesResultKind;
                    await ReplyAsync(new(resultKind, request.RequestId,
                        request.InputSha256, capabilityPayload, outputSha256: WorkerProtocol.ComputeSha256(capabilityPayload))).ConfigureAwait(false);
                    return;
                }
                await ReplyAsync(await ExecuteRenderSessionAsync(request, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                string kind = request.Kind switch
                {
                    ProjectRuntimeInputWorkerContract.CapabilitiesKind => ProjectRuntimeInputWorkerContract.CapabilitiesResultKind,
                    ProjectImagePreviewWorkerContract.CapabilitiesKind => ProjectImagePreviewWorkerContract.CapabilitiesResultKind,
                    ProjectImageValidationWorkerContract.CapabilitiesKind => ProjectImageValidationWorkerContract.CapabilitiesResultKind,
                    RuntimeValidationWorkerContract.CapabilitiesKind => RuntimeValidationWorkerContract.CapabilitiesResultKind,
                    PackageRenderWorkerContract.CapabilitiesKind => PackageRenderWorkerContract.CapabilitiesResultKind,
                    PackageRenderWorkerContract.ValidationCapabilitiesKind => PackageRenderWorkerContract.ValidationCapabilitiesResultKind,
                    PackageRenderWorkerContract.RenderKind => PackageRenderWorkerContract.FrameResultKind,
                    GpuStreamWorkerContract.OpenKind => GpuStreamWorkerContract.OpenResultKind,
                    GpuStreamWorkerContract.CloseKind => GpuStreamWorkerContract.CloseResultKind,
                    GpuStreamWorkerContract.ReadKind => GpuStreamWorkerContract.ReadResultKind,
                    _ => PackageRenderWorkerContract.SessionResultKind,
                };
                await ReplyAsync(new(kind, request.RequestId, request.InputSha256, cancelled: true,
                    sessionId: request.SessionId, sourceSha256: request.SourceSha256, frameSequence: request.FrameSequence)).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                Exception primary = failure;
                WorkerDiagnosticsSnapshotDto? diagnostics = null;
                try
                {
                    diagnostics = failure is WorkerProtocolException protocol ? protocol.Diagnostics
                        : failure is EffectFailureException { DiagnosticSnapshot: { } actual } ? StaticRenderSession.EncodeDiagnostics(actual) : null;
                }
                catch (WorkerProtocolException budget) { primary = budget; }
                string code = primary switch
                { WorkerProtocolException value => value.Code, SnapshotContractException value => value.Code,
                    EffectFailureException value => value.Code, PreparedFrameException value => value.Code,
                    ImageBackgroundDecodeException value => value.Code, GpuPreviewException value => value.Code,
                    ProjectEffectCompilationException => "WorkerPackageCompilationFailed", _ => "WorkerPackageRuntimeFailed" };
                await ReplyAsync(new("error", request.RequestId, request.InputSha256, error: "包 Worker Runtime 会话失败。", errorCode: code,
                    diagnostics: diagnostics, sessionId: request.SessionId, sourceSha256: request.SourceSha256, frameSequence: request.FrameSequence,
                    packageDiagnostics: primary is WorkerProtocolException package ? package.PackageDiagnostics : null)).ConfigureAwait(false);
            }
        }

        async Task RunPackageResourceAsync(WorkerMessage request, CancellationToken cancellationToken)
        {
            try
            {
                if (request.Kind == PackageResourceWorkerContract.CapabilitiesKind)
                {
                    byte[] payload = PackageResourceWorkerContract.EncodeCapabilities(PackageResourceTask.Capabilities());
                    await ReplyAsync(new(PackageResourceWorkerContract.CapabilitiesResultKind, request.RequestId,
                        request.InputSha256, payload, outputSha256: WorkerProtocol.ComputeSha256(payload))).ConfigureAwait(false);
                    return;
                }
                PackageResourceWorkerGrant grant = PackageResourceWorkerContract.DecodeGrant(request.Payload);
                PackageResourceInspection result = PackageResourceTask.Validate(grant, cancellationToken);
                byte[] response = PackageResourceWorkerContract.EncodeInspection(result);
                await ReplyAsync(new(PackageResourceWorkerContract.ValidateResultKind, request.RequestId,
                    request.InputSha256, response, outputSha256: WorkerProtocol.ComputeSha256(response))).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            { await ReplyAsync(new(PackageResourceWorkerContract.ValidateResultKind, request.RequestId, request.InputSha256, cancelled: true)).ConfigureAwait(false); }
            catch (Exception error)
            {
                string code = error is WorkerProtocolException protocol ? protocol.Code : "WorkerResourceValidationFailed";
                await ReplyAsync(new("error", request.RequestId, request.InputSha256,
                    error: "包资源检查失败。", errorCode: code)).ConfigureAwait(false);
            }
        }

        async Task<WorkerMessage> ExecuteRenderSessionAsync(WorkerMessage request, CancellationToken token)
        {
            await renderSessionGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                bool package = request.Kind is ProjectRuntimeInputWorkerContract.OpenKind or ProjectImagePreviewWorkerContract.OpenKind or PackageRenderWorkerContract.OpenKind or PackageRenderWorkerContract.RenderKind or PackageRenderWorkerContract.CloseKind
                    or GpuStreamWorkerContract.OpenKind or GpuStreamWorkerContract.CloseKind or GpuStreamWorkerContract.ReadKind;
                bool opening = request.Kind is ProjectRuntimeInputWorkerContract.OpenKind or ProjectImagePreviewWorkerContract.OpenKind or PackageRenderWorkerContract.OpenKind or StaticRenderWorkerContract.OpenKind;
                bool closing = request.Kind is PackageRenderWorkerContract.CloseKind or StaticRenderWorkerContract.CloseKind;
                string owner = package ? "package" : "static";
                if (opening && renderSessionOwner is not null)
                    throw new WorkerProtocolException("Worker 已持有固定渲染会话。", "WorkerSessionAlreadyOpen");
                if (!opening && renderSessionOwner != owner)
                    throw new WorkerProtocolException("请求不属于本进程固定渲染会话。", "WorkerSessionConflict");
                WorkerMessage result = package
                    ? await packageSession.ExecuteAsync(request, token).ConfigureAwait(false)
                    : await staticSession.ExecuteAsync(request, token).ConfigureAwait(false);
                if (opening) renderSessionOwner = owner;
                if (closing) renderSessionOwner = null;
                return result;
            }
            finally { renderSessionGate.Release(); }
        }

        async Task RunRenderAsync(WorkerMessage request, CancellationToken cancellationToken)
        {
            try
            {
                if (request.InputSha256 is null)
                    throw new WorkerProtocolException("render 缺少 inputSha256。");
                // 旧 opaque seam 保留延迟取消探针；真实像素使用独立 render-frame-v1 合同。
                if (request.DelayMilliseconds > 0)
                    await Task.Delay(request.DelayMilliseconds, cancellationToken).ConfigureAwait(false);
                await ReplyAsync(new("result", request.RequestId, request.InputSha256, request.Payload)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ReplyAsync(new("result", request.RequestId, request.InputSha256, cancelled: true)).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await ReplyAsync(new("error", request.RequestId, error: error.Message)).ConfigureAwait(false);
            }
        }

        async Task RunRenderFrameAsync(WorkerMessage request, CancellationToken cancellationToken)
        {
            try
            {
                if (request.InputSha256 is null)
                    throw new WorkerProtocolException("render-frame-v1 缺少 inputSha256。");
                RenderFrameV1WorkerRequest input = RenderFrameV1WorkerRequest.Decode(request.Payload);
                string actualInputHash = RenderFrameV1WorkerRequest.ComputeInputSha256(request.Payload);
                if (!string.Equals(actualInputHash, request.InputSha256, StringComparison.OrdinalIgnoreCase))
                    throw new WorkerProtocolException("render-frame-v1 输入 SHA-256 不匹配。");

                ProjectSnapshot snapshot = ProjectSnapshot.FromProject(Project.Create(
                    "worker-render-frame-v1",
                    Composition.Create(input.Width, input.Height, FrameRate.Fps30,
                        new RgbaColor(input.BackgroundR, input.BackgroundG, input.BackgroundB, input.BackgroundA), 1_000_000),
                    LyricsDocument.Create([])));
                var factory = new ProjectRenderSessionFactory(WorkerScriptInputs.Empty,
                    new FfmpegVideoFrameProvider(), ReplayReason.Initial, renderBackend: renderBackend);
                await using IRenderFrameSession session = await factory.CreateAsync(snapshot, cancellationToken).ConfigureAwait(false);
                using RenderFrame frame = await session.RenderAsync(
                    RenderRequest.At(snapshot, input.TimeUs) with
                    {
                        Target = new RenderTarget { PixelWidth = input.Width, PixelHeight = input.Height },
                        TransparentBackground = input.TransparentBackground,
                        CancellationToken = cancellationToken,
                    }, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Format != PixelFormat.Bgra8888Premultiplied || frame.Width != input.Width
                    || frame.Height != input.Height || frame.Stride != input.Width * 4
                    || frame.PixelMemory.Length != input.Width * input.Height * 4 || frame.TimeUs != input.TimeUs)
                    throw new WorkerProtocolException("Runtime.Hosting 返回帧不符合 render-frame-v1 输出合同。");
                byte[] pixels = frame.PixelMemory.ToArray();
                string outputHash = WorkerProtocol.ComputeSha256(pixels);
                await ReplyAsync(new("result", request.RequestId, request.InputSha256, pixels,
                    outputSha256: outputHash, frameWidth: frame.Width, frameHeight: frame.Height,
                    frameStride: frame.Stride, frameTimeUs: frame.TimeUs,
                    frameFormat: RenderFrameV1WorkerResult.RequiredPixelFormat)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ReplyAsync(new("result", request.RequestId, request.InputSha256, cancelled: true)).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await ReplyAsync(new("error", request.RequestId, error: error.Message)).ConfigureAwait(false);
            }
        }

        async Task ReplyAsync(WorkerMessage response)
        {
            await outputGate.WaitAsync().ConfigureAwait(false);
            try { await WorkerProtocol.WriteAsync(Console.OpenStandardOutput(), response).ConfigureAwait(false); }
            finally { outputGate.Release(); }
        }
    }
}
