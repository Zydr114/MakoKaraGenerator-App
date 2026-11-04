using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;

if (args.Length != 4 || args[1] is not ("CursorTrail" or "VectorTrail" or "EventBurst"))
    throw new ArgumentException("Arguments: backend-source-root CursorTrail|VectorTrail|EventBurst output.json repeats(1..6).");
int repeats = int.Parse(args[3], CultureInfo.InvariantCulture);
if (repeats is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(args));
using CancellationTokenSource limit = new(TimeSpan.FromMinutes(15));
await using var evidence = new EvidenceFiles(args[2]);
var startedUtc = DateTimeOffset.UtcNow;
using var currentProcess = Process.GetCurrentProcess();
var binaryHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
foreach (string path in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    binaryHashes.Add(Path.GetRelativePath(AppContext.BaseDirectory, path), EvidenceFiles.Hash(await File.ReadAllBytesAsync(path, limit.Token)));
var watch = Stopwatch.StartNew();
long bytes = GC.GetTotalAllocatedBytes(false);
string directory = Path.Combine(Path.GetFullPath(args[0]), "samples", "ScriptEffectsV2", args[1]);
var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(directory, cancellationToken: limit.Token);
var declaration = EffectPackageDefinition.Read(package, limit.Token).Controllers.Single();
double packageLoadMs = watch.Elapsed.TotalMilliseconds;
long packageLoadAllocatedBytes = GC.GetTotalAllocatedBytes(false) - bytes;
if (!EffectPackageProfiles.IsV2(package.Profile)) throw new InvalidOperationException("M08 requires an actual SDK v2 package.");
var snapshot = M08Fixture.Snapshot(package, declaration.Id, args[1]);
long[] times = M08Fixture.Times(args[1]);
var orders = M08Fixture.Orders(times);
byte[] fixture = M08Fixture.Serialize(args[1], snapshot, times, orders);
string fixtureSha256 = EvidenceFiles.Hash(fixture);
await evidence.WriteAssetAsync("fixture.json", fixture, limit.Token);
bytes = GC.GetTotalAllocatedBytes(false); watch.Restart();
var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
double compilerCreationMs = watch.Elapsed.TotalMilliseconds;
long compilerCreationAllocatedBytes = GC.GetTotalAllocatedBytes(false) - bytes;
bytes = GC.GetTotalAllocatedBytes(false); watch.Restart();
var compiled = await CompiledProjectEffects.CompileAsync(snapshot, compiler, new PackageImageValidator(), new PackageShaderValidator(),
    new PackageImageGraphValidator(), new PackageMaterialValidator(), limit.Token);
double compileMs = watch.Elapsed.TotalMilliseconds;
long compileAllocatedBytes = GC.GetTotalAllocatedBytes(false) - bytes;
int explicitCompilationCount = compiler.CompilationCount;
if (!compiled.Succeeded) throw new InvalidOperationException(string.Join('\n', compiled.Packages.SelectMany(static item => item.Artifact.Diagnostics)));

List<object> prepareSessions = [], renderSessions = [], prepareRecords = [], renderRecords = [];
Dictionary<long, string> structured = [], pixelHashes = [];
List<object> withinProcessComparisons = [];
foreach (var order in orders)
{
    var provider = new CountingGeometry();
    bytes = GC.GetTotalAllocatedBytes(false); watch.Restart();
    var evaluated = await compiled.CreateFactory(() => provider, ReplayReason.Initial).CreateAsync(snapshot, limit.Token);
    double setupMs = watch.Elapsed.TotalMilliseconds;
    long setupBytes = GC.GetTotalAllocatedBytes(false) - bytes;
    int setupGeometry = provider.SceneCalls, setupLineGeometry = provider.LineCalls;
    double disposeMs;
    try
    {
        HashSet<long> visited = [];
        for (int cycle = 0; cycle < repeats; cycle++)
        for (int index = 0; index < order.Times.Length; index++)
        {
            limit.Token.ThrowIfCancellationRequested(); long time = order.Times[index];
            int scenes = provider.SceneCalls, lines = provider.LineCalls;
            bytes = GC.GetTotalAllocatedBytes(false); watch.Restart();
            var frame = await evaluated.PrepareAsync(time, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), limit.Token);
            double wallMs = watch.Elapsed.TotalMilliseconds;
            long allocated = GC.GetTotalAllocatedBytes(false) - bytes;
            if (frame.DiagnosticSnapshot.HasError) throw new InvalidOperationException("Invalid prepared M08 output.");
            byte[] canonical = M08FrameFingerprint.Serialize(frame);
            string hash = EvidenceFiles.Hash(canonical);
            bool known = structured.TryGetValue(time, out string? previous);
            string asset = time.ToString(CultureInfo.InvariantCulture) + ".structure.json";
            if (!known) { structured.Add(time, hash); await evidence.WriteAssetAsync(asset, canonical, limit.Token); }
            else withinProcessComparisons.Add(new { stage = "preparation", order = order.Name, cycle, index, timeUs = time, equal = previous == hash });
            // Preserve a differing frame for diagnosis instead of silently reusing the baseline asset.
            if (known && previous != hash)
            {
                asset = order.Name + "-" + cycle.ToString(CultureInfo.InvariantCulture) + "-" + asset;
                await evidence.WriteAssetAsync(asset, canonical, limit.Token);
            }
            prepareRecords.Add(new { order = order.Name, cycle, index, timeUs = time, repeatedInSession = !visited.Add(time),
                wallMs, processAllocatedBytes = allocated, geometrySceneCalls = provider.SceneCalls - scenes,
                geometryLineCalls = provider.LineCalls - lines, sha256 = hash, canonicalAsset = asset,
                instances = frame.V2ParticleSnapshots.Select(static item => new { instance = item.InstanceId.Value,
                    births = item.Births.Births.Length, alive = item.Alive.Length, trails = item.Trails.Length,
                    trailNodes = item.Trails.Sum(static trail => trail.Samples.Length) }),
                births = frame.V2ParticleSnapshots.Sum(static item => item.Births.Births.Length), alive = frame.V2Particles.Length,
                trailNodes = frame.V2ParticleSnapshots.Sum(static item => item.Trails.Sum(static trail => trail.Samples.Length)),
                anchor = frame.V2AnchorStatistics, overlayBatches = frame.OverlayBatches.Length,
                overlayItems = frame.OverlayBatches.Sum(static item => item.ItemCount),
            });
        }
    }
    finally { watch.Restart(); await evaluated.DisposeAsync(); disposeMs = watch.Elapsed.TotalMilliseconds; }
    prepareSessions.Add(new { order = order.Name, setupMs, processAllocatedBytes = setupBytes,
        geometrySceneCalls = setupGeometry, geometryLineCalls = setupLineGeometry, disposeMs });
}

