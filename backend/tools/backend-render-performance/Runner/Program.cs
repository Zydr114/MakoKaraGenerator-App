using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

var options = Options.Parse(args);
var fixtureClock = Stopwatch.StartNew();
var fixture = await Fixtures.CreateAsync(options.SourceRoot, options.Scenario, options.Counterfactual, options.Mode == "matrix", options.Video);
fixtureClock.Stop();
var trace = TraceCapture.Load();
using var monitoring = MonitoringCapture.Start(options.Monitoring, options.Output);
using var videos = new FfmpegVideoFrameProvider();
var factory = new ProjectRenderSessionFactory(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
    options.CacheRoot, videos, ReplayReason.Initial, renderBackend: options.Backend);

long allocated = GC.GetTotalAllocatedBytes(true);
int[] gc = Counts();
var capture = trace.Begin("create");
var stopwatch = Stopwatch.StartNew();
var session = await factory.CreateAsync(fixture.Snapshot);
stopwatch.Stop();
capture?.Dispose();
var create = new { wallMs = stopwatch.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
    gcCollections = Delta(gc), trace = trace.Export() };

List<object> frames = [];
byte[]? lastPixels = null;
object? processEvidence = null;
RuntimeRenderBackendProbeInfo? device = null;
double disposeMs = 0;
try
{
    long[] times = options.Mode == "matrix" ? options.Times : options.Mode == "seek" ? [1_000_000, 1_033_333, 500_000, 1_000_000, 1_000_000, 1_001_234, 1_001_234, 1_200_000]
        : Enumerable.Range(0, options.Warmup + options.Frames).Select(i => 1_000_000L + i * 1_000_000L / 30).ToArray();
    for (int index = 0; index < times.Length; index++)
    {
        long time = times[index];
        var request = RenderRequest.At(fixture.Snapshot, time) with
        {
            Target = new() { PixelWidth = options.Width, PixelHeight = options.Height },
            Quality = RenderQuality.Final, TransparentBackground = options.Transparent,
        };
        allocated = GC.GetTotalAllocatedBytes(true);
        gc = Counts();
        capture = trace.Begin("render");
        stopwatch.Restart();
        using RenderFrame frame = await session.RenderAsync(request);
        stopwatch.Stop();
        capture?.Dispose();
        long frameAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
        int[] frameGc = Delta(gc);
        JsonElement? frameTrace = trace.Export();
        int expectedBytes = checked(options.Width * options.Height * 4);
        if (frame.Width != options.Width || frame.Height != options.Height || frame.Stride != options.Width * 4
            || frame.Format != PixelFormat.Bgra8888Premultiplied || frame.PixelMemory.Length != expectedBytes)
            throw new InvalidOperationException($"Frame {index} has invalid dimensions, pixel format or buffer length.");
        if (frame.DiagnosticSnapshot.HasError) throw new InvalidOperationException($"Frame {index} contains runtime error diagnostics.");
        var diagnostics = frame.DiagnosticSnapshot.ReadAll().ToArray();
        if (diagnostics.Length != frame.DiagnosticSnapshot.Count
            || diagnostics.Any(item => item.Diagnostic.Diagnostic.Severity == DiagnosticSeverity.Error)
            || frame.ImageEffectDiagnostics.Any(item => string.IsNullOrWhiteSpace(item.Code))
            || frame.MaterialDiagnostics.Any(item => string.IsNullOrWhiteSpace(item.Code)))
            throw new InvalidOperationException($"Frame {index} has invalid diagnostics.");
        Dictionary<Guid, int> diagnosticEpochs = [];
        int Epoch(Guid epoch)
        {
            if (!diagnosticEpochs.TryGetValue(epoch, out int number)) diagnosticEpochs.Add(epoch, number = diagnosticEpochs.Count);
            return number;
        }
        var diagnosticContent = new
        {
            runtime = diagnostics.Select(item => new
            {
                epochIndex = Epoch(item.Epoch), item.Sequence, instanceId = item.Diagnostic.InstanceId.Value,
                item.Diagnostic.ArtifactHash, item.Diagnostic.Stage, item.Diagnostic.TimeUs,
                item.Diagnostic.Diagnostic.Code, severity = item.Diagnostic.Diagnostic.Severity.ToString(),
                item.Diagnostic.Diagnostic.Message, item.Diagnostic.Diagnostic.Location, package = item.Diagnostic.PackageIdentity,
            }).ToArray(),
            image = frame.ImageEffectDiagnostics.Select(item => new
            {
                instanceId = item.InstanceId.Value, item.ArtifactHash, graph = item.Graph.Identity, item.InputId, item.Code,
            }).ToArray(),
            material = frame.MaterialDiagnostics.Select(item => new
            {
                material = item.Material.Identity, item.InputId, item.Code, owner = MaterialOwner(item.Owner),
            }).ToArray(),
        };
        string diagnosticHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(diagnosticContent, RunnerJson.Options)));
        string hash = Convert.ToHexStringLower(SHA256.HashData(frame.PixelMemory.Span));
        if (index == times.Length - 1) lastPixels = frame.PixelMemory.ToArray();
        frames.Add(new
        {
            index, timeUs = time, phase = index == 0 ? "cold" : options.Mode is "seek" or "matrix" ? "seek" : index < options.Warmup ? "warmup" : "steady",
            requestKind = index == 0 ? "first" : time == times[index - 1] ? "repeat" : time < times[index - 1] ? "backward" : "forward",
            wallMs = stopwatch.Elapsed.TotalMilliseconds, allocatedBytes = frameAllocated, gcCollections = frameGc,
            bgraSha256 = hash, pixelBytes = frame.PixelMemory.Length,
            diagnosticCount = diagnostics.Length, imageDiagnosticCount = frame.ImageEffectDiagnostics.Length,
            materialDiagnosticCount = frame.MaterialDiagnostics.Length, hasError = frame.DiagnosticSnapshot.HasError,
            diagnosticSha256 = diagnosticHash, diagnostics = diagnosticContent,
            trace = frameTrace,
        });
    }
    processEvidence = ProcessEvidence();
    device = RuntimeRenderBackendProbe.Probe(options.Backend);
}
finally
{
    stopwatch.Restart();
    await session.DisposeAsync();
    stopwatch.Stop();
    disposeMs = stopwatch.Elapsed.TotalMilliseconds;
}
if (lastPixels is null) throw new InvalidOperationException("No frame was rendered.");
monitoring?.Complete();
string output = Path.GetFullPath(options.Output);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
string pngPath = output + ".png";
using (var bitmap = new SKBitmap(new SKImageInfo(options.Width, options.Height, SKColorType.Bgra8888, SKAlphaType.Premul)))
{
    Marshal.Copy(lastPixels, 0, bitmap.GetPixels(), lastPixels.Length);
    using var image = SKImage.FromBitmap(bitmap);
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    await File.WriteAllBytesAsync(pngPath, png.ToArray());
}
var result = new
{
    schema = "mako-render-performance-v1", options.Scenario, options.Backend, options.Mode, options.Counterfactual,
    options.Width, options.Height, options.Transparent, requestedWarmupFrames = options.Warmup, requestedMeasuredFrames = options.Frames,
    timeSequenceStartUs = options.Mode == "matrix" ? options.Times[0] : 1_000_000, requestedTimesUs = options.Mode == "matrix" ? options.Times : null,
    quality = "Final", fixtureSha256 = fixture.Sha256, fixture = fixture.Description,
    fixtureLoadMs = fixtureClock.Elapsed.TotalMilliseconds, create, frames, disposeMs, process = processEvidence, deviceProbe = device,
    deviceProbeFrames = 1, deviceProbeSize = "4x2", projectFrameCount = frames.Count,
    pngPath, traceAvailable = trace.Available, monitoringMode = options.Monitoring,
    monitoringFile = monitoring?.FileName, monitoringLines = monitoring?.Lines ?? 0,
    monitoringConfiguration = monitoring?.Configuration,
    timingScope = "Create and RenderAsync wall time include normal JIT and first resource access; no claim of cleared system or driver caches. The first request at 1 second includes initial timeline replay where applicable. Render includes preparation, draw and readback. RenderFrame disposal, hashing, diagnostics, PNG and probe excluded; process-wide allocations/GC may include unrelated work. fixtureSha256 hashes the displayed fixture descriptor, not the full source tree.",
    diagnosticHashScope = "SHA-256 over UTF-8 JSON of diagnostics in this output, using the same serializer options; runtime random epoch GUIDs are replaced by zero-based first-seen epoch indices while sequence and all diagnostic fields are retained.",
    processEvidenceScope = "maps and DRM descriptors captured with measured session alive; device probe is a separate device in the same process after all measured frames.",
};
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, RunnerJson.Options));

