using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;

if (args.Length != 5 || args[1] is not ("v1" or "v2") || args[2] is not ("WordEmphasis" or "RepeatEmphasis" or "ClusterExit")
    || args[1] == "v1" && args[2] == "ClusterExit")
    throw new ArgumentException("Arguments: backend-source-root v1|v2 WordEmphasis|RepeatEmphasis|ClusterExit output.json repeats(3..12); ClusterExit requires v2.");
int repeats = int.Parse(args[4], CultureInfo.InvariantCulture);
if (repeats is < 3 or > 12) throw new ArgumentOutOfRangeException(nameof(args));
using CancellationTokenSource limit = new(TimeSpan.FromMinutes(12));
await using var evidence = new EvidenceFiles(args[3]);
var startedUtc = DateTimeOffset.UtcNow;
var watch = Stopwatch.StartNew();
string directory = Path.Combine(Path.GetFullPath(args[0]), "samples", args[1] == "v2" ? "ScriptEffectsV2" : "ScriptEffects", args[2]);
var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(directory, cancellationToken: limit.Token);
var declaration = EffectPackageDefinition.Read(package, limit.Token).Controllers.Single();
double packageLoadMs = watch.Elapsed.TotalMilliseconds;
if (EffectPackageProfiles.IsV2(package.Profile) != (args[1] == "v2"))
    throw new InvalidOperationException("Package profile does not match the requested SDK.");

long[] starts = [0, 1_000_000, 2_000_000, 180_000_000, 181_000_000, 360_000_000, 361_000_000];
var lines = starts.Select((start, index) =>
{
    string id = "line-" + index.ToString(CultureInfo.InvariantCulture);
    // One whole-line token aligns the v1 token ordinal with the v2 line ordinal for this finite A/B fixture.
    var token = LyricToken.Create(id + "-token", "星光", new(start, start + 1_000_000), TimingStatus.Edited);
    return LyricLine.Create(id, "星光", new(start, start + 1_000_000), TimingStatus.Edited, [token])
        with { RubyAnnotations = [RubyAnnotation.Create(id + "-ruby", token.Id, "ほし")] };
});
var project = Project.Create("M07 finite source", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 365_000_000),
    LyricsDocument.Create(lines), lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64 });
var snapshot = ProjectSnapshot.FromProject(project with { Effects = new([package], [new() { Id = "effect", Name = "effect",
    PackageId = package.PackageId, Revision = package.Revision, ExportId = declaration.Id, Subscription = new("project") }]) });
var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
watch.Restart();
var compiled = await CompiledProjectEffects.CompileAsync(snapshot, compiler, cancellationToken: limit.Token);
double compileMs = watch.Elapsed.TotalMilliseconds;
int explicitCompilationCount = compiler.CompilationCount;
if (!compiled.Succeeded) throw new InvalidOperationException(string.Join('\n', compiled.Packages.SelectMany(static item => item.Artifact.Diagnostics)));

long[] cycle = args[2] == "ClusterExit"
    ? [999_999, 1_000_000, 1_300_000, 1_599_999, 1_600_000, 180_750_000, 360_750_000, 1_300_000, 999_999]
    : [750_000, 180_750_000, 1_300_000, 360_750_000, 1_300_000, 750_000];
var target = RenderTarget.FromComposition(snapshot.Composition);
var prepareRecords = new List<object>();
double prepareSessionMs;
double prepareDisposeMs;
Dictionary<long, string> structured = [];
{
    var provider = new CountingGeometry();
    watch.Restart();
    var evaluated = await compiled.CreateFactory(() => provider, ReplayReason.Initial).CreateAsync(snapshot, limit.Token);
    prepareSessionMs = watch.Elapsed.TotalMilliseconds;
    try
    {
        for (int index = 0; index < repeats * cycle.Length; index++)
        {
            limit.Token.ThrowIfCancellationRequested();
            long time = cycle[index % cycle.Length];
            int calls = provider.Calls;
            long bytes = GC.GetTotalAllocatedBytes(false);
            watch.Restart();
            var frame = await evaluated.PrepareAsync(time, OutputMode.Transparent, target, limit.Token);
            double elapsed = watch.Elapsed.TotalMilliseconds;
            long allocated = GC.GetTotalAllocatedBytes(false) - bytes;
            if (frame.DiagnosticSnapshot.HasError) throw new InvalidOperationException("Invalid prepared output.");
            byte[] canonical = FrameFingerprint.Serialize(frame);
            string hash = EvidenceFiles.Hash(canonical);
            bool repeatedTime = structured.TryGetValue(time, out string? previous);
            if (repeatedTime && previous != hash) throw new InvalidOperationException("Structured determinism failure.");
            structured[time] = hash;
            string asset = time.ToString(CultureInfo.InvariantCulture) + ".structure.json";
            if (!repeatedTime) await evidence.WriteAssetAsync(asset, canonical, limit.Token);
            prepareRecords.Add(new { index, cycleIndex = index % cycle.Length, cycleNumber = index / cycle.Length,
                timeUs = time, repeatedTime, wallMs = elapsed, processAllocatedBytes = allocated,
                geometrySamples = provider.Calls - calls, sha256 = hash, canonicalAsset = asset });
        }
    }
    finally
    {
        watch.Restart();
        await evaluated.DisposeAsync();
        prepareDisposeMs = watch.Elapsed.TotalMilliseconds;
    }
}

