using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using PublicPackagePreviewProbe;
using MakoKaraGenerator.TestSupport;
using SkiaSharp;

var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
if (args.Length == 2 && args[0] == "p3-package")
{
    string output = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    var package = MixedEffectPackageFixture.Contents();
    using (var archive = ZipFile.Open(output, ZipArchiveMode.Create))
    {
        Write("manifest.json", package.Manifest.ToArray());
        foreach (var file in package.Files) Write(file.Path, file.Content.ToArray());
        void Write(string path, byte[] bytes)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            entry.LastWriteTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); entry.ExternalAttributes = 0;
            using var stream = entry.Open(); stream.Write(bytes);
        }
    }
    File.WriteAllText(output + ".json", JsonSerializer.Serialize(new { PackageId = package.PackageId,
        PackageRevision = package.Revision, ArchiveSha256 = Hash(File.ReadAllBytes(output)),
        Scope = "Existing MixedEffectPackageFixture controller/material/graph; parsing is not execution proof." }, json));
    return;
}
if (args.Length == 3 && args[0] == "completion-template")
{
    string output = Path.GetFullPath(args[1]), fontPath = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    byte[] originalFont = File.ReadAllBytes(fontPath), fixedFont = originalFont.ToArray();
    // The representative font must have a unique family so an installed Noto
    // fallback cannot accidentally satisfy the public package-font flow.
    byte[] originalName = Encoding.BigEndianUnicode.GetBytes("Noto"), fixedName = Encoding.BigEndianUnicode.GetBytes("Mako");
    for (int fontOffset = 0; fontOffset <= fixedFont.Length - originalName.Length; fontOffset++)
        if (fixedFont.AsSpan(fontOffset, originalName.Length).SequenceEqual(originalName)) fixedName.CopyTo(fixedFont, fontOffset);
    using var fontData = SKData.CreateCopy(fixedFont);
    using var font = SKTypeface.FromData(fontData) ?? throw new InvalidOperationException("Owned package font cannot load.");
    if (!font.FamilyName.StartsWith("Mako", StringComparison.Ordinal) || SKFontManager.Default.FontFamilies.Contains(font.FamilyName, StringComparer.Ordinal))
        throw new InvalidOperationException("Completion representative requires an installed Noto font renamed to an absent Mako family.");
    var files = EffectPackageFixture.Files();
    string fontLogical = "assets/probe-font" + Path.GetExtension(fontPath);
    files[fontLogical] = fixedFont;
    var manifest = EffectPackageFixture.Manifest(files);
    foreach (var entry in manifest["files"]!.AsArray())
        if (entry!["path"]!.GetValue<string>() == fontLogical) entry["role"] = "font";
    var package = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
    var recipe = new TemplateEffectRecipe("completion-recipe", "Original token scale", TemplateEffectTarget.TokenFamily,
        package.PackageId, package.Revision, "scale", EffectParameterOverrides.Parse("{\"peak\":2.25}"),
        readScope: ScriptReadScope.BranchInput, branchInput: "vocals", fixedSeed: ulong.MaxValue,
        configurationId: "completion-configuration");
    var template = StyleTemplate.Create("completion-advanced", "Original advanced package font", StyleTemplateKind.Line,
        LyricStyle.Default with { FontFamilyName = font.FamilyName, FontSizePx = 32, Opacity = .85f },
        [LyricStyleProperty.FontFamilyName, LyricStyleProperty.FontSizePx, LyricStyleProperty.Opacity], [recipe], [package]);
    using (var archive = ZipFile.Open(output, ZipArchiveMode.Create))
    {
        Write(StyleTemplateLibrary.FileName, StyleTemplateLibrary.Encode([template]));
        string packagePrefix = StyleTemplateLibrary.PackagePath(package.Revision) + "/";
        Write(packagePrefix + "manifest.json", package.Manifest.ToArray());
        foreach (var file in package.Files) Write(packagePrefix + file.Path, file.Content.ToArray());
        void Write(string path, byte[] bytes)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            entry.LastWriteTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); entry.ExternalAttributes = 0;
            using var stream = entry.Open(); stream.Write(bytes);
        }
    }
    File.WriteAllText(output + ".json", JsonSerializer.Serialize(new { PackageId = package.PackageId, PackageRevision = package.Revision,
        FontFamily = font.FamilyName, OriginalFontSha256 = Hash(originalFont), FontSha256 = Hash(files[fontLogical]), FontBytes = files[fontLogical].Length,
        ArchiveSha256 = Hash(File.ReadAllBytes(output)), Recipe = "completion-recipe", BranchInput = "vocals", Seed = ulong.MaxValue.ToString(CultureInfo.InvariantCulture) }, json));
    return;
}
if (args.Length == 2 && args[0] == "generate")
{
    string root = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
    string sources = Path.Combine(root, "originals"); Directory.CreateDirectory(sources);
    string image = Path.Combine(sources, "owned-pattern.png"), alternate = Path.Combine(sources, "owned-alternate.png");
    WriteImage(image, false); WriteImage(alternate, true);
    string corrupt = Path.Combine(sources, "owned-corrupt.png"); File.WriteAllBytes(corrupt, "probe-owned non-image bytes"u8.ToArray());
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
    var kinds = new List<string> { "image-static", "image-analysis", "image-package", "image-alternate", "image-corrupt", "image-source-change", "image-capture-stable", "image-lease", "image-shutdown" };
    foreach (string stage in new[] { "Initialize", "Evaluate" }) foreach (string action in new[] { "cancel", "deadline", "close" })
        kinds.Add("runaway-" + stage + "-" + action);
    foreach (string kind in kinds)
    {
        ProjectSnapshot basis = FixtureSources.Create("scale");
        bool package = kind is "image-package" or "image-lease" or "image-shutdown" || kind.StartsWith("runaway-", StringComparison.Ordinal);
        if (kind.StartsWith("runaway-", StringComparison.Ordinal))
        {
            string stage = kind.Split('-')[1], marker = Path.Combine(root, "markers", kind);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            basis = FixtureSources.Runaway(stage, marker, kind, kind == "runaway-Initialize-cancel");
        }
        string original = kind == "image-corrupt" ? corrupt : kind == "image-alternate" ? alternate : image;
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
        fixtures.Add(new { Kind = kind, Package = package, Original = Path.GetRelativePath(root, original),
            FixedPath = Path.GetRelativePath(root, fixedProject.Background.Path!), Sha256 = media.Hash,
            SizeBytes = media.ByteLength, Fit = fixedProject.Background.Fit.ToString(),
            BaseProfile = ProjectPreviewSnapshotProfile.Select(ProjectSnapshot.FromProject(fixedProject) with { Background = BackgroundSource.Color }),
            AnalysisContentHash = fixedProject.AudioAnalysis?.Data?.ContentHash });
    }
    File.WriteAllText(Path.Combine(root, "fixtures.json"), JsonSerializer.Serialize(new { Fixtures = fixtures,
        Scope = "Probe-owned ProjectStore.Save image closures. Saved media and original source are distinct owned files. No public media-edit command is claimed." }, json));
    return;
}
if (args.Length != 8 || args[0] != "render")
    throw new ArgumentException("generate directory | render saved-directory prefix width height transparent comma-times none|background|package");
