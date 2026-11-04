using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>固定效果包 Runtime 会话的私有 Worker 合同；不加入 HTTP 或静态公开能力列表。</summary>
public static class PackageRenderWorkerContract
{
    private static readonly JsonSerializerOptions CapabilitiesJson = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    /// <summary>能力探测请求。</summary>
    public const string CapabilitiesKind = "package-render-capabilities-v1";
    /// <summary>能力探测回执。</summary>
    public const string CapabilitiesResultKind = "package-render-capabilities-result-v1";
    /// <summary>验证任务专用能力探测请求；与 Runtime 会话能力分开协商。</summary>
    public const string ValidationCapabilitiesKind = "package-validation-capabilities-v1";
    /// <summary>验证任务专用能力探测回执。</summary>
    public const string ValidationCapabilitiesResultKind = "package-validation-capabilities-result-v1";
    public const string ValidationProfile = "package-lyrics-scene-v1";
    /// <summary>固定包快照 open 请求。</summary>
    public const string OpenKind = "package-render-session-open-v1";
    /// <summary>固定包帧请求。</summary>
    public const string RenderKind = "package-render-session-render-v1";
    /// <summary>固定包会话 close 请求。</summary>
    public const string CloseKind = "package-render-session-close-v1";
    /// <summary>open/close 回执。</summary>
    public const string SessionResultKind = "package-render-session-result-v1";
    /// <summary>实际包帧回执。</summary>
    public const string FrameResultKind = "package-render-session-frame-v1";
    /// <summary>与静态会话相同的二进制快照头部大小。</summary>
    public const int OpenHeaderBytes = StaticRenderWorkerContract.OpenHeaderBytes;
    /// <summary>扣除会话头部后的 Worker payload 快照上限。</summary>
    public const int MaximumSnapshotBytes = StaticRenderWorkerContract.MaximumSnapshotBytes;

