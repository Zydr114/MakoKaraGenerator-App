using System.Buffers.Binary;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class StaticWorkerContractTests
{
    [Fact]
    public void Open扣除头预算并隔离原输入数组()
    {
        byte[] snapshot = [1, 2, 3];
        Guid id = Guid.NewGuid();
        byte[] payload = StaticRenderWorkerContract.EncodeOpen(id, snapshot);
        snapshot[0] = 99;
        var decoded = StaticRenderWorkerContract.DecodeOpen(payload);
        Assert.Equal(id, decoded.SessionId);
        Assert.Equal([1, 2, 3], decoded.SnapshotBytes);
        Assert.Equal(WorkerProtocol.ComputeSha256([1, 2, 3]), decoded.SourceSha256);
        payload[^1] = 99;
        Assert.Equal([1, 2, 3], decoded.SnapshotBytes);
        byte[] limit = new byte[StaticRenderWorkerContract.MaximumSnapshotBytes];
        Assert.Equal(WorkerProtocol.MaxPayloadBytes, StaticRenderWorkerContract.EncodeOpen(id, limit).Length);
        WorkerProtocolException tooLarge = Assert.Throws<WorkerProtocolException>(() =>
            StaticRenderWorkerContract.EncodeOpen(id, new byte[StaticRenderWorkerContract.MaximumSnapshotBytes + 1]));
        Assert.Equal("WorkerSnapshotBudgetExceeded", tooLarge.Code);
    }

    [Fact]
    public void Frame和Close固定封包保留身份SHA微秒目标透明和序号()
    {
        var frame = Request();
        Assert.Equal(frame, StaticRenderWorkerContract.DecodeFrame(StaticRenderWorkerContract.EncodeFrame(frame)));
        var close = StaticRenderWorkerContract.DecodeClose(StaticRenderWorkerContract.EncodeClose(frame.SessionId, frame.SourceSha256, 2));
        Assert.Equal(frame.SessionId, close.SessionId); Assert.Equal(frame.SourceSha256, close.SourceSha256); Assert.Equal(2, close.Sequence);
        byte[] bytes = StaticRenderWorkerContract.EncodeFrame(frame);
        bytes[7] = 2;
        Assert.Throws<WorkerProtocolException>(() => StaticRenderWorkerContract.DecodeFrame(bytes));
        bytes[7] = 1;
        Assert.True(StaticRenderWorkerContract.DecodeFrame(bytes).Inspect);
        Assert.Equal(frame with { Inspect = true }, StaticRenderWorkerContract.DecodeFrame(StaticRenderWorkerContract.EncodeFrame(frame with { Inspect = true })));
        bytes[7] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56), int.MaxValue);
        Assert.Equal("WorkerFrameBudgetExceeded", Assert.Throws<WorkerProtocolException>(() => StaticRenderWorkerContract.DecodeFrame(bytes)).Code);
        Assert.Throws<WorkerProtocolException>(() => StaticRenderWorkerContract.EncodeFrame(frame with { Width = 1920, Height = 1080 }));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("source")]
    [InlineData("sequence")]
    [InlineData("input-sha")]
    [InlineData("output-sha")]
    [InlineData("time")]
    [InlineData("size")]
    [InlineData("transparent")]
    [InlineData("quality")]
    [InlineData("missing-diagnostics")]
    [InlineData("null-occurrences")]
    [InlineData("error")]
    public void 成功帧拒绝错身份旧序号和缺失或Error诊断(string field)
    {
        var request = Request();
        WorkerMessage result = Result(request);
        WorkerMessage invalid = field switch
        {
            "session" => result with { SessionId = Guid.NewGuid().ToString("N") },
            "source" => result with { SourceSha256 = new string('1', 64) },
            "sequence" => result with { FrameSequence = 0 },
            "input-sha" => result with { InputSha256 = new string('0', 64) },
            "output-sha" => result with { OutputSha256 = new string('0', 64) },
            "time" => result with { FrameTimeUs = 43 },
            "size" => result with { FrameStride = int.MaxValue },
            "transparent" => result with { FrameTransparentBackground = true },
            "quality" => result with { FrameQuality = 0 },
            "missing-diagnostics" => result with { Diagnostics = null },
            "null-occurrences" => result with { Diagnostics = new(null!, false) },
            "error" => result with { Diagnostics = new([Occurrence(1, "Error")], true) },
            _ => throw new ArgumentException(field),
        };
        WorkerProtocolException error = Assert.Throws<WorkerProtocolException>(() => StaticRenderWorkerContract.ValidateFrameResult(request, invalid));
        if (field == "error") Assert.Equal("WorkerRuntimeDiagnosticError", error.Code);
    }

    [Theory]
    [InlineData("empty-epoch")]
    [InlineData("first-sequence")]
    [InlineData("duplicate-sequence")]
    [InlineData("repeated-epoch")]
    [InlineData("null-metadata")]
    [InlineData("blank-instance")]
    [InlineData("blank-stage")]
    [InlineData("error-flag")]
    public void 诊断拒绝错代次非递增序号空metadata和伪Error标记(string field)
    {
        var first = Occurrence(1);
        WorkerRuntimeDiagnosticOccurrenceDto[] records = field switch
        {
            "empty-epoch" => [first with { Epoch = Guid.Empty }],
            "first-sequence" => [first with { Sequence = 2 }],
            "duplicate-sequence" => [first, first],
            "repeated-epoch" => [first, first with { Epoch = Guid.NewGuid() }, first],
            "null-metadata" => [first with { Diagnostic = null! }],
            "blank-instance" => [first with { Diagnostic = first.Diagnostic with { InstanceId = "" } }],
            "blank-stage" => [first with { Diagnostic = first.Diagnostic with { Stage = "" } }],
            "error-flag" => [first with { Diagnostic = first.Diagnostic with { Severity = "Error" } }],
            _ => throw new ArgumentException(field),
        };
        Assert.Throws<WorkerProtocolException>(() => WorkerDiagnosticsSnapshotDto.Validate(new(records, false)));
    }

    [Fact]
    public void 诊断完整快照双预算超限有稳定失败编号且不截断()
    {
        var first = Occurrence(1);
        var count = new WorkerDiagnosticsSnapshotDto(Enumerable.Range(1, 257).Select(sequence => first with { Sequence = sequence }).ToArray(), false);
        Assert.Equal("WorkerDiagnosticsBudgetExceeded", Assert.Throws<WorkerProtocolException>(() => WorkerDiagnosticsSnapshotDto.Validate(count)).Code);
        var bytes = new WorkerDiagnosticsSnapshotDto([first with { Diagnostic = first.Diagnostic with { Message = new string('A', 65_536) } }], false);
        Assert.Equal("WorkerDiagnosticsBudgetExceeded", Assert.Throws<WorkerProtocolException>(() => WorkerDiagnosticsSnapshotDto.Validate(bytes)).Code);
        Assert.Equal(257, count.Occurrences.Length);
    }

    [Fact]
    public void Runtime发生导入保留真实身份隔离输入并让游标只消费新后缀()
    {
        var diagnostic = new RuntimeDiagnostic(new("instance"), "artifact", "Render", 42,
            new(DiagnosticSeverity.Warning, "Warning", "来源", new("src/real.cs", 2, 3)));
        Guid epoch = Guid.NewGuid();
        RuntimeDiagnosticOccurrence[] input = [new(epoch, 1, diagnostic), new(epoch, 2, diagnostic with { TimeUs = 43 })];
        RuntimeDiagnosticSnapshot first = RuntimeDiagnosticSnapshot.FromOccurrences(input);
        input[0] = new(Guid.NewGuid(), 1, diagnostic);
        Assert.Equal(epoch, first.ReadAll().First().Epoch);
        Assert.Equal(new long[] { 1, 2 }, first.ReadAll().Select(value => value.Sequence));
        var cursor = new RuntimeDiagnosticCursor();
        Assert.Equal(2, cursor.ReadNew(first).Count());
        Assert.Empty(cursor.ReadNew(RuntimeDiagnosticSnapshot.FromOccurrences(first.ReadAll())));
        var next = RuntimeDiagnosticSnapshot.FromOccurrences(first.ReadAll().Append(new(epoch, 3, diagnostic)));
        Assert.Single(cursor.ReadNew(next));
        Assert.Equal(3, cursor.ConsumedCount);
        Assert.False(first.HasError);
    }

    [Fact]
    public void Runtime发生导入拒绝空或复用epoch断序和空报告并保留Error()
    {
        var diagnostic = new RuntimeDiagnostic(new("instance"), "artifact", "Render", 42, new(DiagnosticSeverity.Error, "Error", "失败"));
        Guid epoch = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => RuntimeDiagnosticSnapshot.FromOccurrences([new(Guid.Empty, 1, diagnostic)]));
        Assert.Throws<ArgumentException>(() => RuntimeDiagnosticSnapshot.FromOccurrences([new(epoch, 0, diagnostic)]));
        Assert.Throws<ArgumentException>(() => RuntimeDiagnosticSnapshot.FromOccurrences([new(epoch, 1, diagnostic), new(epoch, 1, diagnostic)]));
        Assert.Throws<ArgumentException>(() => RuntimeDiagnosticSnapshot.FromOccurrences([new(epoch, 1, diagnostic), new(Guid.NewGuid(), 1, diagnostic), new(epoch, 1, diagnostic)]));
        Assert.Throws<ArgumentException>(() => RuntimeDiagnosticSnapshot.FromOccurrences([new(epoch, 1, null!)]));
        Assert.True(RuntimeDiagnosticSnapshot.FromOccurrences([new(epoch, 1, diagnostic)]).HasError);
        Assert.Same(RuntimeDiagnosticSnapshot.Empty, RuntimeDiagnosticSnapshot.FromOccurrences([]));
    }

    [Theory]
    [InlineData(StaticRenderWorkerContract.OpenKind)]
    [InlineData(StaticRenderWorkerContract.RenderKind)]
    [InlineData(StaticRenderWorkerContract.CloseKind)]
    public void 新操作kind也验证受保护输入SHA(string kind)
    {
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Validate(new(kind, "wrong-sha", new string('0', 64), [1])));
    }

    internal static StaticWorkerFrameRequest Request() => new(Guid.NewGuid(), new string('a', 64), 1, 1, 42, false, 1);
    internal static WorkerRuntimeDiagnosticOccurrenceDto Occurrence(long sequence, string severity = "Warning") => new(
        new Guid("0c95dc85-89bb-4536-908a-4f59347b49f6"), sequence,
        new("instance", "artifact", "Render", 42, severity, "Diagnostic", "真实完整来源", new("src/code.cs", 2, 3), null));
    private static WorkerMessage Result(StaticWorkerFrameRequest request) => new(StaticRenderWorkerContract.FrameResultKind,
        "frame", WorkerProtocol.ComputeSha256(StaticRenderWorkerContract.EncodeFrame(request)), [3, 2, 1, 255],
        outputSha256: WorkerProtocol.ComputeSha256([3, 2, 1, 255]), frameWidth: 1, frameHeight: 1, frameStride: 4,
        frameTimeUs: 42, frameFormat: StaticRenderWorkerContract.RequiredPixelFormat, sessionId: request.SessionId.ToString("N"),
        sourceSha256: request.SourceSha256, frameSequence: 1, frameTransparentBackground: false, diagnostics: new([], false), frameQuality: request.Quality);
}
