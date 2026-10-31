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

if (args.Length != 3) throw new ArgumentException("saved-directory output-prefix frames-per-output");
ProjectLoadResult loaded = ProjectStore.Load(args[0]);
Project project = loaded.Project ?? throw new InvalidOperationException("The saved HTTP project did not load.");
ProjectSnapshot snapshot = ProjectSnapshot.FromProject(project);
int frames = int.Parse(args[2], CultureInfo.InvariantCulture);
List<RuntimeValidationProgress> progress = [];
ProjectRenderSessionFactory factory = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
    args[1] + "-cache", new FfmpegVideoFrameProvider(), ReplayReason.Initial);
RuntimeValidationResult result = await new RuntimeFrameValidator(factory).ValidateAsync(snapshot,
    new RuntimeValidationPlan(frames), new InlineProgress(progress));
await File.WriteAllTextAsync(args[1] + ".json", JsonSerializer.Serialize(new
{
    result.Succeeded, result.OpaqueFrames, result.TransparentFrames,
    Issues = result.Issues.Select(issue => new
    {
        issue.Transparent, TimeUs = issue.TimeUs.ToString(CultureInfo.InvariantCulture),
        issue.Code, issue.Message, issue.IsError, issue.Location, issue.SampleFailureCode,
    }),
    Diagnostics = result.Diagnostics,
    Progress = progress.Select(value => new
    {
        value.CompletedFrames, value.TotalFrames, value.Transparent,
        TimeUs = value.TimeUs.ToString(CultureInfo.InvariantCulture),
    }),
    Source = new
    {
        snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight, FramesPerOutput = frames,
        FrameRateNumerator = snapshot.Composition.FrameRate.Numerator,
        FrameRateDenominator = snapshot.Composition.FrameRate.Denominator,
        OutputDurationUs = snapshot.Composition.OutputDurationUs?.ToString(CultureInfo.InvariantCulture),
        SourceProfile = StaticProjectSnapshotCodec.Profile,
        SnapshotSha256 = Convert.ToHexStringLower(SHA256.HashData(StaticProjectSnapshotCodec.Encode(snapshot))),
        LoadedSavedDirectory = Path.GetFullPath(args[0]),
    },
    Oracle = "Independent ProjectStore.Load and existing RuntimeFrameValidator with real Runtime.Hosting sessions",
}, OracleJson.Options));

internal static class OracleJson
{
    internal static readonly JsonSerializerOptions Options = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}

internal sealed class InlineProgress(List<RuntimeValidationProgress> values) : IProgress<RuntimeValidationProgress>
{
    public void Report(RuntimeValidationProgress value) => values.Add(value);
}