using (FfmpegVideoFrameProvider videos = new())
foreach (var order in orders)
{
    int before = compiler.CompilationCount;
    bytes = GC.GetTotalAllocatedBytes(false); watch.Restart();
    var rendered = await new ProjectRenderSessionFactory(compiler, videos, ReplayReason.Initial, renderBackend: "vulkan")
        .CreateAsync(snapshot, limit.Token);
    double setupMs = watch.Elapsed.TotalMilliseconds;
    long setupBytes = GC.GetTotalAllocatedBytes(false) - bytes;
    int compilations = compiler.CompilationCount - before;
    double disposeMs;
    try
    {
        HashSet<long> visited = [];
        for (int cycle = 0; cycle < repeats; cycle++)
        for (int index = 0; index < order.Times.Length; index++)
        {
            limit.Token.ThrowIfCancellationRequested(); long time = order.Times[index];
            bytes = GC.GetTotalAllocatedBytes(false); watch.Restart();
            using var frame = await rendered.RenderAsync(RenderRequest.At(snapshot, time) with { TransparentBackground = true }, limit.Token);
            double wallMs = watch.Elapsed.TotalMilliseconds;
            long allocated = GC.GetTotalAllocatedBytes(false) - bytes;
            if (frame.DiagnosticSnapshot.HasError || frame.Width != 640 || frame.Height != 360 || frame.PixelMemory.Length != 640 * 360 * 4)
                throw new InvalidOperationException("Invalid production Vulkan output.");
            string hash = EvidenceFiles.Hash(frame.PixelMemory.Span);
            bool known = pixelHashes.TryGetValue(time, out string? previous);
            string asset = time.ToString(CultureInfo.InvariantCulture) + ".bgra";
            if (!known) { pixelHashes.Add(time, hash); await evidence.WriteAssetAsync(asset, frame.PixelMemory, limit.Token); }
            else withinProcessComparisons.Add(new { stage = "rendering", order = order.Name, cycle, index, timeUs = time, equal = previous == hash });
            if (known && previous != hash)
            {
                asset = order.Name + "-" + cycle.ToString(CultureInfo.InvariantCulture) + "-" + asset;
                await evidence.WriteAssetAsync(asset, frame.PixelMemory, limit.Token);
            }
            renderRecords.Add(new { order = order.Name, cycle, index, timeUs = time, repeatedInSession = !visited.Add(time), wallMs,
                processAllocatedBytes = allocated, bgraSha256 = hash, pixelAsset = asset });
        }
    }
    finally { watch.Restart(); await rendered.DisposeAsync(); disposeMs = watch.Elapsed.TotalMilliseconds; }
    renderSessions.Add(new { order = order.Name, setupMs, processAllocatedBytes = setupBytes, compilations, disposeMs });
}

