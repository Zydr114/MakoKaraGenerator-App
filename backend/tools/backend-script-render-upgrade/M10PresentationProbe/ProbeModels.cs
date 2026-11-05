using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;
using MakoKaraGenerator.Rendering.Abstractions;

namespace M10.Presentation.Probe;

internal static class ProbeJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static T Read<T>(string path) where T : class
    {
        // This is an internal, finite fixture/host manifest, never author input.
        if (new FileInfo(path).Length is <= 0 or > 1024 * 1024) throw new ProbeFailure("probe-json-size-invalid");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new ProbeFailure("probe-json-empty");
    }

    internal static void Write(string path, object value)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(output, value, Options);
        output.Flush(true);
    }
}

internal sealed record FixtureSet(int Version, string Oracle, FixtureCase[] Cases)
{
    internal void Validate()
    {
        if (Version != 1 || Cases.Length is < 1 or > 32 || Cases.Select(c => c.Name).Distinct().Count() != Cases.Length)
            throw new ProbeFailure("probe-fixture-set-invalid");
        foreach (FixtureCase item in Cases) item.Validate();
    }
}

internal sealed record FixtureCase(string Name, string File, long ByteLength, string Sha256, int Family,
    MediaSourceKind Kind, int StreamIndex, int Width, int Height, int CanonicalWidth, int CanonicalHeight,
    int SarNumerator, int SarDenominator, MediaQuarterTurn Rotation, bool Mirror,
    MediaTransfer Transfer, MediaMatrix Matrix, MediaRange Range, MediaAlpha Alpha,
    MediaChromaLocation ChromaLocation, MediaNativePixelFormat NativePixelFormat,
    bool MissingTagOverride, ExpectedFrame[] Frames, string PixelsFile, long PixelsByteLength,
    string PixelsSha256, double PixelAbsoluteTolerance, string PixelOracle,
    PresentationEndAuthority TailAuthority, long? VideoDurationUs)
{
    internal void Validate()
    {
        if (Name.Length is < 1 or > 80 || Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))
            || Family is < 1 or > 4 || Frames.Length is < 1 or > 64 || StreamIndex != 0
            || Kind is not (MediaSourceKind.Still or MediaSourceKind.Video)
            || Width is < 1 or > 128 || Height is < 1 or > 128
            || CanonicalWidth is < 1 or > 128 || CanonicalHeight is < 1 or > 128
            || ByteLength is <= 0 or > 16 * 1024 * 1024 || Sha256.Length != 64 || PixelsSha256.Length != 64
            || PixelsByteLength != checked((long)CanonicalWidth * CanonicalHeight * 16 * Frames.Length)
            || PixelAbsoluteTolerance is < 0 or > .05 || !double.IsFinite(PixelAbsoluteTolerance))
            throw new ProbeFailure("probe-fixture-case-invalid");
        if (Kind == MediaSourceKind.Still && (Frames.Length != 1 || Frames[0].Pts is not null || VideoDurationUs is not null
            || TailAuthority != PresentationEndAuthority.Timeless)) throw new ProbeFailure("probe-still-oracle-invalid");
        if (Kind == MediaSourceKind.Video && (Frames.Any(f => f.Pts is null || f.End is null) || VideoDurationUs is null))
            throw new ProbeFailure("probe-video-oracle-invalid");
    }

    internal TrustedMediaColorInterpretation? Override => MissingTagOverride ? new(Transfer, Matrix, Range,
        Alpha, ChromaLocation, new(new string('e', 64))) : null;
}

internal sealed record ExpectedFrame(ExactTime? Pts, ExactTime? End, long? StartUs, long? EndUs);
internal sealed record ExactTime(long Ticks, int Numerator, int Denominator)
{
    internal bool EqualsTime(RationalTimestamp actual) => Numerator > 0 && Denominator > 0
        && (BigInteger)Ticks * Numerator * actual.TimeBase.Denominator
        == (BigInteger)actual.Ticks * actual.TimeBase.Numerator * Denominator;
}

internal sealed record LoadedFile(string Path, long ByteLength, string Sha256, string LogicalId, bool Executable);
internal sealed record ClosureManifest(int Version, string Provenance, string Gate, string Helper,
    string FixturesSha256, LoadedFile[] Files)
{
    internal MediaPresentationDeployment Deployment()
    {
        if (Version != 1 || Provenance != "actual-type4-type5-discovery-not-acceptance" || Files.Length is < 2 or > 1024)
            throw new ProbeFailure("probe-manifest-invalid");
        MediaLoadedToolFile[] entries = Files.Select(file => new MediaLoadedToolFile(file.Path,
            new(file.LogicalId, file.ByteLength, new(file.Sha256)))).ToArray();
        return new(Gate, Helper, entries.Where((_, i) => Files[i].Executable).ToArray(),
            entries.Where((_, i) => !Files[i].Executable).ToArray());
    }
}

