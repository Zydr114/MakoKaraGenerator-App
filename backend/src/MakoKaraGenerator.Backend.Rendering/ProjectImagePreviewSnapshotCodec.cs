using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>固定已保存图片的 Worker 预览输入；不读取路径或执行 native 解码。</summary>
public static class ProjectImagePreviewSnapshotCodec
{
    /// <summary>图片背景预览独立能力。</summary>
    public const string Profile = BackendSnapshotProfiles.ProjectImagePreview;
    /// <summary>包括两层 base64 和全部元数据，扣除 open 头后的完整输入预算。</summary>
    public const int MaximumBytes = WorkerProtocol.MaxPayloadBytes - PackageRenderWorkerContract.OpenHeaderBytes;
    /// <summary>固定编码图片的字节上限。</summary>
    public const int MaximumImageBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 64,
    };

    /// <summary>验证保存身份并编码完整图片和基础源；原背景路径不进入封包。</summary>
    public static byte[] Encode(ProjectSnapshot snapshot, ReadOnlySpan<byte> imageBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            if (snapshot.Background is not { HasImage: true, Media: { } media } background)
                throw new SnapshotContractException("ProjectImageSourceUnavailable", "工程没有已保存图片背景。");
            ValidateBackground(background.Kind, background.Fit, background.Playback);
            ValidateImageSize(imageBytes.Length);
            string hash = Convert.ToHexStringLower(SHA256.HashData(imageBytes));
            ValidateIdentity(media, imageBytes.Length, hash);
            if (snapshot.PresetApplications is null || !snapshot.PresetApplications.Items.IsEmpty)
                throw new SnapshotContractException("SnapshotEffectsUnsupported", "图片预览尚不支持固定预设源。");
            string? fingerprint = Fingerprint(snapshot);
            if (!string.Equals(snapshot.EffectInputsFingerprint, fingerprint, StringComparison.Ordinal))
                throw new SnapshotContractException("SnapshotFingerprintMismatch", "快照效果输入指纹与完整源不匹配。");
            ProjectSnapshot basis = snapshot with { Background = BackgroundSource.Color };
            basis = basis with { EffectInputsFingerprint = Fingerprint(basis) };
            string baseProfile = ProjectPreviewSnapshotProfile.Select(basis);
            byte[] baseBytes = EncodeBase(basis, baseProfile);
            Envelope envelope = new(Profile, StaticProjectSnapshotCodec.EngineIdentity, baseProfile, baseBytes,
                new(background.Kind, media.RelativePath, background.Fit, background.Playback,
                    new(media.Hash, media.ByteLength, media.OriginalName)), imageBytes.ToArray(), hash, fingerprint);
            using StaticProjectSnapshotCodec.BoundedSnapshotStream stream = new(MaximumBytes);
            JsonSerializer.Serialize(stream, envelope, Options);
            return stream.ToArray();
        }
        catch (SnapshotContractException) { throw; }
        catch (Exception error) when (IsInvalidSource(error))
        { throw Invalid(); }
    }

    /// <summary>严格核对引擎、互斥基础能力、完整字节身份和背景字段。</summary>
    public static (ProjectSnapshot Snapshot, string LogicalPath, byte[] ImageBytes, string ImageSha256) Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumBytes)
            throw new SnapshotContractException("SnapshotSizeExceeded", "图片预览输入为空或超过字节上限。");
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new() { MaxDepth = 64 });
            int values = 0;
            RejectDuplicateFields(document.RootElement, ref values);
            Envelope envelope = JsonSerializer.Deserialize<Envelope>(bytes.Span, Options) ?? throw Invalid();
            if (envelope.Profile != Profile || envelope.EngineIdentity != StaticProjectSnapshotCodec.EngineIdentity
                || !SupportedBase(envelope.BaseProfile))
                throw new SnapshotContractException("SnapshotVersionMismatch", "图片预览能力或引擎身份不匹配。");
            if (envelope.SnapshotBytes is null || envelope.Background is not { Media: { } identity } background
                || envelope.ImageBytes is null || envelope.ImageSha256 is null)
                throw Invalid();
            ValidateBackground(background.Kind, background.Fit, background.Playback);
            MediaFileRevision media = new(identity.Hash, identity.ByteLength, identity.OriginalName);
            if (background.LogicalPath != media.RelativePath) throw Invalid();
            ValidateImageSize(envelope.ImageBytes.Length);
            MediaFileRevision.ValidateHash(envelope.ImageSha256);
            string hash = Convert.ToHexStringLower(SHA256.HashData(envelope.ImageBytes));
            ValidateIdentity(media, envelope.ImageBytes.Length, hash);
            if (hash != envelope.ImageSha256)
                throw new SnapshotContractException("ProjectImageSourceChanged", "固定图片内容身份校验失败。");
            ProjectSnapshot basis = DecodeBase(envelope.SnapshotBytes, envelope.BaseProfile);
            if (basis.Background != BackgroundSource.Color || !basis.PresetApplications.Items.IsEmpty
                || ProjectPreviewSnapshotProfile.Select(basis) != envelope.BaseProfile)
                throw Invalid();
            ProjectSnapshot snapshot = basis with
            {
                Background = new() { Kind = background.Kind, Path = background.LogicalPath, Media = media,
                    Fit = background.Fit, Playback = background.Playback },
            };
            string? fingerprint = Fingerprint(snapshot);
            if (!string.Equals(fingerprint, envelope.SourceEffectInputsFingerprint, StringComparison.Ordinal))
                throw new SnapshotContractException("SnapshotFingerprintMismatch", "快照效果输入指纹与完整源不匹配。");
            snapshot = snapshot with { EffectInputsFingerprint = fingerprint };
            return (snapshot, background.LogicalPath, envelope.ImageBytes, hash);
        }
        catch (SnapshotContractException) { throw; }
        catch (Exception error) when (IsInvalidSource(error))
        { throw Invalid(); }
    }

    private static bool SupportedBase(string? profile) => profile is BackendSnapshotProfiles.StaticLyricsScene
        or BackendSnapshotProfiles.StaticLyricsSceneAnalysis or BackendSnapshotProfiles.PackageLyricsScene;

    private static byte[] EncodeBase(ProjectSnapshot snapshot, string profile) => profile switch
    {
        BackendSnapshotProfiles.PackageLyricsScene => PackageProjectSnapshotCodec.Encode(snapshot),
        BackendSnapshotProfiles.StaticLyricsScene or BackendSnapshotProfiles.StaticLyricsSceneAnalysis
            => StaticProjectSnapshotCodec.Encode(snapshot, profile),
        _ => throw new SnapshotContractException("SnapshotEffectsUnsupported", "图片预览基础能力不受支持。"),
    };

    private static ProjectSnapshot DecodeBase(ReadOnlyMemory<byte> bytes, string profile)
        => profile == PackageProjectSnapshotCodec.Profile ? PackageProjectSnapshotCodec.Decode(bytes) : StaticProjectSnapshotCodec.Decode(bytes);

    private static void ValidateBackground(BackgroundKind kind, BackgroundFit fit, BackgroundPlaybackMode playback)
    {
        if (kind != BackgroundKind.Image || !Enum.IsDefined(fit) || !Enum.IsDefined(playback)) throw Invalid();
    }

    private static void ValidateImageSize(int length)
    {
        if (length is <= 0 or > MaximumImageBytes)
            throw new SnapshotContractException("SnapshotSizeExceeded", "固定图片超过编码大小预算。");
    }

    private static void ValidateIdentity(MediaFileRevision media, int length, string hash)
    {
        if (media.ByteLength != length || !string.Equals(media.Hash, hash, StringComparison.Ordinal))
            throw new SnapshotContractException("ProjectImageSourceChanged", "固定图片大小或内容身份校验失败。");
    }

    private static string? Fingerprint(ProjectSnapshot snapshot) => ProjectEffectInputs.Fingerprint(new()
    {
        Name = snapshot.Name, SchemaVersion = snapshot.SchemaVersion, Composition = snapshot.Composition,
        Lyrics = snapshot.Lyrics, LyricStyle = snapshot.LyricStyle, LyricStylePresetId = snapshot.LyricStylePresetId,
        EnterAnimation = snapshot.EnterAnimation, LyricPresentation = snapshot.LyricPresentation, Background = snapshot.Background,
        AudioPath = snapshot.AudioPath, AudioMedia = snapshot.AudioMedia, AudioAnalysis = snapshot.AudioAnalysis,
        Effects = snapshot.Effects, PresetApplications = snapshot.PresetApplications,
    });

    private static void RejectDuplicateFields(JsonElement value, ref int values)
    {
        if (++values > StaticProjectSnapshotCodec.MaximumSourceValues)
            throw new SnapshotContractException("SnapshotSizeExceeded", "图片预览输入对象数量超过上限。");
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> fields = new(StringComparer.Ordinal);
            foreach (JsonProperty field in value.EnumerateObject())
            {
                if (!fields.Add(field.Name))
                    throw new SnapshotContractException("SnapshotDuplicateField", "图片预览输入不能含重复字段。");
                RejectDuplicateFields(field.Value, ref values);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicateFields(item, ref values);
    }

    private static bool IsInvalidSource(Exception error) => error is JsonException or ArgumentException or InvalidOperationException
        or NullReferenceException or KeyNotFoundException or OverflowException or NotSupportedException or EffectPackageException;
    private static SnapshotContractException Invalid() => new("SnapshotInvalid", "图片预览封包字段或源结构无效。");

    private sealed record Envelope(string Profile, string EngineIdentity, string BaseProfile, byte[] SnapshotBytes,
        BackgroundEnvelope Background, byte[] ImageBytes, string ImageSha256, string? SourceEffectInputsFingerprint);
    private sealed record BackgroundEnvelope(BackgroundKind Kind, string LogicalPath, BackgroundFit Fit,
        BackgroundPlaybackMode Playback, MediaEnvelope Media);
    private sealed record MediaEnvelope(string Hash, long ByteLength, string OriginalName);
}