static int[] Counts() => [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
static int[] Delta(int[] before) => [GC.CollectionCount(0) - before[0], GC.CollectionCount(1) - before[1], GC.CollectionCount(2) - before[2]];
static object MaterialOwner(PreparedMaterialOwner owner) => owner switch
{
    PreparedMaterialOwner.Script script => new { kind = "script", instanceId = script.InstanceId.Value, script.ArtifactHash, script.PackageIdentity },
    PreparedMaterialOwner.Source source => (object)new { kind = "source", source.Reference.Path, source.Reference.Region,
        slot = (int)source.Slot, material = new { source.Reference.Material.PackageId, source.Reference.Material.Revision,
            source.Reference.Material.ExportId, parametersJson = source.Reference.Material.Parameters.Json } },
    _ => throw new InvalidOperationException("Unknown material diagnostic owner."),
};
static object ProcessEvidence()
{
    using var process = Process.GetCurrentProcess();
    string stat = File.ReadAllText("/proc/self/stat");
    var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    string[] maps = File.ReadLines("/proc/self/maps").Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last())
        .Where(path => path.StartsWith('/') && (Path.GetFileName(path).Contains("vulkan", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Contains("radeon", StringComparison.OrdinalIgnoreCase)))
        .Select(Path.GetFileName).Where(name => name is not null).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()!;
    var drm = Directory.EnumerateFiles("/proc/self/fd").Select(path =>
    {
        try { return File.ResolveLinkTarget(path, false)?.FullName; }
        catch (IOException) { return null; }
    }).Where(path => path is not null && path.StartsWith("/dev/dri/", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
    return new { pid = process.Id, startTicks = fields[19], mappedLibraries = maps, drmDevices = drm };
}

internal sealed record Options(string SourceRoot, string Scenario, string Backend, string Output, string CacheRoot,
    string Mode, int Warmup, int Frames, int Width, int Height, bool Transparent, string Counterfactual, string Monitoring,
    long[] Times, string? Video)
{
    internal static Options Parse(string[] args)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[i][2..], args[i + 1]))
                throw new ArgumentException("Options must be unique --name value pairs.");
        }
        string[] allowed = ["source-root", "scenario", "backend", "output", "cache-root", "mode", "warmup", "frames", "width", "height", "transparent", "counterfactual", "monitoring", "times-us", "video"];
        if (values.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal))) throw new ArgumentException("Unknown option.");
        string Get(string key, string? fallback = null) => values.GetValueOrDefault(key) ?? fallback ?? throw new ArgumentException($"Missing --{key}.");
        int Number(string key, int fallback) => int.Parse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        string scenario = Get("scenario"), backend = Get("backend"), mode = Get("mode", "sequential"), counterfactual = Get("counterfactual", "none"), monitoring = Get("monitoring", "off");
        if (!new[] { "lyrics", "csharp", "graph", "material", "particle", "combined", "ruby", "multi", "video" }.Contains(scenario, StringComparer.Ordinal)
            || !new[] { "cpu", "vulkan" }.Contains(backend, StringComparer.Ordinal)
            || !new[] { "sequential", "seek", "matrix" }.Contains(mode, StringComparer.Ordinal)
            || !new[] { "none", "all", "csharp", "graph", "material", "particle" }.Contains(counterfactual, StringComparer.Ordinal)
            || !new[] { "off", "summary", "detailed" }.Contains(monitoring, StringComparer.Ordinal))
            throw new ArgumentException("Invalid scenario, backend, mode or counterfactual.");
        int warmup = Number("warmup", mode == "seek" ? 0 : 4), frames = Number("frames", mode == "seek" ? 8 : 16);
        int width = Number("width", 1920), height = Number("height", 1080);
        long[] times = mode == "matrix" ? Get("times-us").Split(',').Select(value => long.Parse(value, CultureInfo.InvariantCulture)).ToArray() : [];
        if (warmup < 0 || frames < 1 || (long)warmup + frames > 48 || width < 1 || height < 1 || width > 4096 || height > 4096
            || (mode == "seek" && (warmup != 0 || frames != 8))
            || (mode == "matrix" && (warmup != 0 || frames != times.Length || times.Any(time => time < 0 || time >= 370_000_000)))
            || (mode != "matrix" && values.ContainsKey("times-us")) || (scenario == "video") != values.ContainsKey("video"))
            throw new ArgumentException("Frame limit is 48; seek requires warmup 0 and frames 8; dimensions must be 1..4096.");
        return new(Get("source-root"), scenario, backend, Get("output"), Get("cache-root"), mode, warmup, frames,
            width, height, bool.Parse(Get("transparent", "false")), counterfactual, monitoring, times, values.GetValueOrDefault("video"));
    }
}

