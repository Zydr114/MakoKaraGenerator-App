using System.Diagnostics;
using System.Text.Json;
using System.Threading;

namespace MakoKaraGenerator.Rendering;

/// <summary>Collects temporary performance spans in an instrumented source copy.</summary>
public static class RenderPerformanceTrace
{
    private static readonly AsyncLocal<Scope?> Current = new();
    private static readonly AsyncLocal<Capture?> Last = new();
    private static readonly IDisposable Noop = new EmptyScope();

    /// <summary>Starts a capture on the current execution context.</summary>
    public static IDisposable BeginCapture(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (Current.Value is not null) throw new InvalidOperationException("A capture is already active.");
        var capture = new Capture();
        Last.Value = capture;
        return Start(capture, null, name);
    }

    /// <summary>Records a nested span when a capture is active.</summary>
    public static IDisposable Measure(string name)
    {
        var parent = Current.Value;
        return parent is null ? Noop : Start(parent.Capture, parent, name);
    }

    /// <summary>Records requested byte counts separately from time spans.</summary>
    public static void AddBytes(string name, long bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Current.Value?.Capture.AddBytes(name, bytes);
    }

    /// <summary>Exports the last capture with validated exclusive wall times.</summary>
    public static string ExportJson()
    {
        var capture = Last.Value ?? throw new InvalidOperationException("No capture exists in this execution context.");
        return capture.Export();
    }

    private static Scope Start(Capture capture, Scope? parent, string name)
    {
        var scope = new Scope(capture, parent, capture.NextId(), name, Stopwatch.GetTimestamp());
        capture.Add(scope);
        Current.Value = scope;
        return scope;
    }

    private sealed class EmptyScope : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class Scope(Capture capture, Scope? parent, long id, string name, long start) : IDisposable
    {
        private int _disposed;
        public Capture Capture { get; } = capture;
        public Scope? Parent { get; } = parent;
        public long Id { get; } = id;
        public long? ParentId => Parent?.Id;
        public string Name { get; } = name;
        public long Start { get; } = start;
        public long End { get; private set; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            End = Stopwatch.GetTimestamp();
            if (Current.Value != this) Capture.Error("Span disposed outside its active context: " + Name);
            Current.Value = Parent;
        }
    }

    private sealed class Capture
    {
        private readonly Lock _sync = new();
        private readonly List<Scope> _spans = [];
        private readonly List<string> _errors = [];
        private readonly Dictionary<string, long> _bytes = new(StringComparer.Ordinal);
        private long _nextId;
        public long NextId() => Interlocked.Increment(ref _nextId);
        public void Add(Scope span) { lock (_sync) _spans.Add(span); }
        public void Error(string error) { lock (_sync) _errors.Add(error); }
        public void AddBytes(string name, long bytes)
        {
            lock (_sync) _bytes[name] = checked(_bytes.GetValueOrDefault(name) + bytes);
        }

        public string Export()
        {
            Scope[] spans; List<string> errors; Dictionary<string, long> bytes;
            lock (_sync) { spans = [.. _spans]; errors = [.. _errors]; bytes = new(_bytes, StringComparer.Ordinal); }
            var children = new Dictionary<Scope, List<Scope>>();
            var members = spans.ToDictionary(static span => span.Id);
            int roots = 0;
            foreach (var span in spans)
            {
                if (span.End == 0 || span.End < span.Start) errors.Add("Unfinished or invalid span: " + span.Name);
                if (span.Parent is null) { roots++; continue; }
                if (span.ParentId is not { } parentId || !members.TryGetValue(parentId, out var recordedParent)
                    || !ReferenceEquals(recordedParent, span.Parent)) { errors.Add("Missing parent: " + span.Name); continue; }
                if (span.Start < span.Parent.Start || span.End > span.Parent.End)
                    errors.Add("Span exceeds parent: " + span.Name);
                if (!children.TryGetValue(span.Parent, out var list)) children.Add(span.Parent, list = []);
                list.Add(span);
            }
            if (roots != 1) errors.Add("Capture must contain exactly one root.");
            var aggregates = new Dictionary<string, Aggregate>(StringComparer.Ordinal);
            foreach (var span in spans)
            {
                long duration = Math.Max(0, span.End - span.Start), covered = 0;
                if (children.TryGetValue(span, out var list))
                {
                    long begin = span.Start, end = span.Start;
                    foreach (var child in list.OrderBy(static item => item.Start))
                    {
                        long a = Math.Clamp(child.Start, span.Start, Math.Max(span.Start, span.End));
                        long b = Math.Clamp(child.End, span.Start, Math.Max(span.Start, span.End));
                        if (b <= a) continue;
                        if (a > end) { covered += end - begin; begin = a; end = b; }
                        else end = Math.Max(end, b);
                    }
                    covered += end - begin;
                }
                if (!aggregates.TryGetValue(span.Name, out var aggregate)) aggregates.Add(span.Name, aggregate = new());
                aggregate.Add(duration, Math.Max(0, duration - covered));
            }
            double factor = 1000d / Stopwatch.Frequency;
            var stages = aggregates.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(pair => new
            {
                name = pair.Key, calls = pair.Value.Calls, inclusiveMs = pair.Value.Inclusive * factor,
                exclusiveMs = pair.Value.Exclusive * factor, minMs = pair.Value.Min * factor, maxMs = pair.Value.Max * factor
            }).ToArray();
            return JsonSerializer.Serialize(new { stages, bytes, spanCount = spans.Length, valid = errors.Count == 0, errors });
        }
    }

    private sealed class Aggregate
    {
        public int Calls { get; private set; }
        public long Inclusive { get; private set; }
        public long Exclusive { get; private set; }
        public long Min { get; private set; } = long.MaxValue;
        public long Max { get; private set; }
        public void Add(long inclusive, long exclusive)
        {
            Calls++; Inclusive += inclusive; Exclusive += exclusive;
            Min = Math.Min(Min, inclusive); Max = Math.Max(Max, inclusive);
        }
    }
}