internal sealed class PrivateFixtureLease : IMediaFileLease
{
    private readonly FileStream _lock;
    internal bool Released { get; private set; }
    public MediaFileContent Content { get; }
    public string TrustedLocalPath { get; }

    internal PrivateFixtureLease(string path, FixtureCase expected)
        : this(path, new MediaFileContent(new(expected.Sha256), expected.ByteLength)) { }

    internal PrivateFixtureLease(string path, MediaFileContent expected)
    {
        TrustedLocalPath = path;
        Content = expected;
        _lock = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        try
        {
            if (_lock.Length != expected.ByteLength || ProbeFiles.Hash(_lock) != expected.Sha256.LowerHex)
                throw new ProbeFailure("probe-fixture-content-changed");
        }
        catch { _lock.Dispose(); throw; }
    }

    public ValueTask DisposeAsync() { _lock.Dispose(); Released = true; return ValueTask.CompletedTask; }
}

internal sealed record RejectionSet(int Version, string FixturesSha256, RejectionCase[] Cases)
{
    internal void Validate()
    {
        if (Version != 1 || FixturesSha256.Length != 64 || Cases.Length is < 1 or > 32
            || Cases.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != Cases.Length)
            throw new ProbeFailure("probe-rejection-set-invalid");
        foreach (RejectionCase item in Cases)
            if (item.Name.Length is < 1 or > 80 || item.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))
                || item.ByteLength is <= 0 or > 16 * 1024 * 1024 || item.Sha256.Length != 64
                || item.Kind is not (MediaSourceKind.Still or MediaSourceKind.Video) || item.StreamIndex is < 0 or > 128
                || item.ExpectedFailure is not (MediaAbFailure.ColorUnsupported or MediaAbFailure.ProfileUnsupported))
                throw new ProbeFailure("probe-rejection-case-invalid");
    }
}

internal sealed record RejectionCase(string Name, string File, long ByteLength, string Sha256,
    MediaSourceKind Kind, int StreamIndex, MediaAbFailure ExpectedFailure, bool NativeExpected, string Reason);

internal static class ProbeFiles
{
    internal static string Hash(string path) { using var file = System.IO.File.OpenRead(path); return Hash(file); }
    internal static string Hash(Stream file)
    {
        long saved = file.Position;
        try { file.Position = 0; return Convert.ToHexStringLower(SHA256.HashData(file)); }
        finally { file.Position = saved; }
    }

    internal static string Child(string root, string name)
    {
        string fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string value = Path.GetFullPath(Path.Combine(root, name));
        if (!value.StartsWith(fullRoot, StringComparison.Ordinal) || !System.IO.File.Exists(value)
            || new FileInfo(value).LinkTarget is not null) throw new ProbeFailure("probe-fixture-path-invalid");
        return value;
    }
}

internal sealed class JournalQuarantine(EvidenceJournal journal, string scenario) : IEnvelopeQuarantine
{
    private readonly List<EnvelopeQuarantine> _records = [];
    internal int Count { get { lock (_records) return _records.Count; } }
    public void Retain(EnvelopeQuarantine record)
    {
        lock (_records) _records.Add(record);
        journal.Write(scenario, "quarantine-retained", new { record.SafeReason, record.Identity,
            nativeBytes = record.Reservation.Bytes, transportBytes = record.TransportReservation.Bytes });
    }
}

internal static class AuditDrain
{
    internal static void Require(AuditedEnvelopeBackend backend, JournalQuarantine quarantine,
        EvidenceJournal journal, string scenario, long liveBytes, int retained)
    {
        var session = backend.Opened;
        if (quarantine.Count != 0 || retained != 0 || liveBytes != 0 || session is null
            || !session.GateReleased || !session.Unreferenced || !session.Disposed
            || session.Last is not { Terminal: true, CgroupPopulated: false } last
            || !(last.CgroupExists || last.CgroupRemoved)) throw new ProbeFailure("probe-owned-drain-incomplete");
        if (session.Held is not { } held) throw new ProbeFailure("probe-held-identity-missing");
        string statPath = "/proc/" + held.MainPid + "/stat";
        bool sameIdentity = false;
        try
        {
            string stat = System.IO.File.ReadAllText(statPath);
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            sameIdentity = ulong.Parse(fields[19], System.Globalization.CultureInfo.InvariantCulture) == held.MainStartTicks;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        journal.Write(scenario, "exact-process-after-drain", new { held.MainPid, held.MainStartTicks, sameIdentity });
        if (sameIdentity) throw new ProbeFailure("probe-exact-owned-process-still-live");
    }
}
