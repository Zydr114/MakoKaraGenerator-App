using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>
/// Worker-only contract for validating resources from an already fixed package.
/// The grant is never part of the public HTTP contract; its path points at a
/// private Host staging file and is checked again by the Worker.
/// </summary>
public static class PackageResourceWorkerContract
{
    public const string Profile = "package-resource-closure-v1";
    public const string CapabilitiesKind = "package-resource-capabilities-v1";
    public const string CapabilitiesResultKind = "package-resource-capabilities-result-v1";
    public const string ValidateKind = "package-resource-validate-v1";
    public const string ValidateResultKind = "package-resource-validate-result-v1";
    public const string ImageDecodeProfile = "skia-codec-v1";
    public const string VideoMetadataProfile = "ffmpeg-video-metadata-v1";
    public const string VideoFrameProfile = "ffmpeg-video-frame-bgra-v1";
    public const string FontShapeProfile = "skia-font-shape-v1";
    public const string FontProviderIdentity = "skia-harfbuzz-v1";
    public const int MaximumGrantBytes = 4 * 1024 * 1024;
    public const int MaximumVideoFrameBytes = 16 * 1024 * 1024;
    public const long MaximumVideoDurationUs = 30 * 60 * 1_000_000L;
    public const int MaximumJsonBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    public static byte[] EncodeCapabilitiesRequest() => [];

    public static byte[] EncodeCapabilities(PackageResourceWorkerCapabilities value)
    {
        ValidateCapabilities(value);
        return EncodeJson(value, "Worker资源能力回执超过预算。");
    }

    public static PackageResourceWorkerCapabilities DecodeCapabilities(ReadOnlySpan<byte> bytes)
    {
        PackageResourceWorkerCapabilities value = DecodeJson<PackageResourceWorkerCapabilities>(bytes,
            "Worker资源能力回执不是合法JSON。", "WorkerResourceCapabilitiesInvalid");
        ValidateCapabilities(value);
        return value;
    }

    public static byte[] EncodeGrant(PackageResourceWorkerGrant value)
    {
        ValidateGrant(value);
        return EncodeJson(value, "Worker资源授权超过预算。");
    }

    public static PackageResourceWorkerGrant DecodeGrant(ReadOnlySpan<byte> bytes)
    {
        PackageResourceWorkerGrant value = DecodeJson<PackageResourceWorkerGrant>(bytes,
            "Worker资源授权不是合法JSON。", "WorkerResourceGrantInvalid");
        ValidateGrant(value);
        return value;
    }

    public static byte[] EncodeInspection(PackageResourceInspection value)
    {
        ValidateInspection(value);
        return EncodeJson(value, "Worker资源检查回执超过预算。");
    }

    public static PackageResourceInspection DecodeInspection(ReadOnlySpan<byte> bytes)
    {
        PackageResourceInspection value = DecodeJson<PackageResourceInspection>(bytes,
            "Worker资源检查回执不是合法JSON。", "WorkerResourceInspectionInvalid");
        ValidateInspection(value);
        return value;
    }

    public static void ValidateCapabilities(PackageResourceWorkerCapabilities value)
    {
        if (value is null || value.EngineIdentity is null || value.EngineIdentity.Length == 0
            || value.Profile != Profile || value.ImageProfiles is null || value.VideoProfiles is null
            || value.FontProfiles is null || value.ImageProfiles.Length == 0
            || !value.ImageProfiles.Contains(ImageDecodeProfile, StringComparer.Ordinal)
            || HasDuplicate(value.ImageProfiles) || HasDuplicate(value.VideoProfiles) || HasDuplicate(value.FontProfiles)
            || value.ImageProfiles.Any(string.IsNullOrWhiteSpace)
            || value.VideoProfiles.Any(string.IsNullOrWhiteSpace)
            || value.FontProfiles.Any(string.IsNullOrWhiteSpace)
            || value.FontProviderIdentity is { Length: > 256 }
            || value.FontProfiles.Contains(FontShapeProfile, StringComparer.Ordinal)
                && string.IsNullOrWhiteSpace(value.FontProviderIdentity))
            throw Invalid("Worker资源能力字段无效。", "WorkerResourceCapabilitiesInvalid");
    }

