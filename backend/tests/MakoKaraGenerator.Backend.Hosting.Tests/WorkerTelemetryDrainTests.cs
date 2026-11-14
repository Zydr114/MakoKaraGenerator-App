using System.Text;
using System.Text.Json;
using System.Diagnostics;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

[CollectionDefinition("WorkerTelemetryDrain", DisableParallelization = true)]
public sealed class WorkerTelemetryDrainSerialTests;

[Collection("WorkerTelemetryDrain")]
public sealed class WorkerTelemetryDrainTests
{
    [Fact]
    public async Task EmptyStderrDoesNotProduceDrainLog()
    {
        List<string> output = [];
        using StreamReader reader = new(new MemoryStream());
        await WorkerTelemetryDrain.DrainAsync(reader, 123, output.Add);
        Assert.Empty(output);
    }

    [Fact]
    public async Task RejectsUnsafeFieldsAndOversizeButReencodesValidRecords()
    {
        PerformanceEvent item = new() { TimestampUtc = DateTimeOffset.UtcNow.ToString("O"), Backend = "cpu", Frames = 1 };
        string valid = JsonSerializer.Serialize(item);
        string injected = valid.Replace("\"Backend\":\"cpu\"", "\"Backend\":\"secret/path\"", StringComparison.Ordinal);
        string extra = valid.Insert(valid.Length - 1, ",\"secret\":\"token\"");
        string input = PerformanceTelemetry.Prefix + injected + "\n"
            + PerformanceTelemetry.Prefix + new string('x', 9000) + "\n"
            + PerformanceTelemetry.Prefix + extra + "\n"
            + PerformanceTelemetry.Prefix + JsonSerializer.Serialize(item with { BackgroundCopyMs = -1 }) + "\n"
            + PerformanceTelemetry.Prefix + JsonSerializer.Serialize(item with
            {
                Kind = "exportQueueWait", Backend = "unknown", Frames = 0,
                ExportQueueWaitCalls = 1, ExportQueueWaitMs = 2.5,
            }) + "\n";
        List<string> output = [];
        using StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(input)));
        await WorkerTelemetryDrain.DrainAsync(reader, 123, output.Add);
        string[] records = output.Where(line => line.StartsWith("worker_telemetry_untrusted", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, records.Length);
        string accepted = Assert.Single(records, line => !line.Contains("exportQueueWait", StringComparison.Ordinal));
        Assert.StartsWith("worker_telemetry_untrusted pid=123 ", accepted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", accepted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", accepted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(records, line => line.Contains("\"ExportQueueWaitMs\":2.5", StringComparison.Ordinal));
        Assert.Contains(output, line => line.Contains("invalid=2 oversized=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FloodAndUnterminatedLineStayBounded()
    {
        string line = PerformanceTelemetry.Prefix + JsonSerializer.Serialize(new PerformanceEvent { TimestampUtc = DateTimeOffset.UtcNow.ToString("O") }) + "\n";
        string input = string.Concat(Enumerable.Repeat(line, 5000)) + new string('x', 100000);
        List<string> output = [];
        using StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(input)));
        await WorkerTelemetryDrain.DrainAsync(reader, 123, output.Add);
        Assert.InRange(output.Count(line => line.StartsWith("worker_telemetry_untrusted", StringComparison.Ordinal)), 1, 20);
        Assert.Contains(output, line => line.Contains("oversized=1", StringComparison.Ordinal) && !line.Contains("rateDropped=0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EofFlushesTailButSlowConsoleIsBounded()
    {
        TextWriter previous = Console.Error;
        using BlockingWriter writer = new();
        try
        {
            Console.SetError(writer);
            string line = PerformanceTelemetry.Prefix + JsonSerializer.Serialize(new PerformanceEvent { TimestampUtc = DateTimeOffset.UtcNow.ToString("O"), Frames = 1 }) + "\n";
            using StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(line)));
            Stopwatch elapsed = Stopwatch.StartNew();
            await WorkerTelemetryDrain.DrainAsync(reader, 123);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
            Assert.True(writer.Entered.Wait(TimeSpan.FromSeconds(2)));
            writer.Release.Set();
            Assert.True(SpinWait.SpinUntil(() => writer.Text.Contains("worker_telemetry_untrusted", StringComparison.Ordinal), TimeSpan.FromSeconds(2)));
        }
        finally { writer.Release.Set(); Console.SetError(previous); }

        using StringWriter tail = new();
        try
        {
            Console.SetError(tail);
            string line = PerformanceTelemetry.Prefix + JsonSerializer.Serialize(new PerformanceEvent { TimestampUtc = DateTimeOffset.UtcNow.ToString("O"), Frames = 1 }) + "\n";
            using StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(line)));
            await WorkerTelemetryDrain.DrainAsync(reader, 456);
            Assert.Contains("worker_telemetry_untrusted pid=456", tail.ToString(), StringComparison.Ordinal);
        }
        finally { Console.SetError(previous); }
    }

    private sealed class BlockingWriter : TextWriter
    {
        private readonly StringBuilder _text = new();
        internal readonly ManualResetEventSlim Entered = new(), Release = new();
        public override Encoding Encoding => Encoding.UTF8;
        internal string Text { get { lock (_text) return _text.ToString(); } }
        public override void WriteLine(string? value)
        {
            Entered.Set(); Release.Wait();
            lock (_text) _text.AppendLine(value);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Entered.Dispose(); Release.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