string savedDirectory = Path.GetFullPath(args[1]), prefix = Path.GetFullPath(args[2]);
Project projectInput = ProjectStore.Load(savedDirectory).Project ?? throw new InvalidOperationException("Oracle project did not load.");
ProjectSnapshot snapshot = ProjectSnapshot.FromProject(projectInput);
byte[] source = File.ReadAllBytes(Path.Combine(savedDirectory, "project.json"));
if (args[7] == "background") snapshot = FixtureSources.Fingerprint(snapshot with { Background = BackgroundSource.Color });
else if (args[7] == "package") snapshot = FixtureSources.WithoutContribution(snapshot, "scale");
else if (args[7] != "none") throw new ArgumentException("Unknown counterfactual.");
using var videos = new FfmpegVideoFrameProvider();
var factory = new ProjectRenderSessionFactory(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), prefix + "-cache", videos, ReplayReason.Initial);
await using var session = await factory.CreateAsync(snapshot);
int index = 0;
foreach (string part in args[6].Split(','))
{
    long time = long.Parse(part, CultureInfo.InvariantCulture);
    using RenderFrame frame = await session.RenderAsync(RenderRequest.At(snapshot, time) with
    {
        Target = new() { PixelWidth = int.Parse(args[3], CultureInfo.InvariantCulture), PixelHeight = int.Parse(args[4], CultureInfo.InvariantCulture) },
        TransparentBackground = bool.Parse(args[5]),
    });
    if (frame.DiagnosticSnapshot.HasError) throw new InvalidOperationException("Independent healthy image Runtime returned Error.");
    byte[] pixels = frame.PixelMemory.ToArray(); string output = prefix + "-" + index.ToString(CultureInfo.InvariantCulture);
    await File.WriteAllBytesAsync(output + ".bgra", pixels);
    using var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
    Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
    using var skImage = SKImage.FromBitmap(bitmap); using var encoded = skImage.Encode(SKEncodedImageFormat.Png, 100);
    await File.WriteAllBytesAsync(output + ".png", encoded.ToArray());
    await File.WriteAllTextAsync(output + ".json", JsonSerializer.Serialize(new
    {
        EngineIdentity = PackageProjectSnapshotCodec.EngineIdentity, SourceSha256 = Hash(source),
        HasError = false, PixelBytes = pixels.Length, BgraSha256 = Hash(pixels), Counterfactual = args[7],
        Width = frame.Width, Height = frame.Height, AlphaMode = bool.Parse(args[5]) ? "straight" : "opaque",
        TimeUs = time.ToString(CultureInfo.InvariantCulture),
        Runtime = frame.DiagnosticSnapshot.ReadAll().Select(Occurrence).ToArray(),
        ImageEffects = frame.ImageEffectDiagnostics.Select(image => new { InstanceId = image.InstanceId.Value, image.ArtifactHash,
            Graph = image.Graph.Identity, image.InputId, image.Code }).ToArray(),
        Materials = frame.MaterialDiagnostics.Select(Material).ToArray(), SourceDiagnostics = Array.Empty<object>(),
        ImageSha256 = projectInput.Background.Media!.Hash,
        BaseProfile = ProjectPreviewSnapshotProfile.Select(snapshot with { Background = BackgroundSource.Color }),
        AnalysisContentHash = snapshot.AudioAnalysis?.Data?.ContentHash,
        Scope = "Independent production ProjectStore.Load + original Runtime reading saved closure. sourceSha256 hashes saved project.json, not HTTP snapshot bytes. No Worker or HTTP result constructs expected pixels.",
    }, json));
    index++;
}
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static object Occurrence(RuntimeDiagnosticOccurrence item) => new
{
    item.Epoch, Sequence = item.Sequence.ToString(CultureInfo.InvariantCulture), InstanceId = item.Diagnostic.InstanceId.Value,
    item.Diagnostic.ArtifactHash, item.Diagnostic.Stage, TimeUs = item.Diagnostic.TimeUs.ToString(CultureInfo.InvariantCulture),
    item.Diagnostic.Diagnostic.Code, Severity = item.Diagnostic.Diagnostic.Severity.ToString().ToLowerInvariant(), item.Diagnostic.Diagnostic.Message,
    item.Diagnostic.Diagnostic.Location, Package = item.Diagnostic.PackageIdentity,
};
static object Material(RenderMaterialDiagnostic value) => new
{
    Material = value.Material.Identity, value.InputId, value.Code,
    Owner = value.Owner switch
    {
        PreparedMaterialOwner.Script script => (object)new { Kind = "script", Script = new { InstanceId = script.InstanceId.Value, script.ArtifactHash, script.PackageIdentity } },
        PreparedMaterialOwner.Source source => new { Kind = "source", Source = new { source.Reference.Path, source.Reference.Region, Slot = (int)source.Slot,
            SourceMaterial = new { source.Reference.Material.PackageId, source.Reference.Material.Revision, source.Reference.Material.ExportId },
            ParametersJson = source.Reference.Material.Parameters.Json } },
        _ => throw new InvalidOperationException("Unknown actual material owner."),
    },
};
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
