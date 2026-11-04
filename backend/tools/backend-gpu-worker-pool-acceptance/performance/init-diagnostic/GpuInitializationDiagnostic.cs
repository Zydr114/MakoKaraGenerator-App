using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>Symmetric diagnostic-build-only counters at actual successful native creation sites.</summary>
internal static class GpuInitializationDiagnostic
{
    private static readonly object Gate = new();
    private static long _devices, _contexts, _sequence;
    private static readonly ConditionalWeakTable<ISkiaRenderDevice, Pair> Pairs = new();
    private static readonly string? Output = Environment.GetEnvironmentVariable("MAKO_GPU_INIT_DIAGNOSTIC_DIR");
    private static readonly string ProcessStartTicks = ReadStartTicks();
    internal static string DeviceCreated()
    {
        lock (Gate) { string id = Guid.NewGuid().ToString("N"); _devices++; Write("device-created", null, id); return id; }
    }
    internal static void ContextCreated(string pairId) { lock (Gate) { _contexts++; Write("context-created", null, pairId); } }
    internal static void Track(ISkiaRenderDevice device, string pairId) { lock (Gate) Pairs.Add(device, new(pairId)); }
    internal static void MarkDevice(string phase, ISkiaRenderDevice device)
    {
        lock (Gate)
        {
            if (device is VulkanSkiaRenderDevice && !Pairs.TryGetValue(device, out _))
                throw new InvalidOperationException("Diagnostic Vulkan device has no actual-creation pair.");
            Write(phase, null, Pairs.TryGetValue(device, out Pair? pair) ? pair.Id : null);
        }
    }
    internal static void Mark(string phase, string? sessionId = null) { lock (Gate) Write(phase, sessionId); }
    private static void Write(string phase, string? sessionId, string? pairId = null)
    {
        if (Output is null) throw new InvalidOperationException("Diagnostic build requires a private diagnostic output directory.");
        string directory = Path.GetFullPath(Output);
        if (!OperatingSystem.IsLinux() || !Directory.Exists(directory) || !directory.Contains("/.local/", StringComparison.Ordinal))
            throw new InvalidOperationException("GPU initialization diagnostics require an existing App .local Linux directory.");
        string line = JsonSerializer.Serialize(new { schemaVersion = 1, pid = Environment.ProcessId,
            startTicks = ProcessStartTicks, sequence = ++_sequence, phase, sessionId, pairId,
            stopwatchTicks = Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture),
            stopwatchFrequency = Stopwatch.Frequency, deviceCreations = _devices, contextCreations = _contexts });
        File.AppendAllText(Path.Combine(directory, $"{Environment.ProcessId}-{ProcessStartTicks}.jsonl"), line + "\n");
    }
    private static string ReadStartTicks()
    {
        string stat = File.ReadAllText($"/proc/{Environment.ProcessId}/stat");
        string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return fields[19];
    }
    private sealed record Pair(string Id);
}
