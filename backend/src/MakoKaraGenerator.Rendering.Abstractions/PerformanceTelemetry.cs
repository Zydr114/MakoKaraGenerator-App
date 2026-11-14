using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

#pragma warning disable CS1591 // Fixed wire fields are documented together on the event type.
namespace MakoKaraGenerator.Rendering;

/// <summary>Inclusive host wall times; these are not GPU hardware timings.</summary>
public enum PerformancePhase { Prepare, Script, Graph, Material, Particle, Raster, Validation, Readback, BackgroundAcquire, BackgroundCopy, Draw, GpuSubmit }

public sealed record PerformanceScript(string Identity, string Stage, long Calls, double WallMs, double MaxMs);

/// <summary>A fixed-shape telemetry event. No user-controlled text is included.</summary>
public sealed record PerformanceEvent
{
    public int Version { get; init; } = 1;
    public string TimestampUtc { get; init; } = "";
    public double WindowMs { get; init; }
    public string Kind { get; init; } = "summary";
    public string Backend { get; init; } = "unknown";
    public string Request { get; init; } = "none";
    public string Session { get; init; } = "none";
    public long Sequence { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long Frames { get; init; }
    public long Errors { get; init; }
    public long Cancelled { get; init; }
    public double WallMs { get; init; }
    public double MaxWallMs { get; init; }
    public double PrepareMs { get; init; }
    public double ScriptMs { get; init; }
    public double GraphMs { get; init; }
    public double MaterialMs { get; init; }
    public double ParticleMs { get; init; }
    public double RasterMs { get; init; }
    public double ValidationMs { get; init; }
    public double ReadbackMs { get; init; }
    public double BackgroundAcquireMs { get; init; }
    public double BackgroundCopyMs { get; init; }
    public double DrawMs { get; init; }
    public double GpuSubmitMs { get; init; }
    public long ExportQueueWaitCalls { get; init; }
    public double ExportQueueWaitMs { get; init; }
    public long ScriptCalls { get; init; }
    public long ImageReads { get; init; }
    public long ImageBytes { get; init; }
    public long SurfaceReads { get; init; }
    public long SurfaceBytes { get; init; }
    public long ProcessAllocatedBytes { get; init; }
    public int Gc0 { get; init; }
    public int Gc1 { get; init; }
    public int Gc2 { get; init; }
    public long Dropped { get; init; }
    public int DetailEvery { get; init; }
    public long ScriptOverflow { get; init; }
    public PerformanceScript[] Scripts { get; init; } = [];
    public double ProcessCpuMs { get; init; }
    public long ProcessWorkingSetBytes { get; init; }
    public int ActiveFrames { get; init; }
    public double LongestActiveFrameMs { get; init; }
    public long ActiveOverflow { get; init; }
}

/// <summary>Process-local, bounded telemetry. Sinks are invoked only on a background task.</summary>
public static class PerformanceTelemetry
{
    public const string Prefix = "MAKO_PERFORMANCE_V1 ";
    public static bool IsEnabled => Volatile.Read(ref _collector) is not null;
    public static bool HasFrame => Current.Value is not null;
    private static readonly AsyncLocal<FrameState?> Current = new();
    private static readonly AsyncLocal<string?> Request = new();
    private static Collector? _collector;

    /// <summary>Configure once per host. Passing null disables collection.</summary>
    public static void Configure(Action<string>? sink, int detailEvery = 0, int intervalSeconds = 10, int maxEventsPerSecond = 10)
    {
        Collector? next = sink is null ? null : new Collector(sink, Math.Clamp(detailEvery, 0, 1000000),
            Math.Clamp(intervalSeconds, 1, 3600), Math.Clamp(maxEventsPerSecond, 1, 100));
        Collector? previous = Interlocked.Exchange(ref _collector, next);
        previous?.Stop();
    }

    /// <summary>Read MAKO_PERFORMANCE_* settings once at process startup.</summary>
    public static void ConfigureFromEnvironment(Action<string> sink)
    {
        string? mode = Environment.GetEnvironmentVariable("MAKO_PERFORMANCE_MODE");
        if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase)) { Configure(null); return; }
        bool detailed = string.Equals(mode, "detailed", StringComparison.OrdinalIgnoreCase);
        static int Setting(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;
        Configure(sink, detailed ? Math.Max(1, Setting("MAKO_PERFORMANCE_DETAIL_EVERY", 1)) : 0,
            Setting("MAKO_PERFORMANCE_INTERVAL_SECONDS", 10), Setting("MAKO_PERFORMANCE_MAX_EVENTS_PER_SECOND", 10));
    }

