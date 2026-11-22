using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Worker;

/// <summary>每个受管理进程最多持有一个固定静态 Runtime 会话，open／render／close 串行排水。</summary>
internal sealed class StaticRenderSession(string? renderBackend = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IRenderFrameSession? _session;
    private ProjectSnapshot? _snapshot;
    private Guid _id;
    private string? _sourceHash;
    private long _sequence;
    private double _openMs;

    public async Task<WorkerMessage> ExecuteAsync(WorkerMessage request, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (request.Kind == StaticRenderWorkerContract.OpenKind)
            {
                if (_session is not null) throw new WorkerProtocolException("Worker 已持有固定会话。", "WorkerSessionAlreadyOpen");
                var input = StaticRenderWorkerContract.DecodeOpen(request.Payload);
                ValidateMetadata(request, input.SessionId, input.SourceSha256, 0);
                ProjectSnapshot snapshot = StaticProjectSnapshotCodec.Decode(input.SnapshotBytes);
                var factory = new ProjectRenderSessionFactory(WorkerScriptInputs.Empty,
                    new FfmpegVideoFrameProvider(), renderBackend: renderBackend);
                long opened = System.Diagnostics.Stopwatch.GetTimestamp();
                IRenderFrameSession session = await factory.CreateAsync(snapshot, token).ConfigureAwait(false);
                _openMs = System.Diagnostics.Stopwatch.GetElapsedTime(opened).TotalMilliseconds;
                try { token.ThrowIfCancellationRequested(); }
                catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
                _id = input.SessionId; _sourceHash = input.SourceSha256; _sequence = 0;
                _snapshot = snapshot; _session = session;
                return SessionResult(request);
            }
            if (request.Kind == StaticRenderWorkerContract.RenderKind)
            {
                StaticWorkerFrameRequest input = StaticRenderWorkerContract.DecodeFrame(request.Payload);
                ValidateMetadata(request, input.SessionId, input.SourceSha256, input.Sequence);
                ValidateOwned(input.SessionId, input.SourceSha256, input.Sequence);
                if (request.FrameTransparentBackground != input.TransparentBackground || request.FrameQuality != input.Quality)
                    throw new WorkerProtocolException("帧透明模式或质量与固定封包不匹配。");
                _sequence = input.Sequence;
                using RenderFrame frame = await _session!.RenderAsync(RenderRequest.At(_snapshot!, input.TimeUs) with
                {
                    Target = new() { PixelWidth = input.Width, PixelHeight = input.Height },
                    TransparentBackground = input.TransparentBackground, Quality = (RenderQuality)input.Quality, CancellationToken = token,
                    Inspect = input.Inspect,
                }, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                WorkerDiagnosticsSnapshotDto diagnostics = EncodeDiagnostics(frame.DiagnosticSnapshot);
                if (diagnostics.HasError)
                    throw new WorkerProtocolException("实际 Runtime Error 诊断禁止像素发布。", "WorkerRuntimeDiagnosticError", diagnostics);
                if (!frame.ImageEffectDiagnostics.IsEmpty || !frame.MaterialDiagnostics.IsEmpty)
                    throw new WorkerProtocolException("静态能力产生未协商的效果／材质诊断，不能静默丢失。", "WorkerDiagnosticsUnsupported");
                if (frame.Format != PixelFormat.Bgra8888Premultiplied)
                    throw new WorkerProtocolException("实际 Runtime 像素格式不符合静态能力。");
                byte[] pixels = frame.PixelMemory.ToArray();
                WorkerMessage result = new(StaticRenderWorkerContract.FrameResultKind, request.RequestId, request.InputSha256,
                    pixels, outputSha256: WorkerProtocol.ComputeSha256(pixels), frameWidth: frame.Width, frameHeight: frame.Height,
                    frameStride: frame.Stride, frameTimeUs: frame.TimeUs, frameFormat: StaticRenderWorkerContract.RequiredPixelFormat,
                    sessionId: _id.ToString("N"), sourceSha256: _sourceHash, frameSequence: input.Sequence,
                    frameTransparentBackground: input.TransparentBackground, diagnostics: diagnostics, frameQuality: input.Quality,
                    frameInspection: EncodeInspection(input, frame, _openMs));
                StaticRenderWorkerContract.ValidateFrameResult(input, result);
                return result;
            }
            if (request.Kind == StaticRenderWorkerContract.CloseKind)
            {
                var input = StaticRenderWorkerContract.DecodeClose(request.Payload);
                ValidateMetadata(request, input.SessionId, input.SourceSha256, input.Sequence);
                ValidateOwned(input.SessionId, input.SourceSha256, input.Sequence);
                IRenderFrameSession session = _session!;
                _session = null; _snapshot = null;
                await session.DisposeAsync().ConfigureAwait(false);
                return SessionResult(request);
            }
            throw new WorkerProtocolException("未知静态会话操作。");
        }
        finally { _gate.Release(); }
    }

    /// <summary>检查帧的投影带上会话打开耗时后编码；未请求或生成失败时为空。</summary>
    internal static PreviewInspectionDto? EncodeInspection(StaticWorkerFrameRequest input, RenderFrame frame, double openMs) =>
        input.Inspect && frame.Inspection is { } inspection
            ? FrameInspectionMapper.Encode(inspection with { Cost = inspection.Cost with { SessionOpenMs = Math.Round(openMs, 3) } })
            : null;

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            IRenderFrameSession? session = _session; _session = null; _snapshot = null;
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal static WorkerDiagnosticsSnapshotDto EncodeDiagnostics(RuntimeDiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Count > WorkerDiagnosticsSnapshotDto.MaximumOccurrences)
            throw new WorkerProtocolException("实际 Runtime 诊断超过完整发生预算。", "WorkerDiagnosticsBudgetExceeded");
        WorkerRuntimeDiagnosticOccurrenceDto[] occurrences = snapshot.ReadAll().Select(occurrence =>
        {
            RuntimeDiagnostic value = occurrence.Diagnostic;
            EffectDiagnostic diagnostic = value.Diagnostic;
            return new WorkerRuntimeDiagnosticOccurrenceDto(occurrence.Epoch, occurrence.Sequence,
                new(value.InstanceId.Value, value.ArtifactHash, value.Stage, value.TimeUs, diagnostic.Severity.ToString(),
                    diagnostic.Code, diagnostic.Message, diagnostic.Location is null ? null :
                        new(diagnostic.Location.Path, diagnostic.Location.Line, diagnostic.Location.Column),
                    value.PackageIdentity is null ? null : new(value.PackageIdentity.PackageId, value.PackageIdentity.Revision, value.PackageIdentity.ExportId)));
        }).ToArray();
        var result = new WorkerDiagnosticsSnapshotDto(occurrences, snapshot.HasError);
        WorkerDiagnosticsSnapshotDto.Validate(result);
        return result;
    }

    private void ValidateOwned(Guid id, string hash, long sequence)
    {
        if (_session is null || _id != id || _sourceHash != hash)
            throw new WorkerProtocolException("请求不属于本进程固定静态会话。", "WorkerSessionConflict");
        if (sequence <= _sequence)
            throw new WorkerProtocolException("静态会话请求序号已过期。", "WorkerRequestOutOfOrder");
    }
    private static void ValidateMetadata(WorkerMessage message, Guid id, string hash, long sequence)
    {
        if (message.SessionId != id.ToString("N") || message.SourceSha256 != hash || message.FrameSequence != sequence)
            throw new WorkerProtocolException("静态会话消息字段与受哈希保护的封包不匹配。");
    }
    private static WorkerMessage SessionResult(WorkerMessage request) => new(StaticRenderWorkerContract.SessionResultKind,
        request.RequestId, request.InputSha256, sessionId: request.SessionId,
        sourceSha256: request.SourceSha256, frameSequence: request.FrameSequence);
}
