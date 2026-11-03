using MakoKaraGenerator.Scripting.Compilation;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.TestSupport;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using PublicPackagePreviewProbe;
using SkiaSharp;

var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
if (args.Length == 2 && args[0] == "generate")
{
    string root = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
    string sources = Path.Combine(root, "originals"); Directory.CreateDirectory(sources);
    string image = Path.Combine(sources, "owned-pattern.png"), alternate = Path.Combine(sources, "owned-alternate.png");
    WriteImage(image, false); WriteImage(alternate, true);
    string corrupt = Path.Combine(sources, "owned-corrupt.png"); File.WriteAllBytes(corrupt, "probe-owned non-image bytes"u8.ToArray());
    string dimension = Path.Combine(sources, "owned-dimension.png"), decoded = Path.Combine(sources, "owned-decoded.png"), large = Path.Combine(sources, "owned-large.bin");
    string noise = Path.Combine(sources, "owned-noise.png"); WriteNoise(noise);
    WriteSolid(dimension, 4097, 1); WriteSolid(decoded, 2049, 2049);
    File.WriteAllBytes(large, new byte[2 * 1024 * 1024 + 1]);
    string audio = Path.Combine(sources, "owned-analysis.wav");
    using (var wave = new BinaryWriter(File.Create(audio)))
    {
        const int samples = 48000; wave.Write(Encoding.ASCII.GetBytes("RIFF")); wave.Write(36 + samples * 2);
        wave.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); wave.Write(16); wave.Write((short)1); wave.Write((short)1);
        wave.Write(48000); wave.Write(96000); wave.Write((short)2); wave.Write((short)16);
        wave.Write(Encoding.ASCII.GetBytes("data")); wave.Write(samples * 2);
        for (int n = 0; n < samples; n++) wave.Write((short)(Math.Sin(n * Math.PI * 440 / 24000) * 2000));
    }
    byte[] audioBytes = File.ReadAllBytes(audio);
    ProjectAudioAnalysis analysis = (await AudioFeatureAnalyzer.CreateDefault().AnalyzeAsync(audio)).ToSourceData()
        ?? throw new InvalidOperationException("Original analyzer did not return a source analysis.");
    if (analysis.Data is null) throw new InvalidOperationException("Original analyzer did not produce Available analysis.");
    List<object> fixtures = [];
    var kinds = new List<string> { "image-static", "image-analysis", "image-package", "image-corrupt", "image-dimension", "image-decoded", "image-source-change", "image-capture-stable", "image-shutdown", "image-large", "image-envelope-budget", "image-diagnostic-compile", "image-diagnostic-compile-source", "image-diagnostic-image", "image-diagnostic-material", "image-diagnostic-script-material", "image-runtime-error", "image-pages", "image-over-budget" };
    foreach (string stage in new[] { "Initialize", "Evaluate" }) foreach (string action in new[] { "cancel", "deadline", "close" })
        kinds.Add("runaway-" + stage + "-" + action);
    foreach (string kind in kinds)
    {
        ProjectSnapshot basis = FixtureSources.Create("scale");
        bool package = kind is "image-package" or "image-shutdown" or "image-runtime-error" or "image-pages" or "image-over-budget" or "image-envelope-budget"
            || kind.StartsWith("runaway-", StringComparison.Ordinal) || kind.StartsWith("image-diagnostic-", StringComparison.Ordinal);
        if (kind.StartsWith("image-diagnostic-", StringComparison.Ordinal)) basis = FixtureSources.DiagnosticSource(kind[17..]);
        else if (kind == "image-runtime-error") basis = FixtureSources.RuntimeError();
        else if (kind is "image-pages" or "image-over-budget") basis = CustomWarnings(kind == "image-pages" ? 65 : 270);
        else if (kind == "image-envelope-budget")
        {
            var files = EffectPackageFixture.Files(); files["owned-padding.bin"] = new byte[1536 * 1024];
            var contents = EffectPackageContents.Create(EffectPackageFixture.Bytes(EffectPackageFixture.Manifest(files)), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
            basis = FixtureSources.Fingerprint(basis with { Effects = new([contents], [basis.Effects.Instances[0] with { PackageId = contents.PackageId, Revision = contents.Revision }]) });
        }
        if (kind.StartsWith("runaway-", StringComparison.Ordinal))
        {
            string stage = kind.Split('-')[1], marker = Path.Combine(root, "markers", kind);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            basis = FixtureSources.Runaway(stage, marker, kind, kind == "runaway-Initialize-cancel");
        }
        string original = kind switch { "image-corrupt" => corrupt, "image-dimension" => dimension, "image-decoded" => decoded,
            "image-large" => large, "image-envelope-budget" => noise, _ => image };
        Project project = Project.Create(kind, basis.Composition, basis.Lyrics, lyricStyle: basis.LyricStyle) with
        {
            Effects = package ? basis.Effects : ProjectEffects.Empty,
            LyricPresentation = basis.LyricPresentation, EnterAnimation = basis.EnterAnimation,
            Background = BackgroundSource.ForImage(original, kind == "image-alternate" ? BackgroundFit.Cover : BackgroundFit.Contain),
            AudioPath = kind == "image-analysis" ? audio : null,
            AudioMedia = kind == "image-analysis" ? new(Hash(audioBytes), audioBytes.Length, "owned-analysis.wav") : null,
            AudioAnalysis = kind == "image-analysis" ? analysis : null,
        };
        string saved = Path.Combine(root, kind); ProjectStore.Save(project, saved);
        Project fixedProject = ProjectStore.Load(saved).Project ?? throw new InvalidOperationException("Saved fixture did not load.");
        var media = fixedProject.Background.Media ?? throw new InvalidOperationException("Image closure missing.");
        string? expectedInputFailure = null;
        if (kind is "image-large" or "image-envelope-budget")
        {
            try
            {
                byte[] envelope = ProjectImagePreviewSnapshotCodec.Encode(ProjectSnapshot.FromProject(fixedProject), File.ReadAllBytes(fixedProject.Background.Path!));
                if (envelope.Length > ProjectImageValidationWorkerContract.MaximumInputBytes) expectedInputFailure = "SnapshotSizeExceeded";
            }
            catch (SnapshotContractException failure) when (failure.Code == "SnapshotSizeExceeded") { expectedInputFailure = failure.Code; }
            if (expectedInputFailure is null) throw new InvalidOperationException("Owned budget fixture did not exceed its complete image input budget.");
        }
        fixtures.Add(new { Kind = kind, Package = package, Original = Path.GetRelativePath(root, original),
            FixedPath = media.RelativePath, Sha256 = media.Hash, ExpectedInputFailure = expectedInputFailure,
            SizeBytes = media.ByteLength, Fit = fixedProject.Background.Fit.ToString(),
            BaseProfile = ProjectPreviewSnapshotProfile.Select(ProjectSnapshot.FromProject(fixedProject) with { Background = BackgroundSource.Color }),
            AnalysisContentHash = fixedProject.AudioAnalysis?.Data?.ContentHash });
    }
    File.WriteAllText(Path.Combine(root, "fixtures.json"), JsonSerializer.Serialize(new { Fixtures = fixtures,
        Scope = "Probe-owned ProjectStore.Save image closures. Saved media and original source are distinct owned files. No public media-edit command is claimed." }, json));
    return;
}
if (args.Length != 5 || args[0] != "validate")
    throw new ArgumentException("generate directory | validate saved-directory output-prefix frames expected-base-profile");
