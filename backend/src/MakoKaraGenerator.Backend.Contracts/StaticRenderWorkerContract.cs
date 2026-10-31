using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>私有静态工程 Worker 会话合同；快照能力由 snapshot-v1 codec 另行校验。</summary>
public static class StaticRenderWorkerContract
{
    /// <summary>查询当前 Worker 实际编译的静态 snapshot 来源能力。</summary>
    public const string CapabilitiesKind = "static-session-capabilities-v1";
    /// <summary>静态来源能力查询回执。</summary>
    public const string CapabilitiesResultKind = "static-session-capabilities-result-v1";
    /// <summary>固定快照并创建一次 Runtime 会话。</summary>
    public const string OpenKind = "static-session-open-v1";
    /// <summary>沿用固定会话渲染一个时刻。</summary>
    public const string RenderKind = "static-session-render-v1";
    /// <summary>排水并关闭固定会话。</summary>
    public const string CloseKind = "static-session-close-v1";
    /// <summary>打开／关闭回执，与旧 opaque result 区分。</summary>
    public const string SessionResultKind = "static-session-result-v1";
    /// <summary>带完整诊断快照的真实像素回执。</summary>
    public const string FrameResultKind = "static-session-frame-v1";
    /// <summary>open 的固定前缀长度：magic/version/保留零与会话 Guid。</summary>
    public const int OpenHeaderBytes = 24;
    /// <summary>快照净预算，明确扣除 open 前缀。</summary>
    public const int MaximumSnapshotBytes = WorkerProtocol.MaxPayloadBytes - OpenHeaderBytes;
    /// <summary>render 的固定前缀长度，包含会话、源 SHA、尺寸、时间和序号。</summary>
    public const int FrameRequestBytes = 80;
    /// <summary>close 的固定前缀长度。</summary>
    public const int CloseRequestBytes = 64;
    private const uint OpenMagic = 0x314F534B; // KSO1
    private const uint FrameMagic = 0x3146534B; // KSF1
    private const uint CloseMagic = 0x3143534B; // KSC1

    private static readonly JsonSerializerOptions CapabilitiesOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>能力查询使用固定非空输入，旧 Worker 会以未知 kind 明确拒绝。</summary>
    public static byte[] EncodeCapabilitiesRequest() => "KSCAP1"u8.ToArray();

    public static byte[] EncodeCapabilitiesResult(StaticWorkerCapabilitiesDto value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateCapabilities(value);
        return JsonSerializer.SerializeToUtf8Bytes(value, CapabilitiesOptions);
    }

    public static StaticWorkerCapabilitiesDto DecodeCapabilities(ReadOnlySpan<byte> bytes)
    {
        StaticWorkerCapabilitiesDto value;
        try
        {
            value = JsonSerializer.Deserialize<StaticWorkerCapabilitiesDto>(bytes, CapabilitiesOptions)
                ?? throw new WorkerProtocolException("静态 Worker 能力回执为空。", "WorkerStaticProfileMismatch");
        }
        catch (JsonException)
        { throw new WorkerProtocolException("静态 Worker 能力回执无效。", "WorkerStaticProfileMismatch"); }
        ValidateCapabilities(value);
        return value;
    }

    private static void ValidateCapabilities(StaticWorkerCapabilitiesDto value)
    {
        if (value.SourceProfiles is not { Length: > 0 }
            || value.SourceProfiles.Distinct(StringComparer.Ordinal).Count() != value.SourceProfiles.Length
            || !value.SourceProfiles.All(BackendSnapshotProfiles.IsSupported)
            || !value.SourceProfiles.Contains(BackendSnapshotProfiles.StaticLyricsScene, StringComparer.Ordinal))
            throw new WorkerProtocolException("静态 Worker 来源能力不完整。", "WorkerStaticProfileMismatch");
    }