internal sealed class TraceCapture(Func<string, IDisposable>? begin, Func<string>? export)
{
    internal bool Available => begin is not null;
    internal IDisposable? Begin(string name) => begin?.Invoke(name);
    internal JsonElement? Export() => export is null ? null : JsonSerializer.Deserialize<JsonElement>(export());
    internal static TraceCapture Load()
    {
        foreach (string name in new[] { "MakoKaraGenerator.Rendering.Abstractions", "MakoKaraGenerator.Rendering.Skia", "MakoKaraGenerator.Runtime.Hosting" })
        {
            var type = Assembly.Load(name).GetType("MakoKaraGenerator.Rendering.RenderPerformanceTrace");
            if (type is null) continue;
            var begin = type.GetMethod("BeginCapture", BindingFlags.Public | BindingFlags.Static, [typeof(string)])
                ?? throw new InvalidOperationException("Trace BeginCapture is missing.");
            var export = type.GetMethod("ExportJson", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)
                ?? throw new InvalidOperationException("Trace ExportJson is missing.");
            return new(begin.CreateDelegate<Func<string, IDisposable>>(), export.CreateDelegate<Func<string>>());
        }
        return new(null, null);
    }
}

/// <summary>Optional runtime telemetry adapter. Reflection keeps the 10bad12 runner source compatible.</summary>
internal sealed class MonitoringCapture : IDisposable
{
    private readonly object? _telemetry;
    private readonly MethodInfo? _shutdown;
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();
    private int _lines;
    private int _stopped;
    internal string? FileName { get; }
    internal int Lines => Volatile.Read(ref _lines);
    internal object Configuration { get; }