    /// <summary>能力请求没有业务输入。</summary>
    public static byte[] EncodeCapabilitiesRequest() => [];
    /// <summary>编码私有能力回执。</summary>
    public static byte[] EncodeCapabilities(PackageRenderWorkerCapabilities value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateCapabilities(value);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, CapabilitiesJson);
        if (bytes.Length > 8192) throw new WorkerProtocolException("包 Worker 能力回执超过预算。", "WorkerPackageCapabilitiesInvalid");
        return bytes;
    }
    /// <summary>严格读取私有能力回执。</summary>
    public static PackageRenderWorkerCapabilities DecodeCapabilities(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > 8192) throw new WorkerProtocolException("包 Worker 能力回执长度无效。", "WorkerPackageCapabilitiesInvalid");
        try
        {
            using (JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 }))
                RejectDuplicateFields(document.RootElement);
            PackageRenderWorkerCapabilities value = JsonSerializer.Deserialize<PackageRenderWorkerCapabilities>(bytes, CapabilitiesJson)
                ?? throw new WorkerProtocolException("包 Worker 能力回执为空。", "WorkerPackageCapabilitiesInvalid");
            ValidateCapabilities(value); return value;
        }
        catch (JsonException) { throw new WorkerProtocolException("包 Worker 能力回执不是合法 JSON。", "WorkerPackageCapabilitiesInvalid"); }
    }
    private static void RejectDuplicateFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new WorkerProtocolException("包 Worker 能力字段重复。", "WorkerPackageCapabilitiesInvalid"); RejectDuplicateFields(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicateFields(item);
    }

    /// <summary>固定 capability 字段，不从产品版本推断。</summary>
    public static void ValidateCapabilities(PackageRenderWorkerCapabilities value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.EngineIdentity) || value.CodecProfile != "package-lyrics-scene-v1"
            || value.PackageDiagnosticsVersion != WorkerPackageDiagnosticsContract.Version || value.PackageVersion <= 0 || string.IsNullOrWhiteSpace(value.SdkVersion) || string.IsNullOrWhiteSpace(value.ShaderProfile)
            || string.IsNullOrWhiteSpace(value.ScriptRefsIdentity) || value.ScriptRefsIdentity.Length != 64
            || value.ScriptRefsIdentity.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) || value.ScriptRefsCount <= 0)
            throw new WorkerProtocolException("包 Worker 能力字段不完整或无效。", "WorkerPackageCapabilitiesInvalid");
        if (value.ValidationProfiles is not null
            && (value.ValidationProfiles.Length == 0 || value.ValidationProfiles.Any(profile => profile != ValidationProfile)
                || value.ValidationProfiles.Distinct(StringComparer.Ordinal).Count() != value.ValidationProfiles.Length))
            throw new WorkerProtocolException("包 Worker 验证能力 profile 无效。", "WorkerPackageCapabilitiesInvalid");
        if (value.RuntimeInputProfile is not null && value.RuntimeInputProfile != ProjectRuntimeInputWorkerContract.Profile)
            throw new WorkerProtocolException("统一工程输入能力 profile 无效。", "WorkerPackageCapabilitiesInvalid");
        if (value.SourceProfiles is not null && (value.SourceProfiles.Length == 0
            || value.SourceProfiles.Any(profile => profile is not (BackendSnapshotProfiles.PackageLyricsScene or BackendSnapshotProfiles.PackageLyricsScenePresets))
            || value.SourceProfiles.Distinct(StringComparer.Ordinal).Count() != value.SourceProfiles.Length))
            throw new WorkerProtocolException("包 Worker 来源能力 profile 无效。", "WorkerPackageCapabilitiesInvalid");
        if (value.SupportedPackageProfiles is { } profiles && (profiles.Length is 0 or > 16
            || profiles.Any(profile => profile is null || profile.PackageVersion <= 0
                || string.IsNullOrWhiteSpace(profile.SdkVersion) || string.IsNullOrWhiteSpace(profile.ShaderProfile))
            || profiles.Distinct().Count() != profiles.Length))
            throw new WorkerProtocolException("包 Worker 支持的包身份列表无效或重复。", "WorkerPackageCapabilitiesInvalid");
    }
    /// <summary>复用静态会话的受验证 open 二进制形状。</summary>
    public static byte[] EncodeOpen(Guid sessionId, ReadOnlySpan<byte> snapshotBytes) => StaticRenderWorkerContract.EncodeOpen(sessionId, snapshotBytes);
    /// <summary>读取 package open 二进制形状。</summary>
    public static (Guid SessionId, byte[] SnapshotBytes, string SourceSha256) DecodeOpen(ReadOnlySpan<byte> bytes) => StaticRenderWorkerContract.DecodeOpen(bytes);
    /// <summary>复用固定帧输入二进制形状。</summary>
    public static byte[] EncodeFrame(StaticWorkerFrameRequest request) => StaticRenderWorkerContract.EncodeFrame(request);
    /// <summary>读取固定帧输入二进制形状。</summary>
    public static StaticWorkerFrameRequest DecodeFrame(ReadOnlySpan<byte> bytes) => StaticRenderWorkerContract.DecodeFrame(bytes);
    /// <summary>复用固定关闭二进制形状。</summary>
    public static byte[] EncodeClose(Guid sessionId, string sourceSha256, long sequence) => StaticRenderWorkerContract.EncodeClose(sessionId, sourceSha256, sequence);
    /// <summary>读取固定关闭二进制形状。</summary>
    public static (Guid SessionId, string SourceSha256, long Sequence) DecodeClose(ReadOnlySpan<byte> bytes) => StaticRenderWorkerContract.DecodeClose(bytes);
    /// <summary>验证 package 帧输入。</summary>
    public static void ValidateFrameRequest(StaticWorkerFrameRequest request) => StaticRenderWorkerContract.ValidateFrameRequest(request);
    /// <summary>验证 package 帧回执的完整相关字段。</summary>
    public static void ValidateFrameResult(StaticWorkerFrameRequest request, WorkerMessage message)
    {
        if (message.Kind != FrameResultKind)
            throw new WorkerProtocolException("包 Worker 帧回执 kind 不匹配。", "WorkerPackageProtocolInvalid");
        if (message.PackageDiagnostics is null)
            throw new WorkerProtocolException("包帧必须包含完整图像／材质诊断。", "WorkerPackageDiagnosticsInvalid");
        WorkerPackageDiagnosticsContract.Validate(message.PackageDiagnostics);
        if (message.PackageDiagnostics.Source.Length != 0)
            throw new WorkerProtocolException("包帧不能包含编译失败。", "WorkerPackageDiagnosticsInvalid");
        if (message.ErrorCode is not null || message.TaskId is not null || message.ProgressSequence is not null
            || message.ValidationProgress is not null || message.ExportFormat is not null || message.ExportIncludeAudio is not null
            || message.ExportProgress is not null || message.ProcessGroupId is not null || message.MediaFileSizeLimitBytes is not null
            || message.ExportDiagnostics is not null || message.ExportDiagnosticsFailureCode is not null || message.AnalysisProgress is not null
            || message.ValidationFramesPerOutput is not null || message.DelayMilliseconds != 0)
            throw new WorkerProtocolException("包 Worker 帧回执含未协商字段。", "WorkerPackageProtocolInvalid");
        try
        {
            StaticRenderWorkerContract.ValidateFrameResult(request, message with { Kind = StaticRenderWorkerContract.FrameResultKind, PackageDiagnostics = null });
        }
        catch (WorkerProtocolException error) when (error.Code == "WorkerRuntimeDiagnosticError")
        {
            throw new WorkerProtocolException(error.Message, error.Code, error.Diagnostics, message.PackageDiagnostics);
        }
    }
}

/// <summary>包 Worker 的实际能力证明；refs identity 来自编译器实际引用集合。</summary>
public sealed record PackageRenderWorkerCapabilities(string EngineIdentity, string CodecProfile, int PackageVersion,
    string SdkVersion, string ShaderProfile, string ScriptRefsIdentity, int ScriptRefsCount,
    [property: JsonRequired] int PackageDiagnosticsVersion = WorkerPackageDiagnosticsContract.Version,
    string[]? ValidationProfiles = null, string[]? SourceProfiles = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RuntimeInputProfile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkerPackageProfileDto[]? SupportedPackageProfiles = null);

/// <summary>实际 Worker 明确支持的一条包／SDK／shader 身份；私有 IPC 使用，不推断 HTTP 能力。</summary>
/// <param name="PackageVersion">包格式。</param><param name="SdkVersion">已部署 SDK 合同。</param><param name="ShaderProfile">精确包后端合同。</param>
public sealed record WorkerPackageProfileDto(int PackageVersion, string SdkVersion, string ShaderProfile);
