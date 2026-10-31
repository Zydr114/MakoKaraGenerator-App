using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

// Author code can write stderr; these records are observations, not an audit trail.
internal static class WorkerTelemetryDrain
{
    private const int MaxLine = 8192;
    private sealed record OutputItem(string? Line, TaskCompletionSource? Barrier = null);
    private static readonly Channel<OutputItem> Output = Channel.CreateBounded<OutputItem>(new BoundedChannelOptions(128)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private static readonly Task Consumer = Task.Run(async () =>
    {
        await foreach (OutputItem item in Output.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item.Barrier is not null) { item.Barrier.TrySetResult(); continue; }
            try { Console.Error.WriteLine(item.Line); } catch { }
        }
    });

    internal static async Task DrainAsync(StreamReader reader, int processId, Action<string>? testOutput = null)
    {
        _ = Consumer;
        char[] buffer = new char[4096];
        StringBuilder line = new(MaxLine);
        bool overflow = false;
        long second = 0; int count = 0;
        long accepted = 0, invalid = 0, oversized = 0, rateDropped = 0, queueDropped = 0;
        void Enqueue(string text)
        {
            if (testOutput is not null) testOutput(text);
            else if (!Output.Writer.TryWrite(new(text))) queueDropped++;
        }
        while (await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false) is int read && read > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char ch = buffer[i];
                if (ch != '\n')
                {
                    if (!overflow && line.Length < MaxLine) line.Append(ch);
                    else overflow = true;
                    continue;
                }
                long now = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
                if (now != second) { second = now; count = 0; }
                if (overflow) oversized++;
                else if (count++ >= 10) rateDropped++;
                else if (line.Length > PerformanceTelemetry.Prefix.Length)
                {
                    bool valid = false;
                    try
                    {
                        string text = line.ToString().TrimEnd('\r');
                        if (text.StartsWith(PerformanceTelemetry.Prefix, StringComparison.Ordinal))
                        {
                            PerformanceEvent? item = JsonSerializer.Deserialize<PerformanceEvent>(text.AsSpan(PerformanceTelemetry.Prefix.Length));
                            if (item is not null && IsValid(item))
                            {
                                string sanitized = $"worker_telemetry_untrusted pid={processId} {PerformanceTelemetry.Prefix}{JsonSerializer.Serialize(item)}";
                                Enqueue(sanitized);
                                accepted++; valid = true;
                            }
                        }
                    }
                    catch (JsonException) { }
                    catch (NotSupportedException) { }
                    if (!valid) invalid++;
                }
                else invalid++;
                line.Clear(); overflow = false;
            }
        }
        if (overflow) oversized++;
        else if (line.Length > 0) invalid++;
        if (accepted + invalid + oversized + rateDropped > 0)
            Enqueue($"worker_telemetry_drain pid={processId} accepted={accepted} invalid={invalid} oversized={oversized} rateDropped={rateDropped} queueDropped={queueDropped}");
        if (testOutput is null)
        {
            TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Output.Writer.TryWrite(new(null, barrier)))
                try { await barrier.Task.WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false); }
                catch (TimeoutException) { }
        }
    }

    private static bool IsValid(PerformanceEvent e)
    {
        static bool Id(string? s) => s == "none" || s is { Length: 16 } && s.All(static c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
        static bool Number(double value) => double.IsFinite(value) && value >= 0 && value <= 1e15;
        return e.Version == 1 && DateTimeOffset.TryParseExact(e.TimestampUtc, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _) && Number(e.WindowMs)
            && e.Kind is "summary" or "frame" && e.Backend is "cpu" or "gpu" or "unknown"
            && Id(e.Request) && Id(e.Session) && e.Sequence >= 0 && e.Width is >= 0 and <= 100000 && e.Height is >= 0 and <= 100000
            && e.Frames >= 0 && e.Errors >= 0 && e.Cancelled >= 0 && e.ScriptCalls >= 0 && e.ImageReads >= 0 && e.ImageBytes >= 0
            && e.SurfaceReads >= 0 && e.SurfaceBytes >= 0 && e.ProcessAllocatedBytes >= 0 && e.Gc0 >= 0 && e.Gc1 >= 0 && e.Gc2 >= 0
            && e.Dropped >= 0 && e.DetailEvery is >= 0 and <= 1000000 && e.ScriptOverflow >= 0 && e.ProcessWorkingSetBytes >= 0
            && e.ActiveFrames is >= 0 and <= 128 && e.ActiveOverflow >= 0
            && Number(e.WallMs) && Number(e.MaxWallMs) && Number(e.PrepareMs) && Number(e.ScriptMs) && Number(e.GraphMs)
            && Number(e.MaterialMs) && Number(e.ParticleMs) && Number(e.RasterMs) && Number(e.ValidationMs) && Number(e.ReadbackMs)
            && Number(e.ProcessCpuMs) && Number(e.LongestActiveFrameMs) && e.Scripts is { Length: <= 16 }
            && e.Scripts.All(static s => s is not null && Id(s.Identity) && s.Identity != "none"
                && s.Stage is "Initialize" or "Advance" or "Evaluate" or "OnEvent" or "AdvanceVisuals" or "EvaluateVisuals" or "EvaluateEmitters" or "other"
                && s.Calls > 0 && Number(s.WallMs) && Number(s.MaxMs));
    }
}
