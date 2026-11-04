using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using SkiaSharp;

if (args.Length != 3) throw new ArgumentException("saved-project output-prefix transparent");
string directory = Path.GetFullPath(args[0]);
string prefix = Path.GetFullPath(args[1]);
bool transparent = bool.Parse(args[2]);
var project = ProjectStore.Load(directory).Project ?? throw new InvalidOperationException("Saved project did not load");
var snapshot = ProjectSnapshot.FromProject(project);
if (!snapshot.Background.HasVideo || snapshot.Background.Media is null || snapshot.Background.Path is null)
    throw new InvalidOperationException("Expected saved video media closure");
var sourcePath = snapshot.Background.Path;
if (!Path.IsPathFullyQualified(sourcePath)) sourcePath = Path.GetFullPath(Path.Combine(directory, sourcePath));
if (!File.Exists(sourcePath)) throw new InvalidOperationException("Saved video closure is unavailable");
using var provider = new OriginalSizeProvider();
var factory = new ProjectRenderSessionFactory(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), prefix + "-cache"), provider, ReplayReason.Initial);
await using var session = await factory.CreateAsync(snapshot);
var plan = CompositionFramePlan.Create(snapshot.Composition.OutputDurationUs!.Value, snapshot.Composition.FrameRate);
List<object> frames = [];
for (int i = 0; i < plan.TotalFrames; i++)
{
    long timeUs = plan.FrameTimeUs(i);
    using var frame = await session.RenderAsync(RenderRequest.At(snapshot, timeUs) with
    {
        Target = new() { PixelWidth = 480, PixelHeight = 270 },
        Quality = RenderQuality.Final, TransparentBackground = transparent,
    });
    if (frame.Width != 480 || frame.Height != 270 || frame.Stride != 480 * 4 || frame.DiagnosticSnapshot.HasError)
        throw new InvalidOperationException("Unexpected frame geometry, stride or diagnostics");
    byte[] pixels = frame.PixelMemory.ToArray();
    string name = prefix + "-" + i.ToString("D3", CultureInfo.InvariantCulture);
    await File.WriteAllBytesAsync(name + ".bgra", pixels);
    using var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
    Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
    using var image = SKImage.FromBitmap(bitmap);
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    await File.WriteAllBytesAsync(name + ".png", png.ToArray());
    frames.Add(new { Index = i, TimeUs = timeUs.ToString(CultureInfo.InvariantCulture),
        Width = frame.Width, Height = frame.Height, BgraSha256 = Hash(pixels), PngSha256 = Hash(png.ToArray()) });
}
if (!transparent && (provider.Reads == 0 || provider.BadSize))
    throw new InvalidOperationException("Original-size video oracle did not receive 1920x1080 frames");
await File.WriteAllTextAsync(prefix + ".json", JsonSerializer.Serialize(new {
    EngineIdentity = MakoKaraGenerator.Backend.Rendering.StaticProjectSnapshotCodec.EngineIdentity,
    BaseProfile = MakoKaraGenerator.Backend.Rendering.ProjectSnapshotSourceProfile.Select(snapshot with { Background = BackgroundSource.Color }),
    RuntimeInputProfile = MakoKaraGenerator.Backend.Rendering.ProjectRuntimeInputCodec.Profile,
    ProjectJsonSha256 = Hash(await File.ReadAllBytesAsync(Path.Combine(directory, "project.json"))),
    MediaSha256 = Hash(await File.ReadAllBytesAsync(sourcePath)),
    snapshot.Background.Media.ByteLength, snapshot.Background.Media.Hash,
    FrameRateNumerator = snapshot.Composition.FrameRate.Numerator,
    FrameRateDenominator = snapshot.Composition.FrameRate.Denominator,
    TotalFrames = plan.TotalFrames, Transparent = transparent, provider.Reads, provider.BadSize, Frames = frames,
    Scope = "Saved ProjectStore input and direct Runtime; wrapper forces original-size video source before Skia target sampling. Project JSON SHA is not HTTP snapshot SHA. Transparent render omits background."
}, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

sealed class OriginalSizeProvider : IVideoFrameProvider
{
    private readonly FfmpegVideoFrameProvider _inner = new();
    public int Reads { get; private set; }
    public bool BadSize { get; private set; }
    public IVideoFrameSource? Open(string path, out string? failureReason)
    {
        var source = _inner.Open(path, out failureReason);
        return source is null ? null : new OriginalSizeSource(source, this);
    }
    public void Dispose() => _inner.Dispose();
    private sealed class OriginalSizeSource(IVideoFrameSource inner, OriginalSizeProvider owner) : IVideoFrameSource
    {
        public long DurationUs => inner.DurationUs;
        public double FrameRate => inner.FrameRate;
        public int Width => inner.Width;
        public int Height => inner.Height;
        public string? LastError => inner.LastError;
        public bool TryGetFrame(long time, out VideoFrame? frame) => TryGetFrame(time, out frame, CancellationToken.None);
        public bool TryGetFrame(long time, out VideoFrame? frame, CancellationToken token)
        {
            bool ok = inner.TryGetFrame(time, out frame, token);
            if (ok && frame is not null) { owner.Reads++; owner.BadSize |= frame.Width != 1920 || frame.Height != 1080; }
            return ok;
        }
        public void Dispose() => inner.Dispose();
    }
}