    /// <summary>编码 open 输入；不复用工程文件格式，复制快照字节到受限封包。</summary>
    public static byte[] EncodeOpen(Guid sessionId, ReadOnlySpan<byte> snapshotBytes)
    {
        ValidateSession(sessionId);
        if (snapshotBytes.Length is <= 0 or > MaximumSnapshotBytes)
            throw new WorkerProtocolException("静态会话快照超过扣除头部后的输入预算。", "WorkerSnapshotBudgetExceeded");
        byte[] bytes = Header(OpenMagic, OpenHeaderBytes + snapshotBytes.Length, sessionId);
        snapshotBytes.CopyTo(bytes.AsSpan(OpenHeaderBytes));
        return bytes;
    }

    /// <summary>验证 open 头部并复制独立快照输入。</summary>
    public static (Guid SessionId, byte[] SnapshotBytes, string SourceSha256) DecodeOpen(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= OpenHeaderBytes || bytes.Length > WorkerProtocol.MaxPayloadBytes)
            throw new WorkerProtocolException("静态会话 open 长度不符合预算。", "WorkerSnapshotBudgetExceeded");
        Guid sessionId = ReadHeader(bytes, OpenMagic, false);
        byte[] snapshot = bytes[OpenHeaderBytes..].ToArray();
        return (sessionId, snapshot, WorkerProtocol.ComputeSha256(snapshot));
    }

    /// <summary>编码只含固定会话／源 SHA、目标、时间、透明模式与单调序号的帧输入。</summary>
    public static byte[] EncodeFrame(StaticWorkerFrameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateFrameRequest(request);
        byte[] bytes = Header(FrameMagic, FrameRequestBytes, request.SessionId);
        bytes[5] = request.TransparentBackground ? (byte)1 : (byte)0;
        bytes[6] = (byte)request.Quality;
        WriteHash(bytes.AsSpan(24, 32), request.SourceSha256);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56), request.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), request.Height);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(64), request.TimeUs);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(72), request.Sequence);
        return bytes;
    }

    /// <summary>严格解码帧输入，不允许可变工程源或额外尾部。</summary>
    public static StaticWorkerFrameRequest DecodeFrame(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != FrameRequestBytes) throw new WorkerProtocolException("静态会话帧输入长度错误。");
        var request = new StaticWorkerFrameRequest(ReadHeader(bytes, FrameMagic, true), Hash(bytes.Slice(24, 32)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[56..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[60..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[64..]), bytes[5] == 1, BinaryPrimitives.ReadInt64LittleEndian(bytes[72..]), bytes[6]);
        ValidateFrameRequest(request);
        return request;
    }

    /// <summary>编码固定会话的关闭输入。</summary>
    public static byte[] EncodeClose(Guid sessionId, string sourceSha256, long sequence)
    {
        if (sequence <= 0) throw new WorkerProtocolException("静态会话关闭序号必须为正。");
        byte[] bytes = Header(CloseMagic, CloseRequestBytes, sessionId);
        WriteHash(bytes.AsSpan(24, 32), sourceSha256);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(56), sequence);
        return bytes;
    }

    /// <summary>严格解码关闭输入。</summary>
    public static (Guid SessionId, string SourceSha256, long Sequence) DecodeClose(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != CloseRequestBytes) throw new WorkerProtocolException("静态会话 close 长度错误。");
        Guid id = ReadHeader(bytes, CloseMagic, false);
        long sequence = BinaryPrimitives.ReadInt64LittleEndian(bytes[56..]);
        if (sequence <= 0) throw new WorkerProtocolException("静态会话关闭序号必须为正。");
        return (id, Hash(bytes.Slice(24, 32)), sequence);
    }

    /// <summary>在分配像素或进入 Runtime 前验证固定帧预算。</summary>
    public static void ValidateFrameRequest(StaticWorkerFrameRequest request)
    {
        ValidateSession(request.SessionId);
        ValidateHash(request.SourceSha256);
        if (request.Width <= 0 || request.Height <= 0 || request.Width > RenderFrameV1WorkerRequest.MaxWidth
            || request.Height > RenderFrameV1WorkerRequest.MaxHeight
            || (long)request.Width * request.Height * 4 > WorkerProtocol.MaxPayloadBytes)
            throw new WorkerProtocolException("静态会话输出超过尺寸或 4 MiB 像素预算。", "WorkerFrameBudgetExceeded");
        if (request.TimeUs < 0 || request.Sequence <= 0 || request.Quality is < 0 or > 2)
            throw new WorkerProtocolException("静态会话时间或帧序号非法。");
    }

    /// <summary>交付像素前核对完整会话身份、请求元数据、输出 SHA 和实际诊断。</summary>
    public static void ValidateFrameResult(StaticWorkerFrameRequest request, WorkerMessage message)
    {
        ValidateFrameRequest(request);
        WorkerProtocol.Validate(message);
        if (message.Kind != FrameResultKind || message.Cancelled || message.Error is not null
            || message.InputSha256 != WorkerProtocol.ComputeSha256(EncodeFrame(request))
            || message.SessionId != request.SessionId.ToString("N") || message.SourceSha256 != request.SourceSha256
            || message.FrameSequence != request.Sequence || message.FrameTransparentBackground != request.TransparentBackground
            || message.FrameQuality != request.Quality
            || message.FrameWidth != request.Width || message.FrameHeight != request.Height
            || message.FrameStride != request.Width * 4 || message.FrameTimeUs != request.TimeUs
            || message.FrameFormat != RenderFrameV1WorkerResult.RequiredPixelFormat
            || message.Payload.Length != request.Width * request.Height * 4 || message.OutputSha256 is null
            || message.Diagnostics is null)
            throw new WorkerProtocolException("静态会话帧回执不属于固定输入或缺少真实诊断。");
        WorkerDiagnosticsSnapshotDto.Validate(message.Diagnostics);
        if (message.Diagnostics.HasError)
            throw new WorkerProtocolException("Runtime Error 诊断禁止成功像素帧。", "WorkerRuntimeDiagnosticError", message.Diagnostics);
    }

    private static byte[] Header(uint magic, int length, Guid sessionId)
    {
        ValidateSession(sessionId);
        byte[] bytes = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, magic); bytes[4] = 1;
        sessionId.TryWriteBytes(bytes.AsSpan(8, 16));
        return bytes;
    }
    private static Guid ReadHeader(ReadOnlySpan<byte> bytes, uint magic, bool flags)
    {
        if (bytes.Length < OpenHeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != magic || bytes[4] != 1
            || bytes[5] > (flags ? 1 : 0) || bytes[6] > (flags ? 2 : 0) || bytes[7] != 0)
            throw new WorkerProtocolException("静态会话 magic/version/保留字段错误。");
        Guid id = new(bytes.Slice(8, 16)); ValidateSession(id); return id;
    }
    private static void ValidateSession(Guid id)
    { if (id == Guid.Empty) throw new WorkerProtocolException("静态会话身份不能为空。"); }
    private static string Hash(ReadOnlySpan<byte> hash) => Convert.ToHexString(hash).ToLowerInvariant();
    private static void WriteHash(Span<byte> destination, string hash)
    { ValidateHash(hash); Convert.FromHexString(hash).CopyTo(destination); }
    private static void ValidateHash(string hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(character => !char.IsAsciiHexDigit(character))
            || hash.Any(character => character is >= 'A' and <= 'F'))
            throw new WorkerProtocolException("静态会话源 SHA 必须为完整小写 SHA-256。");
    }
}

