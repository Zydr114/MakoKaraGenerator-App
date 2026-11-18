using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed class GpuStreamProtocolTests
{
    [Fact]
    public void SharedWireFixtureMatchesProductionHandshakeAndFrameSerializer()
    {
        JsonNode fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/GpuStreamWorkerWire.json")))!;
        GpuStreamWorkerOpen open = new("stream", "host", "1", 256, 128, true, 64UL * 1024 * 1024, 123, "/unused", new('a', 64));
        GpuDeviceIdentity device = new("fixture", 1, 2, 226, 128, new('0', 32));
        GpuFramePoolInfo pool = new(1, 2, 256, 128, 3, 393216, device);
        GpuHello hello = new("hello", 1, "fixture", "1", "stream", "host", "1", open.Handshake, "1000000");
        Assert.True(JsonNode.DeepEquals(fixture["helloAccepted"], JsonNode.Parse(GpuStreamProtocol.HelloAccepted(open, pool, hello, 2_000_000, "runtime"))));

        GpuFrameDescriptor descriptor = new(new(1, 2, 1, 1, 0), 256, 128, 1024, 0, 131072, 131072, 0, device, 1, 0, 1);
        GpuStreamDiagnosticSummary diagnostics = new(1, 1, 0, false, [new("Warning", "StyleFallback", "Fixture warning", "fixture-instance", "render")]);
        JsonNode actual = JsonNode.Parse(GpuStreamProtocol.Frame(
            open, descriptor, "1", 1, 1, "initial", 1000, "runtime", 3_000_000, 4_000_000, diagnostics,
            new PreviewFrameTimings(750, 250)))!;
        actual["producerPid"] = 123;
        Assert.True(JsonNode.DeepEquals(fixture["frame"], actual));

        // SHM 模式（跨平台无 DRM 假设、双格式/步长与拆分计时）
        PreviewDeviceIdentity cpuDevice = new("cpu", "Skia raster");
        PreviewPoolInfo shmPool = new(
            PreviewTransportKind.Shm, "cpu", 1, 2, 256, 128,
            PreviewPixelFormat.Rgba8888, 1088, 3, 417792, cpuDevice, "shm://fixture");
        Assert.True(JsonNode.DeepEquals(
            fixture["shmHelloAccepted"],
            JsonNode.Parse(GpuStreamProtocol.HelloAccepted(open, shmPool, hello, 2_000_000, "runtime"))));

        PreviewFrameDescriptor shmDescriptor = new(
            PreviewTransportKind.Shm, "cpu", new PreviewFrameToken(1, 2, 2, 2, 1),
            256, 128, PreviewPixelFormat.Rgba8888, 1088, 139264, 139264, 417792,
            cpuDevice, new PreviewFrameCounters(0, 1, 0));
        JsonNode actualShmFrame = JsonNode.Parse(GpuStreamProtocol.Frame(
            open, shmDescriptor, new PreviewFrameTimings(420, 180),
            "1", 1, 1, "initial", 2000, "runtime", 5_000_000, 5_600_000, diagnostics: null))!;
        actualShmFrame["producerPid"] = 123;
        Assert.True(JsonNode.DeepEquals(fixture["shmFrame"], actualShmFrame));
    }

    [Theory]
    [InlineData("{\"type\":\"stop\",\"Type\":\"stop\"}")]
    [InlineData("{\"type\":\"stop\",\"extra\":1}")]
    [InlineData("null")]
    public void DuplicateUnknownAndNullInputMessagesAreRejected(string json) =>
        Assert.Throws<WorkerProtocolException>(() => GpuStreamWorkerContract.Decode<GpuIdentityPacket>(System.Text.Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void DiagnosticSummaryKeepsRealCountsWithBoundedTextsAndItems()
    {
        var records = Enumerable.Range(0, 30).Select(i => new RuntimeDiagnostic(new("fixture-instance"), "artifact", "render", 0,
            new(i % 2 == 0 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error, new('界', 1000), new('界', 1000)))).ToImmutableArray();
        GpuStreamDiagnosticSummary summary = GpuStreamDiagnostics.Summarize(RuntimeDiagnosticSnapshot.FromArray(records));
        Assert.Equal(30, summary.Total); Assert.Equal(15, summary.WarningCount); Assert.Equal(15, summary.ErrorCount);
        Assert.True(summary.Truncated); Assert.Equal(4, summary.Items.Length);
        Assert.All(summary.Items, item => { Assert.True(item.Code.Length <= 96); Assert.True(item.Message.Length <= 256); });
        Assert.True(GpuStreamWorkerContract.Encode(new { diagnostics = summary }).Length < GpuStreamWorkerContract.MaximumBytes);
    }

    [Fact]
    public void WorstCaseUnicodeDiagnosticsFitAlongsideFrameWithinPacketBudget()
    {
        var records = Enumerable.Range(0, 5).Select(_ => new RuntimeDiagnostic(new(new string('界', 200)), "artifact",
            new('界', 200), 0, new(DiagnosticSeverity.Warning, new('界', 1000), new('界', 1000)))).ToImmutableArray();
        var summary = GpuStreamDiagnostics.Summarize(RuntimeDiagnosticSnapshot.FromArray(records));
        var open = new GpuStreamWorkerOpen(new('界', 128), new('界', 128), "1", 256, 128, true, 64UL * 1024 * 1024, 123, "/unused", new('a', 64));
        var device = new GpuDeviceIdentity(new('界', 256), 1, 2, 226, 128, new('0', 32));
        var descriptor = new GpuFrameDescriptor(new(ulong.MaxValue, ulong.MaxValue, 1, ulong.MaxValue, 0), 256, 128, 1024, 0, 131072, 131072, 0, device, 1, 0, 1);
        byte[] packet = GpuStreamProtocol.Frame(open, descriptor, "1", long.MaxValue, 1, new('界', 128), 1000, "runtime", 1, 2, summary);
        Assert.True(packet.Length <= GpuStreamWorkerContract.MaximumBytes);
    }
}
