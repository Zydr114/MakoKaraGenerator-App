using System.Collections.Immutable;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed partial class StaticAnalysisExportTaskTests
{
    [Fact]
    public async Task 非错误逐帧运行时诊断超过512帧仍去重完成导出且保持序号连续()
    {
        using TemporaryDirectory directory = new();
        string candidate = directory.FilePath("dedup-555-frames.mp4");
        ProjectSnapshot snapshot = StaticExportSnapshot(outputDurationUs: 18_500_000);
        WorkerMessage request = StaticExportRequest(snapshot, candidate);
        var factory = new DiagnosticEmittingFactory((index, timeUs) => RuntimeDiagnosticSnapshot.FromArray([
            new RuntimeDiagnostic(new("group-a"), "hash-a", "RenderGroupIsolation", timeUs,
                new(DiagnosticSeverity.Warning, "GroupIsolationChangesOrder", "组隔离改变了层叠顺序。")),
            new RuntimeDiagnostic(new("group-a"), "hash-a", "RenderImageEffects", timeUs,
                new(DiagnosticSeverity.Info, "GroupImageEffectNoVisibleChildren", "当前组图像效果没有可见子项。")),
        ]));

        WorkerMessage response = await StaticExportTask.ExecuteAsync(request, true, _ => Task.CompletedTask, factory, CancellationToken.None);

        ExportReportDto report = WorkerExportContract.DecodeReport(response.Payload,
            new("opaqueMp4", false, 8, 4, 30, 1, 555, 18_500_000));
        Assert.True(report.StrictlyVerified);
        Assert.Equal(555, report.DecodedFrames);
        Assert.Equal(555, report.FramesWritten);
        Assert.Equal(2, report.Diagnostics.Length);
        Assert.Equal("GroupIsolationChangesOrder", report.Diagnostics[0].Code);
        Assert.Equal("warning", report.Diagnostics[0].Severity);
        Assert.Equal("1", report.Diagnostics[0].Occurrence!.Sequence);
        Assert.Equal("0", report.Diagnostics[0].Occurrence!.TimeUs);
        Assert.Equal("GroupImageEffectNoVisibleChildren", report.Diagnostics[1].Code);
        Assert.Equal("info", report.Diagnostics[1].Severity);
        Assert.Equal("2", report.Diagnostics[1].Occurrence!.Sequence);
        Assert.Equal(report.Diagnostics[0].Occurrence!.Epoch, report.Diagnostics[1].Occurrence!.Epoch);
        Assert.True(File.Exists(candidate));
    }

    [Fact]
    public async Task 非错误诊断每帧文案不同超过512条与256警告时优雅封顶不中断导出()
    {
        using TemporaryDirectory directory = new();
        string candidate = directory.FilePath("capped-555-frames.mp4");
        ProjectSnapshot snapshot = StaticExportSnapshot(outputDurationUs: 18_500_000);
        WorkerMessage request = StaticExportRequest(snapshot, candidate);
        var factory = new DiagnosticEmittingFactory((index, timeUs) => RuntimeDiagnosticSnapshot.FromArray([
            new RuntimeDiagnostic(new("fx-1"), "hash-1", "Render", timeUs,
                new(DiagnosticSeverity.Warning, "PerFrameNotice", $"frame-{index}-notice")),
        ]));

        WorkerMessage response = await StaticExportTask.ExecuteAsync(request, true, _ => Task.CompletedTask, factory, CancellationToken.None);

        ExportReportDto report = WorkerExportContract.DecodeReport(response.Payload,
            new("opaqueMp4", false, 8, 4, 30, 1, 555, 18_500_000));
        Assert.Equal(555, report.DecodedFrames);
        Assert.Equal(WorkerExportContract.MaximumOccurrences, report.Diagnostics.Length);
        Assert.Equal(WorkerExportContract.MaximumWarnings, report.Warnings.Length);
        Assert.True(File.Exists(candidate));
    }

    [Fact]
    public async Task 同一Epoch跨帧部分去重后仍保持连续序号()
    {
        using TemporaryDirectory directory = new();
        string candidate = directory.FilePath("same-epoch-dedup.mp4");
        ProjectSnapshot snapshot = StaticExportSnapshot(outputDurationUs: 100_000); // 3 frames @ 30fps
        WorkerMessage request = StaticExportRequest(snapshot, candidate);
        RuntimeDiagnosticBuffer shared = new();
        var factory = new DiagnosticEmittingFactory((index, timeUs) =>
        {
            if (index is 0 or 1)
                shared.Add(new RuntimeDiagnostic(new("fx-1"), "hash-1", "Render", timeUs,
                    new(DiagnosticSeverity.Warning, "RepeatedCode", "same message")));
            else if (index == 2)
                shared.Add(new RuntimeDiagnostic(new("fx-1"), "hash-1", "Render", timeUs,
                    new(DiagnosticSeverity.Info, "SecondCode", "second message")));
            return shared.Snapshot();
        });

        WorkerMessage response = await StaticExportTask.ExecuteAsync(request, true, _ => Task.CompletedTask, factory, CancellationToken.None);

        ExportReportDto report = WorkerExportContract.DecodeReport(response.Payload,
            new("opaqueMp4", false, 8, 4, 30, 1, 3, 100_000));
        Assert.Equal(2, report.Diagnostics.Length);
        Assert.Equal("RepeatedCode", report.Diagnostics[0].Code);
        Assert.Equal("1", report.Diagnostics[0].Occurrence!.Sequence);
        Assert.Equal("SecondCode", report.Diagnostics[1].Code);
        Assert.Equal("2", report.Diagnostics[1].Occurrence!.Sequence);
        Assert.Equal(report.Diagnostics[0].Occurrence!.Epoch, report.Diagnostics[1].Occurrence!.Epoch);
    }

    [Fact]
    public async Task 导出渲染抛出EffectFailureException与PreparedFrameException保留真实失败码与诊断快照()
    {
        using TemporaryDirectory directory = new();
        ProjectSnapshot snapshot = StaticExportSnapshot(outputDurationUs: 100_000);

        // 1. EffectFailureException with Error DiagnosticSnapshot
        var errorDiag = new RuntimeDiagnostic(new("fx-err"), "hash-err", "Render", 0,
            new(DiagnosticSeverity.Error, "AuthorHardError", "author reported fatal error"));
        RuntimeDiagnosticSnapshot errorSnapshot = RuntimeDiagnosticSnapshot.FromArray([errorDiag]);
        var effectWithSnapshot = new EffectFailureException(new("fx-err"), "hash-err", "Render", 0,
            "AuthorHardError", new InvalidOperationException("boom"))
        {
            Diagnostic = errorDiag,
            DiagnosticSnapshot = errorSnapshot,
        };
        WorkerMessage req1 = StaticExportRequest(snapshot, directory.FilePath("fail-1.mp4"));
        var f1 = await Assert.ThrowsAsync<RenderSessionFailureException>(() => StaticExportTask.ExecuteAsync(
            req1, true, _ => Task.CompletedTask, new FailingSourceFactory(effectWithSnapshot), CancellationToken.None));
        Assert.Equal("AuthorHardError", f1.Code);
        Assert.NotNull(f1.DiagnosticSnapshot);
        Assert.True(f1.DiagnosticSnapshot!.HasError);
        WorkerDiagnosticsSnapshotDto encoded = StaticExportTask.EncodeDiagnostics(f1.DiagnosticSnapshot);
        Assert.Single(encoded.Occurrences);
        Assert.Equal("AuthorHardError", encoded.Occurrences[0].Diagnostic.Code);

        // 2. EffectFailureException without DiagnosticSnapshot (e.g. GroupIsolation / CaptureBudgetExceeded)
        var effectNoSnapshot = new EffectFailureException(new("group-iso"), "hash-iso", "RenderGroupIsolation", 0,
            "CaptureBudgetExceeded", new PreparedFrameException("CaptureBudgetExceeded", "too large"));
        WorkerMessage req2 = StaticExportRequest(snapshot, directory.FilePath("fail-2.mp4"));
        var f2 = await Assert.ThrowsAsync<RenderSessionFailureException>(() => StaticExportTask.ExecuteAsync(
            req2, true, _ => Task.CompletedTask, new FailingSourceFactory(effectNoSnapshot), CancellationToken.None));
        Assert.Equal("CaptureBudgetExceeded", f2.Code);
        Assert.Null(f2.DiagnosticSnapshot);

        // 3. Direct PreparedFrameException (e.g. GroupCompositionBudgetExceeded)
        var prepared = new PreparedFrameException("GroupCompositionBudgetExceeded", "too many nested groups");
        WorkerMessage req3 = StaticExportRequest(snapshot, directory.FilePath("fail-3.mp4"));
        var f3 = await Assert.ThrowsAsync<RenderSessionFailureException>(() => StaticExportTask.ExecuteAsync(
            req3, true, _ => Task.CompletedTask, new FailingSourceFactory(prepared), CancellationToken.None));
        Assert.Equal("GroupCompositionBudgetExceeded", f3.Code);
        Assert.Null(f3.DiagnosticSnapshot);

        // 4. Frame with Error DiagnosticSnapshot still aborts immediately with WorkerRuntimeDiagnosticError
        WorkerMessage req4 = StaticExportRequest(snapshot, directory.FilePath("fail-4.mp4"));
        var errorFrameFactory = new DiagnosticEmittingFactory((_, _) => errorSnapshot);
        var f4 = await Assert.ThrowsAsync<RenderSessionFailureException>(() => StaticExportTask.ExecuteAsync(
            req4, true, _ => Task.CompletedTask, errorFrameFactory, CancellationToken.None));
        Assert.Equal("WorkerRuntimeDiagnosticError", f4.Code);
        Assert.Same(errorSnapshot, f4.DiagnosticSnapshot);
    }

    private static ProjectSnapshot StaticExportSnapshot(long outputDurationUs)
    {
        Project project = Project.Create("diag export",
            Composition.Create(8, 4, FrameRate.Fps30, outputDurationUs: outputDurationUs),
            LyricsDocument.Create([]));
        return ProjectSnapshot.FromProject(project);
    }

    private static WorkerMessage StaticExportRequest(ProjectSnapshot snapshot, string candidate)
    {
        string profile = StaticProjectSnapshotCodec.SelectProfile(snapshot);
        byte[] bytes = StaticProjectSnapshotCodec.Encode(snapshot, profile);
        Guid id = Guid.NewGuid();
        byte[] payload = WorkerExportContract.EncodeInput(id, Metadata(candidate, profile), bytes);
        return new(WorkerExportContract.RequestKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload, taskId: id.ToString("N"),
            sourceSha256: WorkerProtocol.ComputeSha256(bytes), exportFormat: "opaqueMp4", exportIncludeAudio: false);
    }

    private sealed class DiagnosticEmittingFactory(Func<int, long, RuntimeDiagnosticSnapshot> diagnosticsForFrame)
        : IRenderFrameSessionFactory
    {
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IRenderFrameSession>(new Session(diagnosticsForFrame));

        private sealed class Session(Func<int, long, RuntimeDiagnosticSnapshot> diagnosticsForFrame) : IRenderFrameSession
        {
            private int _index;
            public ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            {
                var target = request.ResolveTarget();
                RuntimeDiagnosticSnapshot snapshot = diagnosticsForFrame(_index++, request.TimeUs);
                return ValueTask.FromResult(new RenderFrame(new byte[target.PixelWidth * target.PixelHeight * 4], target,
                    PixelFormat.Bgra8888Premultiplied, request.TimeUs) { DiagnosticSnapshot = snapshot });
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