string savedDirectory = Path.GetFullPath(args[1]), prefix = Path.GetFullPath(args[2]);
Project projectInput = ProjectStore.Load(savedDirectory).Project ?? throw new InvalidOperationException("Oracle project did not load.");
ProjectSnapshot loadedSnapshot = ProjectSnapshot.FromProject(projectInput);
byte[] imageBytes = File.ReadAllBytes(projectInput.Background.Path!);
string logicalPath = projectInput.Background.Media!.RelativePath;
// Use the production saved closure directly. No Worker task, decoder, response, report or HTTP data constructs expectations.
ProjectSnapshot snapshot = loadedSnapshot with { Background = loadedSnapshot.Background with { Path = logicalPath } };
var factory = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), prefix + "-cache"),
    new FfmpegVideoFrameProvider(), ReplayReason.Initial, () =>
    {
        try { return new MemoryImageBackgroundFrameSourceResolver(logicalPath, imageBytes); }
        catch (ImageBackgroundDecodeException failure)
        { throw new RenderSessionFailureException(failure.Code == "PackageImageBudgetExceeded" ? "PackageImageBudgetExceeded" : "PackageImageDecodeFailed", failure.Message, innerException: failure); }
    });
int frames = int.Parse(args[3], CultureInfo.InvariantCulture);
List<RuntimeValidationProgress> progress = [];
var result = await new RuntimeFrameValidator(factory).ValidateAsync(snapshot, new(frames), new InlineProgress(progress));
ValidationReportDto report = new(result.Succeeded, result.OpaqueFrames, result.TransparentFrames,
    [.. result.Issues.Select(MapIssue)], [.. result.Diagnostics.Select(MapDiagnostic)]);