double renderSessionMs;
double renderDisposeMs;
int renderSessionCompilations;
var renderRecords = new List<object>();
Dictionary<long, string> pixelHashes = [];
using (FfmpegVideoFrameProvider videos = new())
{
    int before = compiler.CompilationCount;
    watch.Restart();
    var rendered = await new ProjectRenderSessionFactory(compiler, videos, ReplayReason.Initial, renderBackend: "vulkan")
        .CreateAsync(snapshot, limit.Token);
    renderSessionMs = watch.Elapsed.TotalMilliseconds;
    renderSessionCompilations = compiler.CompilationCount - before;
    try
    {
        for (int index = 0; index < repeats * cycle.Length; index++)
        {
            limit.Token.ThrowIfCancellationRequested();
            long time = cycle[index % cycle.Length];
            watch.Restart();
            using var frame = await rendered.RenderAsync(RenderRequest.At(snapshot, time) with { TransparentBackground = true }, limit.Token);
            double elapsed = watch.Elapsed.TotalMilliseconds;
            if (frame.DiagnosticSnapshot.HasError || frame.Width != 640 || frame.Height != 360 || frame.PixelMemory.Length != 640 * 360 * 4)
                throw new InvalidOperationException("Invalid GPU output.");
            string hash = EvidenceFiles.Hash(frame.PixelMemory.Span);
            bool repeatedTime = pixelHashes.TryGetValue(time, out string? previous);
            if (repeatedTime && previous != hash) throw new InvalidOperationException("Pixel determinism failure.");
            pixelHashes[time] = hash;
            string asset = time.ToString(CultureInfo.InvariantCulture) + ".bgra";
            if (!repeatedTime) await evidence.WriteAssetAsync(asset, frame.PixelMemory, limit.Token);
            renderRecords.Add(new { index, cycleIndex = index % cycle.Length, cycleNumber = index / cycle.Length,
                timeUs = time, repeatedTime, wallMs = elapsed, bgraSha256 = hash, pixelAsset = asset });
        }
    }
    finally
    {
        watch.Restart();
        await rendered.DisposeAsync();
        renderDisposeMs = watch.Elapsed.TotalMilliseconds;
    }
}

// This creates a separate device after the measured sessions; it is not the retained render-session device identity.
var separateBackendProbe = RuntimeRenderBackendProbe.Probe("vulkan", limit.Token);
await evidence.CompleteAsync(new
{
    schema = "mako-sdk-m07-finite-v2", status = "completed", startedUtc, completedUtc = DateTimeOffset.UtcNow,
    sdk = args[1], sample = args[2], package.PackageId, package.Revision, declaration.Id, package.Profile,
    product = typeof(Project).Assembly.GetName().Version?.ToString(), compiler.Identity,
    framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
    fixture = new { id = "m07-one-token-cjk-ruby-v1", lineStartsUs = starts, tokenText = "星光", ruby = "ほし",
        font = "Noto Sans CJK SC", width = 640, height = 360, outputMode = "Transparent", cycle, repeats },
    packageLoadMs, compileMs, prepareSessionMs, prepareDisposeMs, renderSessionMs, renderDisposeMs,
    explicitCompilationCount, renderSessionCompilations, totalCompilationCount = compiler.CompilationCount,
    uniqueStructuredTimes = structured.Count, uniquePixelTimes = pixelHashes.Count,
    preparation = prepareRecords, rendering = renderRecords, separateBackendProbe,
    scope = "Single fresh process and fixed input. Direct prepare and production Vulkan render use separate serial sessions. Prepare excludes drawing; render includes prepare/draw/readback. Hashing, serialization, asset writes and disposal are outside per-frame timings. Session creation may compile again. repeatedTime means a repeated query in the same session, not an independent frame or process. No OS/driver-cache reset, HTTP/Worker/UI, formal threshold or general multi-token Repeat parity claim.",
}, limit.Token);

sealed class CountingGeometry : ILineGeometryProvider, IDisposable
{
    private readonly SkiaRenderPipeline _renderer = new();
    public int Calls { get; private set; }
    public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
    { Calls++; return _renderer.GetSceneFrame(snapshot, timeUs, editingLineId); }
    public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => _renderer.GetLineGeometry(snapshot, lineId);
    public void Dispose() => _renderer.Dispose();
}
