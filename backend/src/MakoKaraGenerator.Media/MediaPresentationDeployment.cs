using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;

namespace MakoKaraGenerator.Media;

/// <summary>Internal immutable deployment of the controlled gate helper and complete permitted loaded closure.</summary>
/// <remarks>This is host configuration, never an HTTP or author-supplied switch. Exact executable/dependency bytes are hashed before and after every native operation.</remarks>
public sealed class MediaPresentationDeployment
{
    private readonly MediaLoadedToolFile[] _files;
    private readonly MediaToolIdentity _tool;
    private readonly string _gate, _helper;

    /// <summary>Freezes a finite logical manifest and private installation map.</summary>
    /// <param name="gateExecutable">Absolute packaged execution gate.</param>
    /// <param name="helperExecutable">Absolute packaged same-chain libav helper.</param>
    /// <param name="executables">Pinned gate and helper executable entries.</param>
    /// <param name="dependencies">Pinned complete native dependency closure; no optional untracked libraries.</param>
    public MediaPresentationDeployment(string gateExecutable, string helperExecutable,
        IReadOnlyList<MediaLoadedToolFile> executables, IReadOnlyList<MediaLoadedToolFile> dependencies)
    {
        if (!Path.IsPathFullyQualified(gateExecutable) || !Path.IsPathFullyQualified(helperExecutable) ||
            executables.Count is < 2 or > MediaAbCapacityV1.MaximumToolManifestEntries || dependencies.Count > MediaAbCapacityV1.MaximumToolManifestEntries - executables.Count)
            throw Unsupported("Controlled decoder deployment has invalid executable locations or manifest capacity.");
        _gate = gateExecutable; _helper = helperExecutable;
        _files = executables.Concat(dependencies).ToArray();
        if (!_files.Any(file => string.Equals(file.TrustedPath, _gate, StringComparison.Ordinal)) || !_files.Any(file => string.Equals(file.TrustedPath, _helper, StringComparison.Ordinal)) ||
            _files.Select(file => file.TrustedPath).Distinct(StringComparer.Ordinal).Count() != _files.Length ||
            _files.Any(file => !Path.IsPathFullyQualified(file.TrustedPath) || file.TrustedPath.Length > 4096)) throw Unsupported("Controlled decoder manifest does not uniquely bind its private gate helper and dependency paths.");
        var writer = new MediaAbIdentityWriter();
        MediaDigest exe = writer.ComputeExecutableManifestDigest(executables.Select(value => value.ManifestEntry).ToArray());
        MediaDigest deps = writer.ComputeDependencyManifestDigest(dependencies.Select(value => value.ManifestEntry).ToArray());
        MediaDigest pipeline = new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizedPipeline))));
        var preliminary = new MediaToolIdentity(exe, deps, pipeline, default);
        _tool = new(exe, deps, pipeline, writer.ComputeToolDigest(preliminary));
    }

    /// <summary>Fixed complete pipeline text; no local paths or ambient defaults enter identity.</summary>
    public const string NormalizedPipeline =
        "media-decoder-sdr-rgba32f-v1\n" +
        "helper=mkg-libav-same-frame-v1;ffmpeg=9;software-builtin=h264,hevc,mpeg4,vp8,vp9,ffv1,png,mjpeg\n" +
        "single-selected-stream;threads=1;custom-file-io;no-extra-io;no-find-stream-info;no-parser;no-fill-in;mov-advanced-editlist=0\n" +
        "frame-pts=actual-AVFrame.pts;copy-opaque=unique-one-packet-one-frame;packet-sha256=source-payload;drain=NULL-to-EOF\n" +
        "timing=nonfragmented-stts-ctts-stsz-stsc-stco-co64-or-matroska-real-DefaultDuration-BlockDuration;no-fps-no-stream-duration-estimates\n" +
        "still=single-PNG8-16-or-JPEG8-baseline-progressive;compressed-still-max=67108864;ICC-HDR-animation-refused\n" +
        "native=RGB-RGBA8-16LE-or-planar-YUV420-422-444-8-10LE;negative-stride=exact-top-left-row-copy;gray-palette=lossless-expansion\n" +
        "media-jfif-srgb-bt601-full-v1;JPEG=opaque,sRGB,full,BT601,center-chroma;JPEG-structural-RGB-libav-default-matrix5=RGB24-components;IDCT=builtin-bitexact;no-auto-color\n" +
        "media-display-rational-sar-quarterturn-v1;Exif=8-orthogonal;mirror-before-clockwise-quarterturn;SAR=positive-reduced-no-resample;PNG-libav-pHYs-density-SAR=reciprocal\n" +
        "YUV709=R:Y+1.5748Cr,B:Y+1.8556Cb,G:Y-.1873242729306488Cb-.4681242729306488Cr\n" +
        "JPEG601=R:Y+1.402Cr,B:Y+1.772Cb,G:Y-.34413628620102216Cb-.7141362862010221Cr\n" +
        "chroma=bilinear-edge-clamp;luma-centers=i+.5,j+.5;left-x=2i+.5;center-x=2i+1;420-y=2j+1\n" +
        "EOTF=double-sRGB-or-BT709;YUV-encoded-RGB-saturate-only;straight-alpha;premultiply-linear;single-binary32-cast;canonical-positive-zero\n" +
        "capacity=media-ab-capacity-v1;native-hard-envelope-and-live-managed-backing<=268435456;fd3-record-max=16384;ACK=awaited-frame-consumer\n";

    /// <summary>Composite content identity of the frozen tool configuration.</summary>
    public MediaToolIdentity Tool => _tool;
    internal IReadOnlyList<MediaLoadedToolFile> Files => _files;
    internal long WireCapacityBytes => checked(MediaAbCapacityV1.MaximumMetadataRecordBytes + _files.Sum(value => value.TrustedPath.Length * 4L + 768) + 4096);

    internal EnvelopeCommand Command(IMediaFileLease source, int family, int stream, bool inspect) => new(_gate, _helper,
        [family.ToString(CultureInfo.InvariantCulture), stream.ToString(CultureInfo.InvariantCulture), inspect ? "inspect" : "decode", source.TrustedLocalPath, "mkg-libav-same-frame-v1"]);

    internal async ValueTask VerifyFilesAsync(IMediaWorkingSetBudget budget, CancellationToken token)
    {
        using IMediaMemoryReservation reserved = budget.Reserve(64 * 1024 + 4096, MediaWorkingSetPurpose.Metadata);
        byte[] scratch = new byte[64 * 1024];
        foreach (MediaLoadedToolFile file in _files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(file.TrustedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
                await MediaPresentationSourceVerification.VerifyAsync(stream, new(file.ManifestEntry.ContentSha256, file.ManifestEntry.ByteLength), scratch, token, MediaAbFailure.ProfileUnsupported).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw Unsupported("Controlled decoder deployment file cannot be read under its pinned local content policy."); }
        }
    }
    private static MediaAbException Unsupported(string detail) => new(MediaAbFailure.ProfileUnsupported, detail);
}

internal static class MediaPresentationSourceVerification
{
    internal static async ValueTask VerifyAsync(Stream source, MediaFileContent expected, byte[] scratch, CancellationToken token,
        MediaAbFailure failure = MediaAbFailure.SourceChanged)
    {
        if (!source.CanRead || !source.CanSeek || source.Length != expected.ByteLength || expected.ByteLength <= 0 || expected.ByteLength > MediaAbCapacityV1.MaximumSourceBytes)
            throw new MediaAbException(failure, "Source or controlled tool length does not match its pinned content identity.");
        long saved = source.Position;
        try
        {
            source.Position = 0; using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long bytes = 0; int got;
            while ((got = await source.ReadAsync(scratch, token).ConfigureAwait(false)) != 0) { bytes = checked(bytes + got); if (bytes > expected.ByteLength) throw new MediaAbException(failure, "Source grew beyond its pinned byte length."); hash.AppendData(scratch, 0, got); }
            if (bytes != expected.ByteLength || source.Length != expected.ByteLength || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expected.Sha256.LowerHex, StringComparison.Ordinal))
                throw new MediaAbException(failure, "Source or controlled tool content changed from its pinned SHA256 identity.");
        }
        finally { source.Position = saved; }
    }
}
