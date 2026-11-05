using System.Diagnostics;
using System.Text.Json;

namespace M10.NativeEnvelope.Probe;

internal sealed class EvidenceJournal : IDisposable
{
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private long _sequence;

    internal EvidenceJournal(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "events.jsonl");
        _stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
    }

    internal void Write(string scenario, string action, object? data = null)
    {
        lock (_gate)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
            { sequence = ++_sequence, utc = DateTimeOffset.UtcNow, monotoneTicks = Stopwatch.GetTimestamp(), scenario, action, data });
            _stream.Write(json);
            _stream.WriteByte((byte)'\n');
            _stream.Flush(flushToDisk: true);
        }
    }

    public void Dispose() { lock (_gate) _stream.Dispose(); }
}

internal sealed class ProbeFailure(string code) : Exception(code);