    /// <summary>Stop accepting frames and allow a bounded period for the final summary.</summary>
    public static async Task ShutdownAsync()
    {
        Collector? collector = Interlocked.Exchange(ref _collector, null);
        if (collector is null) return;
        collector.Stop();
        await Task.WhenAny(collector.Completion, Task.Delay(500)).ConfigureAwait(false);
    }

    /// <summary>Associate an operation using a short hash, never the input identifier itself.</summary>
    public static IDisposable BeginRequest(string requestId)
    {
        if (Volatile.Read(ref _collector) is null) return EmptyScope.Instance;
        string? previous = Request.Value;
        Request.Value = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)))[..16];
        return new RequestScope(previous);
    }

    /// <summary>开始一帧。capture 为 true 时即使全局收集器关闭也采集本帧，供检查投影用 <see cref="FrameScope.Capture"/> 读取，不进入日志。</summary>
    public static FrameScope BeginFrame(string session, string backend, long sequence, int width, int height, bool capture = false)
    {
        Collector? collector = Volatile.Read(ref _collector);
        if (collector is null && !capture) return default;
        bool logSampled = collector?.Sample() ?? false;
        FrameState state = new(collector, Current.Value, Request.Value ?? "none", SafeId(session),
            backend is "cpu" or "gpu" ? backend : "unknown", sequence, width, height, capture || logSampled, logSampled);
        Current.Value = state;
        return new FrameScope(state);
    }

    public static PhaseScope Measure(PerformancePhase phase)
    {
        FrameState? state = Current.Value;
        return state is null || Volatile.Read(ref state.Closed) != 0 ? default : new PhaseScope(state, phase, Stopwatch.GetTimestamp());
    }

    /// <summary>Measure export queue admission wall time, including failed or cancelled waits; independent of render frames and encoding time.</summary>
    public static ExportQueueWaitScope MeasureExportQueueWait(string session, long sequence)
    {
        Collector? collector = Volatile.Read(ref _collector);
        return collector is null ? default : new ExportQueueWaitScope(collector, Request.Value ?? "none", SafeId(session), sequence,
            collector.SampleExportQueueWait(), Stopwatch.GetTimestamp());
    }

    public static void RecordReadback(bool image, long requestedBytes)
    {
        FrameState? state = Current.Value;
        if (state is null || Volatile.Read(ref state.Closed) != 0 || requestedBytes < 0) return;
        if (image) { Interlocked.Increment(ref state.ImageReads); Interlocked.Add(ref state.ImageBytes, requestedBytes); }
        else { Interlocked.Increment(ref state.SurfaceReads); Interlocked.Add(ref state.SurfaceBytes, requestedBytes); }
    }

    /// <summary>Measure actual author code, identifying sampled instances by hash only.</summary>
    public static ScriptScope MeasureScript(string identity, string stage)
    {
        FrameState? state = Current.Value;
        if (state is null || Volatile.Read(ref state.Closed) != 0) return default;
        string? id = state.Sampled ? SafeId(identity) : null;
        string safeStage = stage is "Initialize" or "Advance" or "Evaluate" or "OnEvent" or "AdvanceVisuals" or "EvaluateVisuals" or "EvaluateEmitters" ? stage : "other";
        return new ScriptScope(state, id, safeStage, Stopwatch.GetTimestamp());
    }

    public static string CreateScriptIdentity(string identity) => SafeId(identity);

    public static ScriptScope MeasureScriptIdentity(string identity, string stage)
    {
        FrameState? state = Current.Value;
        if (state is null || Volatile.Read(ref state.Closed) != 0) return default;
        string? id = state.Sampled && identity.Length == 16 && identity.All(static c => c is >= '0' and <= '9' or >= 'A' and <= 'F') ? identity : null;
        string safeStage = stage is "Initialize" or "Advance" or "Evaluate" or "OnEvent" or "AdvanceVisuals" or "EvaluateVisuals" or "EvaluateEmitters" ? stage : "other";
        return new ScriptScope(state, id, safeStage, Stopwatch.GetTimestamp());
    }

    private static string SafeId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    private sealed class RequestScope(string? previous) : IDisposable
    {
        private int _closed;
        public void Dispose() { if (Interlocked.Exchange(ref _closed, 1) == 0) Request.Value = previous; }
    }
    private sealed class EmptyScope : IDisposable { public static EmptyScope Instance { get; } = new(); public void Dispose() { } }

    internal sealed class FrameState(Collector? collector, FrameState? previous, string request, string session,
        string backend, long sequence, int width, int height, bool sampled, bool logSampled)
    {
        internal readonly Collector? Collector = collector;
        internal readonly bool LogSampled = logSampled;
        internal readonly FrameState? Previous = previous;
        internal readonly string Request = request, Session = session, Backend = backend;
        internal readonly long Sequence = sequence, Started = Stopwatch.GetTimestamp(), Allocated = GC.GetTotalAllocatedBytes(false);
        internal readonly int Width = width, Height = height, Gc0 = GC.CollectionCount(0), Gc1 = GC.CollectionCount(1), Gc2 = GC.CollectionCount(2);
        internal readonly long[] Ticks = new long[12];
        internal readonly bool Sampled = sampled;
        internal readonly int ActiveSlot = collector?.StartFrame() ?? -1;
        internal readonly Dictionary<(string, string), PerformanceScript> Scripts = [];
        internal long ScriptOverflow;
        internal long ScriptCalls, ImageReads, ImageBytes, SurfaceReads, SurfaceBytes;
        internal int Outcome, Closed;
    }

    public readonly struct FrameScope : IDisposable
    {
        private readonly FrameState? _state;
        internal FrameScope(FrameState state) => _state = state;
        public void SetOutcome(string outcome) { if (_state is not null) _state.Outcome = outcome == "ok" ? 1 : outcome == "cancelled" ? 2 : 0; }
        /// <summary>读取本帧到目前为止的采集结果；没有开始采集时为 null。</summary>
        public PerformanceEvent? Capture()
        {
            FrameState? state = _state;
            if (state is null) return null;
            try { return Snapshot(state); }
            catch { return null; }
        }
        public void Dispose()
        {
            FrameState? state = _state;
            if (state is null || Interlocked.Exchange(ref state.Closed, 1) != 0) return;
            state.Collector?.EndFrame(state.ActiveSlot);
            if (ReferenceEquals(Current.Value, state))
            {
                FrameState? previous = state.Previous;
                while (previous is not null && Volatile.Read(ref previous.Closed) != 0) previous = previous.Previous;
                Current.Value = previous;
            }
            if (state.Collector is not { } collector) return;
            try { collector.Enqueue(Snapshot(state)); }
            catch { /* Telemetry must not replace the render outcome. */ }
        }

        private static PerformanceEvent Snapshot(FrameState state)
        {
                double ms = 1000d / Stopwatch.Frequency;
                PerformanceScript[] scripts;
                long overflow;
                lock (state.Scripts) { scripts = state.Scripts.Values.ToArray(); overflow = state.ScriptOverflow; }
                double wallMs = (Stopwatch.GetTimestamp() - state.Started) * ms;
                return new PerformanceEvent { Kind = "frame", Backend = state.Backend, Request = state.Request,
                    Session = state.Session, Sequence = state.Sequence, Width = state.Width, Height = state.Height, Frames = 1,
                    Errors = state.Outcome == 0 ? 1 : 0, Cancelled = state.Outcome == 2 ? 1 : 0,
                    WallMs = wallMs, MaxWallMs = wallMs, PrepareMs = state.Ticks[0] * ms,
                    ScriptMs = state.Ticks[1] * ms, GraphMs = state.Ticks[2] * ms, MaterialMs = state.Ticks[3] * ms,
                    ParticleMs = state.Ticks[4] * ms, RasterMs = state.Ticks[5] * ms, ValidationMs = state.Ticks[6] * ms,
                    ReadbackMs = state.Ticks[7] * ms, BackgroundAcquireMs = state.Ticks[8] * ms,
                    BackgroundCopyMs = state.Ticks[9] * ms, DrawMs = state.Ticks[10] * ms,
                    GpuSubmitMs = state.Ticks[11] * ms, ScriptCalls = state.ScriptCalls, ImageReads = state.ImageReads,
                    ImageBytes = state.ImageBytes, SurfaceReads = state.SurfaceReads, SurfaceBytes = state.SurfaceBytes,
                    ProcessAllocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(false) - state.Allocated),
                    Gc0 = GC.CollectionCount(0) - state.Gc0, Gc1 = GC.CollectionCount(1) - state.Gc1, Gc2 = GC.CollectionCount(2) - state.Gc2,
                    DetailEvery = state.LogSampled ? state.Collector?.DetailEvery ?? 0 : 0, Scripts = scripts, ScriptOverflow = overflow };
        }
    }

    public readonly struct ScriptScope : IDisposable
    {
        private readonly ScriptLease? _lease;
        internal ScriptScope(FrameState state, string? identity, string stage, long start) => _lease = new(state, identity, stage, start);
        public void Dispose()
        {
            ScriptLease? lease = _lease;
            if (lease is null || Interlocked.Exchange(ref lease.Closed, 1) != 0) return;
            FrameState state = lease.State;
            if (Volatile.Read(ref state.Closed) != 0) return;
            long ticks = Stopwatch.GetTimestamp() - lease.Start;
            Interlocked.Add(ref state.Ticks[(int)PerformancePhase.Script], ticks);
            Interlocked.Increment(ref state.ScriptCalls);
            if (lease.Identity is null) return;
            double ms = ticks * (1000d / Stopwatch.Frequency);
            lock (state.Scripts)
            {
                if (Volatile.Read(ref state.Closed) != 0) return;
                var key = (lease.Identity, lease.Stage);
                if (state.Scripts.TryGetValue(key, out PerformanceScript? existing))
                    state.Scripts[key] = existing with { Calls = existing.Calls + 1, WallMs = existing.WallMs + ms, MaxMs = Math.Max(existing.MaxMs, ms) };
                else if (state.Scripts.Count < 16) state.Scripts.Add(key, new(lease.Identity, lease.Stage, 1, ms, ms));
                else state.ScriptOverflow++;
            }
        }
    }

    private sealed class ScriptLease(FrameState state, string? identity, string stage, long start)
    {
        internal readonly FrameState State = state;
        internal readonly string? Identity = identity;
        internal readonly string Stage = stage;
        internal readonly long Start = start;
        internal int Closed;
    }

    public readonly struct PhaseScope : IDisposable
    {
        private readonly PhaseLease? _lease;
        internal PhaseScope(FrameState state, PerformancePhase phase, long start) => _lease = new(state, phase, start);
        public void Dispose()
        {
            PhaseLease? lease = _lease;
            if (lease is null || Interlocked.Exchange(ref lease.Closed, 1) != 0 || (uint)lease.Phase >= (uint)lease.State.Ticks.Length || Volatile.Read(ref lease.State.Closed) != 0) return;
            Interlocked.Add(ref lease.State.Ticks[(int)lease.Phase], Stopwatch.GetTimestamp() - lease.Start);
        }
    }

    private sealed class PhaseLease(FrameState state, PerformancePhase phase, long start)
    {
        internal readonly FrameState State = state;
        internal readonly PerformancePhase Phase = phase;
        internal readonly long Start = start;
        internal int Closed;
    }

    public readonly struct ExportQueueWaitScope : IDisposable
    {
        private readonly ExportQueueWaitLease? _lease;
        internal ExportQueueWaitScope(Collector collector, string request, string session, long sequence, bool sampled, long start) =>
            _lease = new(collector, request, session, sequence, sampled, start);
        public void Dispose()
        {
            ExportQueueWaitLease? lease = _lease;
            if (lease is null || Interlocked.Exchange(ref lease.Closed, 1) != 0) return;
            try
            {
                lease.Collector.Enqueue(new PerformanceEvent
                {
                    Kind = "exportQueueWait", Request = lease.Request, Session = lease.Session, Sequence = lease.Sequence,
                    ExportQueueWaitCalls = 1, ExportQueueWaitMs = (Stopwatch.GetTimestamp() - lease.Start) * (1000d / Stopwatch.Frequency),
                    DetailEvery = lease.Sampled ? lease.Collector.DetailEvery : 0
                });
            }
            catch { /* Telemetry must not replace the export outcome. */ }
        }
    }

    private sealed class ExportQueueWaitLease(Collector collector, string request, string session, long sequence, bool sampled, long start)
    {
        internal readonly Collector Collector = collector;
        internal readonly string Request = request, Session = session;
        internal readonly long Sequence = sequence, Start = start;
        internal readonly bool Sampled = sampled;
        internal int Closed;
    }

    internal sealed class Collector
    {
        private readonly Channel<PerformanceEvent> _queue = Channel.CreateBounded<PerformanceEvent>(new BoundedChannelOptions(128)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly Action<string> _sink;
        private readonly int _detailEvery, _intervalSeconds, _maxRate;
        private long _dropped;
        private long _sampleCounter;
        private long _exportQueueWaitSampleCounter;
        private readonly long[] _active = new long[128];
        private long _activeOverflow;
        internal int StartFrame()
        {
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < _active.Length; i++)
                if (Interlocked.CompareExchange(ref _active[i], started, 0) == 0) return i;
            Interlocked.Increment(ref _activeOverflow);
            return -1;
        }
        internal void EndFrame(int slot) { if (slot >= 0) Interlocked.Exchange(ref _active[slot], 0); }
        internal int DetailEvery => _detailEvery;
        internal bool Sample() => _detailEvery > 0 && Interlocked.Increment(ref _sampleCounter) % _detailEvery == 0;
        internal bool SampleExportQueueWait() => _detailEvery > 0 && Interlocked.Increment(ref _exportQueueWaitSampleCounter) % _detailEvery == 0;
        internal Task Completion { get; }
        internal Collector(Action<string> sink, int detailEvery, int intervalSeconds, int maxRate)
        {
            _sink = sink; _detailEvery = detailEvery; _intervalSeconds = intervalSeconds; _maxRate = maxRate;
            Completion = Task.Run(RunAsync);
        }
        internal void Enqueue(PerformanceEvent frame) { if (!_queue.Writer.TryWrite(frame)) Interlocked.Increment(ref _dropped); }
        internal void Stop() => _queue.Writer.TryComplete();
        private async Task RunAsync()
        {
            PerformanceEvent[] totals = [new() { Backend = "cpu" }, new() { Backend = "gpu" }, new()];
            long lastFlush = Stopwatch.GetTimestamp(), nextFlush = lastFlush + _intervalSeconds * Stopwatch.Frequency;
            long[] windowStart = [lastFlush, lastFlush, lastFlush];
            int nextBackend = 0;
            long second = 0; int emitted = 0;
            bool Emit(PerformanceEvent item, bool summary)
            {
                long now = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
                if (now != second) { second = now; emitted = 0; }
                if (emitted >= (summary ? _maxRate : _maxRate - 1)) { if (!summary) Interlocked.Increment(ref _dropped); return false; }
                emitted++;
                try { _sink(Prefix + JsonSerializer.Serialize(item with { TimestampUtc = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) })); return true; }
                catch { Interlocked.Increment(ref _dropped); return false; }
            }
            void Flush()
            {
                long flushTime = Stopwatch.GetTimestamp();
                double windowMs = (flushTime - lastFlush) * (1000d / Stopwatch.Frequency);
                lastFlush = flushTime;
                int active = 0; long earliest = long.MaxValue;
                for (int i = 0; i < _active.Length; i++)
                {
                    long started = Volatile.Read(ref _active[i]);
                    if (started == 0) continue;
                    active++; earliest = Math.Min(earliest, started);
                }
                double cpu = 0; long workingSet = 0;
                try { using Process process = Process.GetCurrentProcess(); cpu = process.TotalProcessorTime.TotalMilliseconds; workingSet = process.WorkingSet64; } catch { }
                bool emittedSummary = false;
                for (int offset = 0; offset < totals.Length; offset++)
                {
                    int i = (nextBackend + offset) % totals.Length;
                    if (totals[i].Frames == 0 && totals[i].ExportQueueWaitCalls == 0) continue;
                    long dropped = Interlocked.Read(ref _dropped);
                    bool success = Emit(totals[i] with { Dropped = dropped, DetailEvery = _detailEvery,
                        WindowMs = (flushTime - windowStart[i]) * (1000d / Stopwatch.Frequency),
                        ProcessCpuMs = cpu, ProcessWorkingSetBytes = workingSet, ActiveFrames = active,
                        LongestActiveFrameMs = active == 0 ? 0 : (Stopwatch.GetTimestamp() - earliest) * (1000d / Stopwatch.Frequency),
                        ActiveOverflow = Interlocked.Read(ref _activeOverflow) }, true);
                    if (success)
                    {
                        Interlocked.Add(ref _dropped, -dropped);
                        emittedSummary = true;
                        totals[i] = new PerformanceEvent { Backend = totals[i].Backend };
                        windowStart[i] = flushTime;
                    }
                }
                nextBackend = (nextBackend + 1) % totals.Length;
                if (!emittedSummary && !totals.Any(static t => t.Frames > 0 || t.ExportQueueWaitCalls > 0))
                {
                    long dropped = Interlocked.Read(ref _dropped);
                    if (Emit(new PerformanceEvent { ProcessCpuMs = cpu, ProcessWorkingSetBytes = workingSet, ActiveFrames = active,
                        LongestActiveFrameMs = active == 0 ? 0 : (Stopwatch.GetTimestamp() - earliest) * (1000d / Stopwatch.Frequency),
                        ActiveOverflow = Interlocked.Read(ref _activeOverflow), Dropped = dropped, DetailEvery = _detailEvery, WindowMs = windowMs }, true))
                        Interlocked.Add(ref _dropped, -dropped);
                }
            }
            while (true)
            {
                while (_queue.Reader.TryRead(out PerformanceEvent? frame))
                {
                    int i = frame.Backend == "cpu" ? 0 : frame.Backend == "gpu" ? 1 : 2;
                    PerformanceEvent t = totals[i];
                    totals[i] = t with { Frames = t.Frames + frame.Frames, Errors = t.Errors + frame.Errors, Cancelled = t.Cancelled + frame.Cancelled,
                        MaxWallMs = Math.Max(t.MaxWallMs, frame.WallMs), Request = frame.WallMs > t.MaxWallMs ? frame.Request : t.Request,
                        Session = frame.WallMs > t.MaxWallMs ? frame.Session : t.Session, Sequence = frame.WallMs > t.MaxWallMs ? frame.Sequence : t.Sequence,
                        Width = frame.WallMs > t.MaxWallMs ? frame.Width : t.Width, Height = frame.WallMs > t.MaxWallMs ? frame.Height : t.Height,
                        WallMs = t.WallMs + frame.WallMs, PrepareMs = t.PrepareMs + frame.PrepareMs, ScriptMs = t.ScriptMs + frame.ScriptMs,
                        GraphMs = t.GraphMs + frame.GraphMs, MaterialMs = t.MaterialMs + frame.MaterialMs, ParticleMs = t.ParticleMs + frame.ParticleMs,
                        RasterMs = t.RasterMs + frame.RasterMs, ValidationMs = t.ValidationMs + frame.ValidationMs, ReadbackMs = t.ReadbackMs + frame.ReadbackMs,
                        BackgroundAcquireMs = t.BackgroundAcquireMs + frame.BackgroundAcquireMs, BackgroundCopyMs = t.BackgroundCopyMs + frame.BackgroundCopyMs,
                        DrawMs = t.DrawMs + frame.DrawMs, GpuSubmitMs = t.GpuSubmitMs + frame.GpuSubmitMs,
                        ExportQueueWaitCalls = t.ExportQueueWaitCalls + frame.ExportQueueWaitCalls, ExportQueueWaitMs = t.ExportQueueWaitMs + frame.ExportQueueWaitMs,
                        ScriptCalls = t.ScriptCalls + frame.ScriptCalls, ImageReads = t.ImageReads + frame.ImageReads, ImageBytes = t.ImageBytes + frame.ImageBytes,
                        SurfaceReads = t.SurfaceReads + frame.SurfaceReads, SurfaceBytes = t.SurfaceBytes + frame.SurfaceBytes,
                        ProcessAllocatedBytes = t.ProcessAllocatedBytes + frame.ProcessAllocatedBytes, Gc0 = t.Gc0 + frame.Gc0, Gc1 = t.Gc1 + frame.Gc1, Gc2 = t.Gc2 + frame.Gc2 };
                    if (frame.DetailEvery > 0) Emit(frame, false);
                }
                if (Stopwatch.GetTimestamp() >= nextFlush) { Flush(); nextFlush = Stopwatch.GetTimestamp() + _intervalSeconds * Stopwatch.Frequency; }
                if (_queue.Reader.Completion.IsCompleted) { Flush(); break; }
                using CancellationTokenSource wait = new(250);
                try { await _queue.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
    }
}
