using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>已保存工程图片预览的独立私有能力；帧和关闭复用固定包会话合同。</summary>
public static class ProjectImagePreviewWorkerContract
{
    public const string CapabilitiesKind = "project-image-preview-capabilities-v1";
    public const string CapabilitiesResultKind = "project-image-preview-capabilities-result-v1";
    public const string OpenKind = "project-image-preview-session-open-v1";
    public const string ImageDecoderProfile = "memory-image-bgra-v1";
    public const int MaximumImageBytes = 2 * 1024 * 1024;
    public const int MaximumDimension = 4096;
    public const long MaximumDecodedBytes = 16 * 1024 * 1024;
    public const int MaximumBaseSnapshotBytes = 4 * 1024 * 1024;
    public const int MaximumSourceValues = 200_000;
    public const int MaximumSnapshotBytes = WorkerProtocol.MaxPayloadBytes - PackageRenderWorkerContract.OpenHeaderBytes;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };

    public static byte[] EncodeCapabilitiesRequest() => [];

    public static byte[] EncodeCapabilities(ProjectImagePreviewWorkerCapabilities value)
    {
        ValidateCapabilities(value);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > 8192) throw Invalid();
        return bytes;
    }

    public static ProjectImagePreviewWorkerCapabilities DecodeCapabilities(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > 8192) throw Invalid();
        try
        {
            using (JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 }))
                RejectDuplicates(document.RootElement);
            var value = JsonSerializer.Deserialize<ProjectImagePreviewWorkerCapabilities>(bytes, Options) ?? throw Invalid();
            ValidateCapabilities(value);
            return value;
        }
        catch (JsonException) { throw Invalid(); }
    }

    public static void ValidateCapabilities(ProjectImagePreviewWorkerCapabilities value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.EngineIdentity)
            || value.CodecProfile != BackendSnapshotProfiles.ProjectImagePreview || value.ImageDecoderProfile != ImageDecoderProfile
            || value.MaximumImageBytes != MaximumImageBytes || value.MaximumDimension != MaximumDimension
            || value.MaximumDecodedBytes != MaximumDecodedBytes || value.MaximumSnapshotBytes != MaximumSnapshotBytes
            || value.MaximumBaseSnapshotBytes != MaximumBaseSnapshotBytes || value.MaximumSourceValues != MaximumSourceValues
            || value.PackageDiagnosticsVersion != WorkerPackageDiagnosticsContract.Version
            || value.BaseProfiles is null || value.BaseProfiles.Length != 3
            || !value.BaseProfiles.ToHashSet(StringComparer.Ordinal).SetEquals([
                BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis,
                BackendSnapshotProfiles.PackageLyricsScene]) || value.PackageCapabilities is null)
            throw Invalid();
        PackageRenderWorkerContract.ValidateCapabilities(value.PackageCapabilities);
        if (value.PackageCapabilities.EngineIdentity != value.EngineIdentity) throw Invalid();
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }

    private static WorkerProtocolException Invalid() => new("图片预览 Worker 能力与固定合同不匹配。", "WorkerImagePreviewCapabilitiesInvalid");
}

/// <summary>图片解码预算、基础源与完整诊断的实际 Worker 能力证明。</summary>
public sealed record ProjectImagePreviewWorkerCapabilities(
    [property: JsonRequired] string EngineIdentity,
    [property: JsonRequired] string CodecProfile,
    [property: JsonRequired] string[] BaseProfiles,
    [property: JsonRequired] string ImageDecoderProfile,
    [property: JsonRequired] int MaximumImageBytes,
    [property: JsonRequired] int MaximumDimension,
    [property: JsonRequired] long MaximumDecodedBytes,
    [property: JsonRequired] int MaximumSnapshotBytes,
    [property: JsonRequired] int MaximumBaseSnapshotBytes,
    [property: JsonRequired] int MaximumSourceValues,
    [property: JsonRequired] int PackageDiagnosticsVersion,
    [property: JsonRequired] PackageRenderWorkerCapabilities PackageCapabilities);