await File.WriteAllBytesAsync(prefix + "-report.json", WorkerValidationContract.EncodeReport(report, frames));
await File.WriteAllTextAsync(prefix + "-source.json", JsonSerializer.Serialize(new
{
    ImageSha256 = Hash(imageBytes), SnapshotSha256 = Hash(ProjectImagePreviewSnapshotCodec.Encode(loadedSnapshot, imageBytes)),
    BaseProfile = args[4], ActualBaseProfile = ProjectPreviewSnapshotProfile.Select(loadedSnapshot with { Background = BackgroundSource.Color }),
    loadedSnapshot.Composition.CanvasWidth, loadedSnapshot.Composition.CanvasHeight,
    FrameRateNumerator = loadedSnapshot.Composition.FrameRate.Numerator, FrameRateDenominator = loadedSnapshot.Composition.FrameRate.Denominator,
    OutputDurationUs = loadedSnapshot.Composition.OutputDurationUs?.ToString(CultureInfo.InvariantCulture), FramesPerOutput = frames,
    Progress = progress.Select(value => new { value.CompletedFrames, value.TotalFrames, value.Transparent, TimeUs = value.TimeUs.ToString(CultureInfo.InvariantCulture) }),
    Scope = "Independent ProjectStore.Load saved closure and original RuntimeFrameValidator. Runtime epochs retain first-seen session grouping and contiguous sequences during comparison; no HTTP pixels or HTTP result builds expectations.",
}, json));
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static void WriteImage(string path, bool alternate)
{
    using var bitmap = new SKBitmap(new SKImageInfo(96, 128, SKColorType.Bgra8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(bitmap); canvas.ClipRect(new(0, 0, 96, 128)); canvas.Clear(SKColors.Transparent);
    using var paint = new SKPaint { Color = alternate ? new(20, 180, 60, 220) : new(240, 50, 80, 220), IsAntialias = false };
    canvas.DrawRect(new(0, 0, 48, 128), paint); paint.Color = alternate ? new(200, 50, 230, 160) : new(30, 110, 230, 160);
    canvas.DrawRect(new(48, 0, 96, 128), paint); paint.Color = new(255, 210, 30, 255); canvas.DrawRect(new(12, 12, 84, 44), paint);
    paint.Color = new(25, 240, 210, 128); canvas.DrawCircle(48, 91, 22, paint);
    using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, encoded.ToArray());
}

    static ValidationIssueDto MapIssue(RuntimeValidationIssue value)
    {
        BackendDiagnosticDto? diagnostic = value.Compilation is { } source ? new(source.Source.Code, source.Source.IsError ? "error" : "warning", source.Source.Message,
            source.Source.Path, OriginKind: "compileSource", Source: new(source.PackageId, source.Revision, source.InstanceId, source.ExportId,
                source.Source.Code, source.Source.Message, source.Source.IsError, source.Source.Path, source.Source.Line, source.Source.Column))
            : value.Graph is { } graph ? new(graph.Code, "warning", value.Message ?? graph.Code, (string?)null, OriginKind: "imageEffect",
                ImageEffect: new(graph.InstanceId.Value, graph.ArtifactHash,
                    new(graph.Graph.Identity.PackageId, graph.Graph.Identity.Revision, graph.Graph.Identity.ExportId), graph.InputId, graph.Code))
            : value.Material is { } material ? MaterialDiagnostic(material) : null;
        return new(value.Transparent, value.TimeUs.ToString(CultureInfo.InvariantCulture), value.Code, value.Message, value.IsError,
            value.Location is null ? null : new(value.Location.Path, value.Location.Line, value.Location.Column), value.SampleFailureCode, diagnostic);

        static BackendDiagnosticDto MaterialDiagnostic(RenderMaterialDiagnostic value)
        {
            BackendDiagnosticMaterialOwnerDto owner = value.Owner switch
            {
                PreparedMaterialOwner.Script script => new("script", new(script.InstanceId.Value, script.ArtifactHash,
                    script.PackageIdentity is null ? null : new(script.PackageIdentity.PackageId, script.PackageIdentity.Revision, script.PackageIdentity.ExportId))),
                PreparedMaterialOwner.Source source => new("source", null, new(source.Reference.Path, source.Reference.Region,
                    (int)source.Slot, new(source.Reference.Material.PackageId, source.Reference.Material.Revision, source.Reference.Material.ExportId), source.Reference.Material.Parameters.Json)),
                _ => throw new RenderSessionFailureException("WorkerUnsupportedValidationIssue", "材质诊断来源无效。"),
            };
            return new(value.Code, "warning", value.Code, (string?)null, OriginKind: "material",
                Material: new(owner, new(value.Material.Identity.PackageId, value.Material.Identity.Revision, value.Material.Identity.ExportId), value.InputId, value.Code));
        }
    }
    static ValidationDiagnosticDto MapDiagnostic(RuntimeValidationDiagnostic value)
    {
        RuntimeDiagnosticOccurrence occurrence = value.Occurrence;
        RuntimeDiagnostic runtime = occurrence.Diagnostic;
        EffectDiagnostic diagnostic = runtime.Diagnostic;
        return new(value.Transparent, new(diagnostic.Code, diagnostic.Severity.ToString().ToLowerInvariant(), diagnostic.Message,
            null, new(occurrence.Epoch, occurrence.Sequence.ToString(CultureInfo.InvariantCulture), runtime.InstanceId.Value,
                runtime.ArtifactHash, runtime.Stage, runtime.TimeUs.ToString(CultureInfo.InvariantCulture),
                diagnostic.Location is null ? null : new(diagnostic.Location.Path, diagnostic.Location.Line, diagnostic.Location.Column),
                runtime.PackageIdentity is null ? null : new(runtime.PackageIdentity.PackageId, runtime.PackageIdentity.Revision,
                    runtime.PackageIdentity.ExportId)), "runtime"));
    }

static ProjectSnapshot CustomWarnings(int count)
{
    string source = "using MakoKaraGenerator.Scripting; public sealed class PackEffect:EffectBase { private bool reported; public override void Evaluate(FrameContext frame,FrameWriter output) { if(reported)return;reported=true; for(int i=0;i<" + count.ToString(CultureInfo.InvariantCulture) + ";i++) frame.Diagnostics.Report(DiagnosticSeverity.Warning,\"ActualPageWarning\",\"Actual warning \"+i); } }";
    var files = EffectPackageFixture.Files(); files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(source);
    var package = EffectPackageContents.Create(EffectPackageFixture.Bytes(EffectPackageFixture.Manifest(files)), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
    var basis = FixtureSources.Create("scale");
    return FixtureSources.Fingerprint(basis with { Effects = new([package], [basis.Effects.Instances[0] with { PackageId = package.PackageId, Revision = package.Revision }]) });
}

static void WriteSolid(string path, int width, int height)
{
    using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
    bitmap.Erase(SKColors.CornflowerBlue);
    using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, encoded.ToArray());
}
static void WriteNoise(string path)
{
    using var bitmap = new SKBitmap(new SKImageInfo(640, 640, SKColorType.Bgra8888, SKAlphaType.Opaque));
    byte[] pixels = new byte[bitmap.ByteCount]; new Random(200100).NextBytes(pixels);
    for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
    Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
    using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, encoded.ToArray());
}
internal sealed class InlineProgress(List<RuntimeValidationProgress> values) : IProgress<RuntimeValidationProgress>
{ public void Report(RuntimeValidationProgress value) => values.Add(value); }
