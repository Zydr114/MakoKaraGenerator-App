using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;

if (args.Length != 3 || args[0] is not ("prepare" or "render"))
    throw new ArgumentException("prepare saved-directory fps-numerator | render saved-directory output-prefix");
ProjectLoadResult loaded = ProjectStore.Load(Path.GetFullPath(args[1]));
Project project = loaded.Project ?? throw new InvalidOperationException("Owned saved project failed to load: " +
    string.Join(", ", loaded.Report.Issues.Select(issue => issue.Code)));
if (args[0] == "prepare")
{
    bool withoutAudio = args[2] == "noaudio";
    int numerator = withoutAudio ? 30 : int.Parse(args[2], CultureInfo.InvariantCulture);
    FrameRate rate = numerator == 30_000 ? FrameRate.NtscVideo : FrameRate.Fps30;
    LyricLine line = LyricLine.Create("owned-line", "歌詞 KARAOKE", new(800_000, 2_900_000), TimingStatus.Edited,
        [LyricToken.Create("owned-base", "歌詞", new(800_000, 1_800_000), TimingStatus.Edited),
         LyricToken.Create("owned-word", " KARAOKE", new(1_800_000, 2_900_000), TimingStatus.Edited)]) with
    { RubyAnnotations = [RubyAnnotation.Create("owned-ruby", "owned-base", "かし")] };
    Project fixture = Project.Create("Owned HTTP movie lyrics ruby and scene",
        Composition.Create(320, 180, rate, new(20, 40, 100, 255), 3_000_000), LyricsDocument.Create([line]),
        lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 30,
            UnsungColor = new(240, 240, 240, 160), SungColor = new(255, 80, 70, 190) },
        audioPath: withoutAudio ? null : project.AudioPath) with
        { AudioMedia = withoutAudio ? null : project.AudioMedia, AudioAnalysis = project.AudioAnalysis };
    var program = fixture.LyricPresentation.FindProgram("primary")!;
    if (!LeadInEditor.TryCreateCountdown(fixture, program.RootContainerId, "owned countdown", 3,
        out string indicator, out fixture, out var diagnostic)) throw new InvalidOperationException(diagnostic.ToSummary());
    if (!LeadInEditor.TrySetLeadIn(fixture, program.Id,
        new() { Id = "owned-cue", TargetLineId = line.Id, IndicatorContainerId = indicator, DurationUs = 600_000 },
        out fixture, out diagnostic)) throw new InvalidOperationException(diagnostic.ToSummary());
    ProjectStore.Save(fixture, args[1]);
    Console.WriteLine("Owned fixture persisted through ProjectStore: timed lyrics, ruby, countdown scene.");
    return;
}

ProjectSnapshot snapshot = ProjectSnapshot.FromProject(project);
string sourceProfile = StaticProjectSnapshotCodec.SelectProfile(snapshot);
CompositionFramePlan plan = CompositionFramePlan.Create(snapshot.Composition.OutputDurationUs!.Value, snapshot.Composition.FrameRate);
using FfmpegVideoFrameProvider videos = new();
ProjectRenderSessionFactory factory = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
    args[2] + "-unused-cache", videos, ReplayReason.Initial);
foreach (bool transparent in new[] { false, true })
{
    await using IRenderFrameSession session = await factory.CreateAsync(snapshot);
    await using FileStream output = File.Create(args[2] + (transparent ? "-transparent.bgra" : "-opaque.bgra"));
    for (int index = 0; index < plan.TotalFrames; index++)
    {
        using RenderFrame frame = await session.RenderAsync(RenderRequest.At(snapshot, plan.FrameTimeUs(index)) with
        { Quality = RenderQuality.Final, TransparentBackground = transparent });
        await output.WriteAsync(frame.PixelMemory);
    }
}
var primary = snapshot.Lyrics.PrimaryBranch!;
ProjectSnapshot noRuby = snapshot with { Lyrics = snapshot.Lyrics.ReplaceBranch(primary with
{ Lines = primary.Lines.SetItem(0, primary.Lines[0] with { RubyAnnotations = [] }) }) };
ProjectSnapshot noScene = snapshot with { LyricPresentation = snapshot.LyricPresentation with
{ Nodes = [.. snapshot.LyricPresentation.Nodes.Select(node => node.Content is CountdownSequenceContent content
    ? node with { Content = content with { Appearance = content.Appearance with { Opacity = 0 } } } : node)] } };
async Task<int> DifferenceAsync(ProjectSnapshot alternative, long time)
{
    await using IRenderFrameSession original = await factory.CreateAsync(snapshot);
    await using IRenderFrameSession changed = await factory.CreateAsync(alternative);
    using RenderFrame expected = await original.RenderAsync(RenderRequest.At(snapshot, time) with
    { Quality = RenderQuality.Final, TransparentBackground = true });
    using RenderFrame removed = await changed.RenderAsync(RenderRequest.At(alternative, time) with
    { Quality = RenderQuality.Final, TransparentBackground = true });
    int pixels = 0;
    for (int offset = 0; offset < expected.PixelMemory.Length; offset += 4)
        if (!expected.PixelMemory.Span.Slice(offset, 4).SequenceEqual(removed.PixelMemory.Span.Slice(offset, 4))) pixels++;
    return pixels;
}
int rubyPixels = await DifferenceAsync(noRuby, 1_300_000);
int scenePixels = await DifferenceAsync(noScene, 400_000);
if (rubyPixels == 0 || scenePixels == 0) throw new InvalidOperationException("Owned ruby/scene fixture must change actual pixels.");
await File.WriteAllTextAsync(args[2] + ".json", JsonSerializer.Serialize(new
{
    snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight, plan.TotalFrames,
    DerivedVideoDurationUs = plan.VideoDurationUs.ToString(CultureInfo.InvariantCulture),
    FrameRateNumerator = snapshot.Composition.FrameRate.Numerator, FrameRateDenominator = snapshot.Composition.FrameRate.Denominator,
    SnapshotSha256 = Convert.ToHexStringLower(SHA256.HashData(StaticProjectSnapshotCodec.Encode(snapshot, sourceProfile))),
    SourceProfile = sourceProfile, RubyChangedPixels = rubyPixels, SceneChangedPixels = scenePixels,
    Oracle = "Independent saved input loaded by ProjectStore; original Runtime.Hosting Final render sessions at original CompositionFramePlan times.",
}, OracleJson.Options));

internal static class OracleJson
{
    internal static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}