    public static void ValidateGrant(PackageResourceWorkerGrant value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.GrantId) || value.GrantId.Length > 128
            || string.IsNullOrWhiteSpace(value.PackageId) || value.PackageId.Length > 256
            || string.IsNullOrWhiteSpace(value.PackageRevision) || value.PackageRevision.Length > 256
            || string.IsNullOrWhiteSpace(value.PackagePath) || value.PackagePath.Length > 512
            || string.IsNullOrWhiteSpace(value.FilePath) || !Path.IsPathFullyQualified(value.FilePath)
            || value.FilePath.Any(char.IsControl) || value.Role is not ("image" or "video" or "font")
            || value.Size is <= 0 or > MaximumGrantBytes || value.Sha256 is null || value.Sha256.Length != 64
            || value.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Invalid("Worker资源授权字段无效。", "WorkerResourceGrantInvalid");
    }

    public static void ValidateInspection(PackageResourceInspection value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.GrantId) || value.Role is not ("image" or "video" or "font")
            || value.Size is <= 0 or > MaximumGrantBytes || value.Sha256 is null || value.Sha256.Length != 64
            || value.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Invalid("Worker资源检查字段无效。", "WorkerResourceInspectionInvalid");
        int parts = (value.Image is null ? 0 : 1) + (value.Video is null ? 0 : 1) + (value.Font is null ? 0 : 1);
        if (parts != 1) throw Invalid("Worker资源检查结果必须恰有一个类型载荷。", "WorkerResourceInspectionInvalid");
        if (value.Role == "image")
        {
            if (value.Image is null || value.Image.Width <= 0 || value.Image.Height <= 0
                || value.Image.Width > 16_384 || value.Image.Height > 16_384
                || value.Image.DecodedBytes <= 0 || value.Image.DecodedBytes > MaximumGrantBytes
                || string.IsNullOrWhiteSpace(value.Image.EncodedFormat))
                throw Invalid("图片资源检查结果无效。", "WorkerResourceInspectionInvalid");
            if (value.Video is not null || value.Font is not null) throw Invalid("图片资源类型混入其他载荷。", "WorkerResourceInspectionInvalid");
        }
        else if (value.Role == "video")
        {
            if (value.Video is null || value.Video.DurationUs <= 0 || value.Video.DurationUs > MaximumVideoDurationUs
                || value.Video.Width <= 0 || value.Video.Height <= 0
                || value.Video.FrameTimeUs < 0 || value.Video.FrameTimeUs > value.Video.DurationUs
                || value.Video.DecodedFrameBytes <= 0 || value.Video.DecodedFrameBytes > MaximumVideoFrameBytes
                || value.Video.FrameSha256 is null || value.Video.FrameSha256.Length != 64
                || value.Video.FrameSha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                || string.IsNullOrWhiteSpace(value.Video.Codec))
                throw Invalid("视频资源检查结果无效。", "WorkerResourceInspectionInvalid");
            if (value.Image is not null || value.Font is not null) throw Invalid("视频资源类型混入其他载荷。", "WorkerResourceInspectionInvalid");
        }
        else
        {
            if (value.Font is null || value.Font.FamilyNames is null || value.Font.FamilyNames.Length == 0
                || value.Font.FamilyNames.Any(string.IsNullOrWhiteSpace) || value.Font.GlyphCount <= 0
                || string.IsNullOrWhiteSpace(value.Font.ShapingProfile) || !float.IsFinite(value.Font.ShapedAdvancePx)
                || value.Font.ShapedAdvancePx <= 0 || value.Font.ShapingFingerprint is null
                || value.Font.ShapingFingerprint.Length != 64
                || value.Font.ShapingFingerprint.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                throw Invalid("字体资源检查结果无效。", "WorkerResourceInspectionInvalid");
            if (value.Image is not null || value.Video is not null) throw Invalid("字体资源类型混入其他载荷。", "WorkerResourceInspectionInvalid");
        }
    }

    private static bool HasDuplicate(IEnumerable<string> values) => values.Distinct(StringComparer.Ordinal).Count() != values.Count();

    private static byte[] EncodeJson<T>(T value, string message)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length is <= 0 or > MaximumJsonBytes)
            throw Invalid(message, "WorkerResourceProtocolInvalid");
        return bytes;
    }

    private static T DecodeJson<T>(ReadOnlySpan<byte> bytes, string message, string code)
    {
        if (bytes.Length is <= 0 or > MaximumJsonBytes) throw Invalid(message, code);
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateFields(document.RootElement, code);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw Invalid(message, code);
        }
        catch (JsonException) { throw Invalid(message, code); }
    }

    private static void RejectDuplicateFields(JsonElement value, string code)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid("Worker资源JSON字段重复。", code);
                RejectDuplicateFields(property.Value, code);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicateFields(item, code);
    }

    private static WorkerProtocolException Invalid(string message, string code) => new(message, code);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageResourceWorkerCapabilities(
    string EngineIdentity,
    string Profile,
    string[] ImageProfiles,
    string[] VideoProfiles,
    string[] FontProfiles,
    string? FontProviderIdentity = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageResourceWorkerGrant(
    string GrantId,
    string PackageId,
    string PackageRevision,
    string PackagePath,
    string Role,
    string FilePath,
    long Size,
    string Sha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageResourceInspection(
    string GrantId,
    string Role,
    long Size,
    string Sha256,
    PackageImageInspection? Image = null,
    PackageVideoInspection? Video = null,
    PackageFontInspection? Font = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageImageInspection(int Width, int Height, string EncodedFormat, int DecodedBytes, bool HasAlpha);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageVideoInspection(long DurationUs, int Width, int Height, string Codec, bool HasAudio,
    int DecodedFrameBytes = 0, string FrameSha256 = "", long FrameTimeUs = 0);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageFontInspection(string[] FamilyNames, int GlyphCount, string ShapingProfile,
    float ShapedAdvancePx, string ShapingFingerprint);