    private MonitoringCapture(string mode, string output)
    {
        int detailEvery = mode == "detailed" ? 1 : 0;
        Configuration = new { mode, detailEvery, intervalSeconds = 10, maxEventsPerSecond = 10,
            scope = "Direct Runtime telemetry with production defaults; no Server/Worker transport or HTTP logging." };
        Type? type = null;
        foreach (string assembly in new[] { "MakoKaraGenerator.Rendering.Abstractions", "MakoKaraGenerator.Rendering.Skia", "MakoKaraGenerator.Runtime.Hosting" })
        {
            try { type = Assembly.Load(assembly).GetType("MakoKaraGenerator.Rendering.PerformanceTelemetry"); }
            catch (FileNotFoundException) { }
            if (type is not null) break;
        }
        if (type is null)
        {
            if (mode == "off") return;
            throw new InvalidOperationException("Requested monitoring mode requires PerformanceTelemetry.");
        }
        MethodInfo configure = type.GetMethod("Configure", BindingFlags.Public | BindingFlags.Static,
            [typeof(Action<string>), typeof(int), typeof(int), typeof(int)])
            ?? throw new InvalidOperationException("PerformanceTelemetry.Configure is missing.");
        _shutdown = type.GetMethod("ShutdownAsync", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)
            ?? throw new InvalidOperationException("PerformanceTelemetry.ShutdownAsync is missing.");
        if (mode == "off") { configure.Invoke(null, [null, 0, 10, 10]); return; }
        string path = Path.GetFullPath(output + ".monitoring.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        FileName = Path.GetFileName(path);
        Action<string> sink = line =>
        {
            lock (_gate) { _writer.WriteLine(line); Interlocked.Increment(ref _lines); }
        };
        configure.Invoke(null, [sink, detailEvery, 10, 10]);
        _telemetry = type;
    }

    internal static MonitoringCapture? Start(string mode, string output) => new(mode, output);

    internal void Complete()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        if (_telemetry is not null && _shutdown is not null)
        {
            try
            {
                if (_shutdown.Invoke(null, null) is Task task) task.GetAwaiter().GetResult();
            }
            catch { /* Shutdown errors must not replace render output. */ }
        }
        lock (_gate) { _writer?.Flush(); }
    }

    public void Dispose()
    {
        Complete();
        lock (_gate) { _writer?.Dispose(); }
    }
}

internal static class RunnerJson
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
