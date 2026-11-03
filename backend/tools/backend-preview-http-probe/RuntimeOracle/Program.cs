using MakoKaraGenerator.Scripting.Compilation;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;

if (args.Length is not (6 or 7)) throw new ArgumentException("saved-directory output-prefix timeUs width height transparent [owned-audio-declaration]");
ProjectLoadResult loaded = ProjectStore.Load(args[0]);
Project project = loaded.Project ?? throw new InvalidOperationException("The saved HTTP project did not load.");
if (args.Length == 7)
{
    if (!Path.IsPathFullyQualified(args[6])) throw new ArgumentException("The test-owned declaration must be absolute.");
    // Declaration normalization only: never read the optional resource path.
    project = project with { AudioPath = args[6] };
}
ProjectSnapshot snapshot = ProjectSnapshot.FromProject(project);
if (args.Length == 7)
{
    string profile = StaticProjectSnapshotCodec.SelectProfile(snapshot);
    byte[] source = StaticProjectSnapshotCodec.Encode(snapshot, profile);
    await File.WriteAllBytesAsync(args[1] + ".snapshot.json", source);
    byte[]? analysis = snapshot.AudioAnalysis?.Data is { } data ? AudioAnalysisJson.Encode(data) : null;
    if (analysis is not null) await File.WriteAllBytesAsync(args[1] + ".analysis.json", analysis);
    await File.WriteAllTextAsync(args[1] + ".source.json", JsonSerializer.Serialize(new
    {
        SourceProfile = profile, SourceSha256 = Convert.ToHexStringLower(SHA256.HashData(source)),
        AnalysisContentHash = snapshot.AudioAnalysis?.Data?.ContentHash, CompleteAnalysisBytes = analysis?.Length,
        AudioDeclarationNormalized = true,
        SourceShaMeaning = "Complete loaded source with only its declaration path normalized to the same test-owned HTTP declaration; no IO through that parameter. Not the unmodified package path SHA.",
    }, SourceEvidenceJson.Options));
}
long time = long.Parse(args[2], CultureInfo.InvariantCulture);
int width = int.Parse(args[3], CultureInfo.InvariantCulture);
int height = int.Parse(args[4], CultureInfo.InvariantCulture);
bool transparent = bool.Parse(args[5]);
ProjectRenderSessionFactory factory = new(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), args[1] + "-cache"), new FfmpegVideoFrameProvider(), ReplayReason.Initial);
await using IRenderFrameSession session = await factory.CreateAsync(snapshot);
using RenderFrame frame = await session.RenderAsync(RenderRequest.At(snapshot, time) with
{
    Target = new RenderTarget { PixelWidth = width, PixelHeight = height },
    TransparentBackground = transparent,
});
byte[] pixels = frame.PixelMemory.ToArray();
await File.WriteAllBytesAsync(args[1] + ".bgra", pixels);
await File.WriteAllTextAsync(args[1] + ".json", JsonSerializer.Serialize(new
{
    frame.Width, frame.Height, frame.Stride, frame.Format, frame.TimeUs,
    PixelBytes = pixels.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(pixels)),
    LoadedSavedDirectory = Path.GetFullPath(args[0]),
    SnapshotSource = "Independent ProjectStore.Load and real Runtime.Hosting render",
}));

internal static class SourceEvidenceJson
{
    internal static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}