/// <summary>Worker 运行时实际编译并允许打开的静态 snapshot 来源。</summary>
public sealed record StaticWorkerCapabilitiesDto(string[] SourceProfiles);

/// <summary>绑定固定会话与快照 SHA 的有界帧输入。</summary>
public sealed record StaticWorkerFrameRequest(Guid SessionId, string SourceSha256, int Width, int Height,
    long TimeUs, bool TransparentBackground, long Sequence, int Quality = 1);

/// <summary>完整实际 Runtime 发生快照；超预算明确拒绝，不截断或重新分配发生身份。</summary>
public sealed record WorkerDiagnosticsSnapshotDto(WorkerRuntimeDiagnosticOccurrenceDto[] Occurrences, bool HasError)
{
    private static readonly SearchValues<char> LowerHexCharacters = SearchValues.Create("0123456789abcdef");
    /// <summary>本静态能力接受的完整发生次数上限。</summary>
    public const int MaximumOccurrences = 256;
    /// <summary>诊断快照独立 JSON 字节上限。</summary>
    public const int MaximumJsonBytes = 64 * 1024;

    /// <summary>校验完整代次／序号和预算，空快照也必须显式携带数组。</summary>
    public static void Validate(WorkerDiagnosticsSnapshotDto snapshot)
    {
        if (snapshot?.Occurrences is null)
            throw new WorkerProtocolException("Worker 诊断快照缺少真实发生数组。");
        if (snapshot.Occurrences.Length > MaximumOccurrences)
            throw new WorkerProtocolException("Worker 诊断发生次数超过静态能力预算。", "WorkerDiagnosticsBudgetExceeded");
        HashSet<Guid> epochs = [];
        Guid epoch = Guid.Empty; long sequence = 0; bool hasError = false;
        foreach (var occurrence in snapshot.Occurrences)
        {
            if (occurrence is null || occurrence.Epoch == Guid.Empty || occurrence.Diagnostic is null)
                throw new WorkerProtocolException("Worker 诊断发生缺少代次或报告。");
            if (occurrence.Epoch != epoch)
            {
                if (!epochs.Add(occurrence.Epoch)) throw new WorkerProtocolException("Worker 诊断代次重复。");
                epoch = occurrence.Epoch; sequence = 0;
            }
            if (occurrence.Sequence != ++sequence) throw new WorkerProtocolException("Worker 诊断序号不连续递增。");
            WorkerRuntimeDiagnosticDto diagnostic = occurrence.Diagnostic;
            if (string.IsNullOrWhiteSpace(diagnostic.InstanceId) || diagnostic.ArtifactHash is null
                || string.IsNullOrWhiteSpace(diagnostic.Stage) || diagnostic.TimeUs < 0
                || diagnostic.Severity is not ("Info" or "Warning" or "Error")
                || string.IsNullOrWhiteSpace(diagnostic.Code) || string.IsNullOrWhiteSpace(diagnostic.Message)
                || diagnostic.Location is { } location && (string.IsNullOrWhiteSpace(location.Path) || location.Line < 1 || location.Column < 1)
                || diagnostic.Package is { } package && (string.IsNullOrWhiteSpace(package.PackageId)
                    || package.Revision is null || package.Revision.Length != 71 || !package.Revision.StartsWith("sha256:", StringComparison.Ordinal)
                    || package.Revision.AsSpan(7).ContainsAnyExcept(LowerHexCharacters) || string.IsNullOrWhiteSpace(package.ExportId)))
                throw new WorkerProtocolException("Worker 诊断来源或内容字段无效。");
            hasError |= diagnostic.Severity == "Error";
        }
        if (hasError != snapshot.HasError) throw new WorkerProtocolException("Worker 诊断 Error 标记与完整报告不一致。");
        if (JsonSerializer.SerializeToUtf8Bytes(snapshot, WorkerProtocol.JsonOptions).Length > MaximumJsonBytes)
            throw new WorkerProtocolException("Worker 诊断字节超过静态能力预算。", "WorkerDiagnosticsBudgetExceeded");
    }
}

/// <summary>真实日志发生身份及完整 Runtime 报告。</summary>
public sealed record WorkerRuntimeDiagnosticOccurrenceDto(Guid Epoch, long Sequence, WorkerRuntimeDiagnosticDto Diagnostic);
/// <summary>无 SDK 依赖的实际 Runtime 诊断字段。</summary>
public sealed record WorkerRuntimeDiagnosticDto(string InstanceId, string ArtifactHash, string Stage, long TimeUs,
    string Severity, string Code, string Message, WorkerDiagnosticLocationDto? Location, WorkerDiagnosticPackageDto? Package);
/// <summary>包内逻辑源码位置。</summary>
public sealed record WorkerDiagnosticLocationDto(string Path, int Line, int Column);
/// <summary>实际固定包／内容修订／导出身份。</summary>
public sealed record WorkerDiagnosticPackageDto(string PackageId, string Revision, string ExportId);
