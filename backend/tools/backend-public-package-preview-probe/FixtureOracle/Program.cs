using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.TestSupport;
using PublicPackagePreviewProbe;
using SkiaSharp;

if (args.Length >= 2 && args[0] == "generate")
{
    string directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
    Dictionary<string, ProjectSnapshot> fixtures = new(StringComparer.Ordinal);
    foreach (string kind in new[] { "scale", "graph", "sprite", "material" }) fixtures.Add(kind, FixtureSources.Create(kind));
    foreach (string kind in new[] { "compile", "compile-source", "image", "material", "script-material" }) fixtures.Add("diagnostic-" + kind, FixtureSources.DiagnosticSource(kind));
    string audioPath = Path.Combine(directory, "owned.wav");
    using (var wave = new BinaryWriter(File.Create(audioPath)))
    {
        const int samples = 48000; wave.Write(Encoding.ASCII.GetBytes("RIFF")); wave.Write(36 + samples * 2);
        wave.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); wave.Write(16); wave.Write((short)1); wave.Write((short)1);
        wave.Write(48000); wave.Write(96000); wave.Write((short)2); wave.Write((short)16);
        wave.Write(Encoding.ASCII.GetBytes("data")); wave.Write(samples * 2);
        for (int n = 0; n < samples; n++) wave.Write((short)(Math.Sin(n * Math.PI * 440 / 24000) * 2000));
    }
    byte[] audioBytes = File.ReadAllBytes(audioPath);
    fixtures.Add("audio-scale", FixtureSources.Create("scale") with { AudioPath = audioPath, AudioMedia = new(Hash(audioBytes), audioBytes.Length, "owned.wav") });
    fixtures.Add("runtime-error", FixtureSources.RuntimeError());
    fixtures.Add("static", FixtureSources.Create("scale") with { Effects = ProjectEffects.Empty, EffectInputsFingerprint = null });
    fixtures.Add("pages", CustomWarnings(130));
    fixtures.Add("over-budget", CustomWarnings(270));
    foreach (string stage in new[] { "Initialize", "Evaluate" }) foreach (string action in new[] { "cancel", "deadline", "close" })
    {
        string name = "runaway-" + stage + "-" + action;
        string marker = Path.Combine(directory, "markers", name); Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        fixtures.Add(name, FixtureSources.Runaway(stage, marker, name, stage == "Initialize" && action == "cancel"));
    }
    foreach (var pair in fixtures)
    {
        var source = pair.Value;
        var project = Project.Create(source.Name, source.Composition, source.Lyrics, lyricStyle: source.LyricStyle) with
        { Effects = source.Effects, LyricPresentation = source.LyricPresentation, Background = source.Background,
            PresetApplications = source.PresetApplications, EnterAnimation = source.EnterAnimation,
            LyricStylePresetId = source.LyricStylePresetId, AudioPath = source.AudioPath, AudioMedia = source.AudioMedia, AudioAnalysis = source.AudioAnalysis };
        ProjectStore.Save(project, Path.Combine(directory, pair.Key));
    }
    await File.WriteAllTextAsync(Path.Combine(directory, "fixtures.json"), JsonSerializer.Serialize(new { Kinds = fixtures.Keys,
        Scope = "Probe-owned production ProjectStore.Save closures; no public author mutation is claimed. Runaway marker paths belong only to this output." }, OracleJson.Options));
    return;
}
if (args.Length is not (8 or 9) || args[0] != "render") throw new ArgumentException("generate directory | render saved-directory prefix width height transparent comma-times counterfactual-kind-or-none");
string saved = Path.GetFullPath(args[1]), prefix = Path.GetFullPath(args[2]);
Project projectInput = ProjectStore.Load(saved).Project ?? throw new InvalidOperationException("Oracle project did not load.");
ProjectSnapshot snapshot = ProjectSnapshot.FromProject(projectInput);
if (args[7] != "none") snapshot = FixtureSources.WithoutContribution(snapshot, args[7]);
using var videos = new FfmpegVideoFrameProvider();
var factory = new ProjectRenderSessionFactory(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), prefix + "-cache", videos, ReplayReason.Initial);
byte[] sourceBytes = snapshot.Effects.Packages.IsEmpty ? StaticProjectSnapshotCodec.Encode(snapshot) : PackageProjectSnapshotCodec.Encode(snapshot);
int index = 0;
try
{
    await using var session = await factory.CreateAsync(snapshot);
    foreach (string part in args[6].Split(','))
    {
        long time = long.Parse(part, CultureInfo.InvariantCulture);
        using RenderFrame frame = await session.RenderAsync(RenderRequest.At(snapshot, time) with { Target = new() {
            PixelWidth = int.Parse(args[3], CultureInfo.InvariantCulture), PixelHeight = int.Parse(args[4], CultureInfo.InvariantCulture) }, TransparentBackground = bool.Parse(args[5]) });
        byte[] pixels = frame.PixelMemory.ToArray();
        string output = prefix + "-" + index.ToString(CultureInfo.InvariantCulture);
        if (!frame.DiagnosticSnapshot.HasError)
        {
            await File.WriteAllBytesAsync(output + ".bgra", pixels);
            using var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
            using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(output + ".png", encoded.ToArray());
        }
        await File.WriteAllTextAsync(output + ".json", JsonSerializer.Serialize(new
        {
            EngineIdentity = PackageProjectSnapshotCodec.EngineIdentity, SourceSha256 = Hash(sourceBytes), TimeUs = part,
            HasError = frame.DiagnosticSnapshot.HasError, PixelBytes = pixels.Length, BgraSha256 = Hash(pixels),
            Runtime = frame.DiagnosticSnapshot.ReadAll().Select(Occurrence).ToArray(),
            ImageEffects = frame.ImageEffectDiagnostics.Select(image => new { InstanceId = image.InstanceId.Value, image.ArtifactHash,
                Graph = image.Graph.Identity, image.InputId, image.Code }).ToArray(),
            Materials = frame.MaterialDiagnostics.Select(Material).ToArray(),
            SourceDiagnostics = Array.Empty<object>(),
            Scope = "Independent public-save ProjectStore.Load and direct Runtime; no HTTP/Worker values used to construct expected pixels or diagnostics. Error pixels deliberately not saved.",
        }, OracleJson.Options));
        index++;
    }
}
catch (EffectFailureException error)
{
    var actual = error.DiagnosticSnapshot ?? (error.Diagnostic is null ? RuntimeDiagnosticSnapshot.Empty : RuntimeDiagnosticSnapshot.FromArray([error.Diagnostic]));
    await File.WriteAllTextAsync(prefix + "-" + index.ToString(CultureInfo.InvariantCulture) + ".json", JsonSerializer.Serialize(new
    { EngineIdentity = PackageProjectSnapshotCodec.EngineIdentity, SourceSha256 = Hash(sourceBytes), HasError = true, FailureCode = error.Code,
        Runtime = actual.ReadAll().Select(Occurrence).ToArray(), ImageEffects = Array.Empty<object>(), Materials = Array.Empty<object>(), SourceDiagnostics = Array.Empty<object>(),
        Scope = "Actual independent Runtime EffectFailureException; full actual diagnostic snapshot, no pixel artifact and no claimed drawing-stage side diagnostics.",
    }, OracleJson.Options));
}
catch (ProjectEffectCompilationException error)
{
    await File.WriteAllTextAsync(prefix + "-0.json", JsonSerializer.Serialize(new
    { EngineIdentity = PackageProjectSnapshotCodec.EngineIdentity, SourceSha256 = Hash(sourceBytes), HasError = true,
        Runtime = Array.Empty<object>(), ImageEffects = Array.Empty<object>(), Materials = Array.Empty<object>(),
        SourceDiagnostics = error.Diagnostics.Select(value => new { value.PackageId, value.Revision, value.InstanceId, value.ExportId,
            value.Source.Code, value.Source.Message, value.Source.IsError, value.Source.Path, value.Source.Line, value.Source.Column }).ToArray(),
        Scope = "Actual independent Runtime compilation exception; no synthetic runtime occurrence and no pixel artifact.",
    }, OracleJson.Options));
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
static ProjectSnapshot CustomWarnings(int count)
{
    string source = "using MakoKaraGenerator.Scripting; public sealed class PackEffect:EffectBase { private bool reported; public override void Evaluate(FrameContext frame,FrameWriter output) { if(reported)return;reported=true; for(int i=0;i<" + count.ToString(CultureInfo.InvariantCulture) + ";i++) frame.Diagnostics.Report(DiagnosticSeverity.Warning,\"ActualPageWarning\",\"Actual warning \"+i); } }";
    var files = EffectPackageFixture.Files(); files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(source);
    var package = EffectPackageContents.Create(EffectPackageFixture.Bytes(EffectPackageFixture.Manifest(files)), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
    var basis = FixtureSources.Create("scale");
    return FixtureSources.Fingerprint(basis with { Effects = new([package], [basis.Effects.Instances[0] with { PackageId = package.PackageId, Revision = package.Revision }]) });
}
internal static class OracleJson
{
    internal static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true };
}