// This probe owns a new device after the measured sessions; it does not identify the retained production devices.
var separateBackendProbe = RuntimeRenderBackendProbe.Probe("vulkan", limit.Token);
await evidence.CompleteAsync(new
{
    schema = "mako-sdk-m08-finite-v1", status = "completed", startedUtc, completedUtc = DateTimeOffset.UtcNow,
    processId = Environment.ProcessId, processStartUtc = currentProcess.StartTime.ToUniversalTime(),
    sample = args[1], package.PackageId, package.Revision, declaration.Id, package.Profile,
    manifestSha256 = EvidenceFiles.Hash(package.Manifest.AsSpan()),
    packageFiles = package.Files.OrderBy(static file => file.Path, StringComparer.Ordinal)
        .Select(static file => new { file.Path, file.Role, sha256 = EvidenceFiles.Hash(file.Content.AsSpan()) }),
    product = typeof(Project).Assembly.GetName().Version?.ToString(), compiler.Identity, compiler.ReferenceIdentity, compiler.ReferenceCount,
    binarySha256 = binaryHashes, framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount, GCSettings.IsServerGC,
    vulkanEnvironment = new { driverFiles = Environment.GetEnvironmentVariable("VK_DRIVER_FILES"), icdFilenames = Environment.GetEnvironmentVariable("VK_ICD_FILENAMES") },
    fixture = new { id = M08Fixture.Id, sha256 = fixtureSha256, asset = "fixture.json", snapshot.EffectInputsFingerprint,
        timesUs = times, orders = orders.Select(static order => new { name = order.Name, timesUs = order.Times }), repeats },
    packageLoadMs, packageLoadAllocatedBytes, compilerCreationMs, compilerCreationAllocatedBytes, compileMs, compileAllocatedBytes,
    explicitCompilationCount, totalCompilationCount = compiler.CompilationCount,
    preparationSessions = prepareSessions, renderingSessions = renderSessions, preparation = prepareRecords, rendering = renderRecords,
    withinProcessComparisons, separateBackendProbe,
    scope = "Finite fixed two-token CJK/Ruby input. Each order has independent serial prepare and production Vulkan sessions. Preparation session creation includes Declare/schedule preparation; no public per-Declare timer is available. Per-frame render includes production prepare/draw/readback. Geometry and v2 anchor/birth/alive metrics are observed only in direct preparation; production pixel session does not expose them. Managed process allocation deltas include background work and are not peaks. Canonical serialization, hashing, asset writes and disposal are outside per-frame timings. Anchor/memo work counts are excluded from semantic hashes. Same-process comparisons include independent order sessions and repeated queries; the Python matrix compares three fresh processes per sample. No performance pass threshold, v1 equivalence, cache reset, HTTP/Worker/UI or shared texture claim.",
}, limit.Token);

sealed class CountingGeometry : ILineGeometryProvider, IDisposable
{
    private readonly SkiaRenderPipeline _renderer = new();
    public int SceneCalls { get; private set; }
    public int LineCalls { get; private set; }
    public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
    { SceneCalls++; return _renderer.GetSceneFrame(snapshot, timeUs, editingLineId); }
    public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId)
    { LineCalls++; return _renderer.GetLineGeometry(snapshot, lineId); }
    public void Dispose() => _renderer.Dispose();
}
