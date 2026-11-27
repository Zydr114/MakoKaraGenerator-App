using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>Host 私有固定媒体授权；不是公共文件路径请求。</summary>
public sealed record ProjectRuntimeBackgroundGrant(BackgroundKind Kind, string LogicalPath, string FilePath,
    string Sha256, long ByteLength, string OriginalName, BackgroundFit Fit, BackgroundPlaybackMode Playback);

/// <summary>Host私有完整包目录授权；内容地址仍由原包校验器确认。</summary>
public sealed record ProjectRuntimePackageGrant(string PackageId, string Revision, string DirectoryPath);

/// <summary>固定领域源和 Host 私有媒体授权共用的二进制输入；不执行 IO。</summary>
public static class ProjectRuntimeInputCodec
{
    /// <summary>完整固定运行输入能力。</summary>
    public const string Profile = "runtime-project-input-v1";
    /// <summary>扣除会话头部后的输入预算。</summary>
    public const int MaximumBytes = PackageRenderWorkerContract.MaximumSnapshotBytes;
    private static ReadOnlySpan<byte> Magic => "KRI1"u8;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false), new CoreMediaValueJsonConverterFactory() },
    };

    /// <summary>检查二进制固定运行输入标记。</summary>
    public static bool IsRuntimeInput(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Magic);

    /// <summary>媒体与固定包二进制资源须经同一私有运行输入准备。</summary>
    public static bool RequiresPreparation(ProjectSnapshot snapshot) => snapshot.MediaFeatureInput is not null || snapshot.Background.Kind != BackgroundKind.Color
        || RequiresPackagePreparation(snapshot);

    /// <summary>固定包图片、视频与字体沿原包闭包授权，避免将资源base64挤入消息预算。</summary>
    public static bool RequiresPackagePreparation(ProjectSnapshot snapshot)
        => snapshot.Effects.Packages.Any(package => package.Files.Any(file => file.Role is "font" or "image" or "video"));

    /// <summary>
    /// 在不执行任何磁盘复制或媒体特征等待的前提下，计算固定运行时工程源的确定性 SHA-256 身份并核验基础快照预算。
    /// 供导出／正式验证在异步准备前登记任务唯一来源身份使用。
    /// </summary>
    public static string ComputeFixedSourceSha256(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.EffectInputsFingerprint != Fingerprint(snapshot)) throw FingerprintInvalid();
        var basis = snapshot with { Background = BackgroundSource.Color, MediaFeatureInput = null };
        basis = basis with { EffectInputsFingerprint = Fingerprint(basis) };
        string baseProfile = ProjectSnapshotSourceProfile.Select(basis);
        bool externalPackages = RequiresPackagePreparation(snapshot);
        byte[] source = externalPackages
            ? StaticProjectSnapshotCodec.EncodeCore(basis, baseProfile, true, !basis.PresetApplications.Items.IsEmpty, externalPackages: true)
            : ProjectSnapshotSourceProfile.Encode(basis);
        if (source.Length + 8 > MaximumBytes) throw Size();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("makokara-runtime-source-v1\0"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(StaticProjectSnapshotCodec.EngineIdentity));
        hash.AppendData("\0"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(baseProfile));
        hash.AppendData("\0"u8);
        hash.AppendData(source);
        if (snapshot.Background.Kind != BackgroundKind.Color)
        {
            if (snapshot.Background.Media is not { } media) throw Invalid();
            hash.AppendData(Encoding.UTF8.GetBytes(
                $"\0bg:{snapshot.Background.Kind}:{snapshot.Background.Fit}:{snapshot.Background.Playback}:{media.Hash}:{media.ByteLength}:{media.OriginalName}:{media.RelativePath}"));
        }
        if (snapshot.EffectInputsFingerprint is { } fingerprint)
            hash.AppendData(Encoding.UTF8.GetBytes("\0fp:" + fingerprint));
        foreach (var package in snapshot.Effects.Packages)
            hash.AppendData(Encoding.UTF8.GetBytes($"\0pkg:{package.PackageId}:{package.Revision}"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>媒体字节通过固定 grant 读取，领域 snapshot 原字节直接内嵌，避免 base64 放大。</summary>
    /// <param name="snapshot">完整固定源；其媒体特征输入随私有授权传输。</param>
    /// <param name="grant">固定背景媒体授权。</param>
    /// <param name="packageGrants">固定包目录授权。</param>
    /// <param name="mediaArtifactPath">AvailableFile 媒体输入的私有只读特征轨文件。</param>
    public static byte[] Encode(ProjectSnapshot snapshot, ProjectRuntimeBackgroundGrant? grant, ProjectRuntimePackageGrant[]? packageGrants = null,
        string? mediaArtifactPath = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        packageGrants ??= [];
        ValidatePackages(packageGrants);
        ProjectRuntimeMediaGrant? mediaGrant;
        try { mediaGrant = snapshot.MediaFeatureInput is { } input ? ProjectRuntimeMediaGrant.From(input, mediaArtifactPath) : null; }
        catch (Exception error) when (error is MediaAbException or ArgumentException) { throw Invalid(); }
        if (mediaGrant is null && mediaArtifactPath is not null) throw Invalid();
        if (grant is null && packageGrants.Length == 0 && mediaGrant is null)
        {
            if (snapshot.Background != BackgroundSource.Color) throw Invalid();
            return ProjectSnapshotSourceProfile.Encode(snapshot);
        }
        if (grant is not null)
        {
            ValidateGrant(grant);
            if (snapshot.Background is not { Media: { } media } background || background.Kind != grant.Kind
                || background.Fit != grant.Fit || background.Playback != grant.Playback || media.Hash != grant.Sha256
                || media.ByteLength != grant.ByteLength || media.OriginalName != grant.OriginalName || media.RelativePath != grant.LogicalPath) throw Invalid();
        }
        else if (snapshot.Background != BackgroundSource.Color) throw Invalid();
        if (packageGrants.Length != 0 && !packageGrants.Select(value => (value.PackageId, value.Revision)).ToHashSet()
            .SetEquals(snapshot.Effects.Packages.Select(value => (value.PackageId, value.Revision)))) throw Invalid();
        if (snapshot.EffectInputsFingerprint != Fingerprint(snapshot)) throw FingerprintInvalid();
        var basis = snapshot with { Background = BackgroundSource.Color, MediaFeatureInput = null };
        basis = basis with { EffectInputsFingerprint = Fingerprint(basis) };
        string baseProfile = ProjectSnapshotSourceProfile.Select(basis);
        byte[] source = packageGrants.Length == 0 ? ProjectSnapshotSourceProfile.Encode(basis)
            : StaticProjectSnapshotCodec.EncodeCore(basis, baseProfile, true, !basis.PresetApplications.Items.IsEmpty, externalPackages: true);
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new Metadata(Profile, StaticProjectSnapshotCodec.EngineIdentity,
            baseProfile, grant, snapshot.EffectInputsFingerprint, packageGrants, mediaGrant), Options);
        if ((long)source.Length + metadata.Length + 8 > MaximumBytes) throw Size();
        byte[] bytes = new byte[source.Length + metadata.Length + 8];
        Magic.CopyTo(bytes); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), metadata.Length);
        metadata.CopyTo(bytes, 8); source.CopyTo(bytes, metadata.Length + 8); return bytes;
    }

    /// <summary>严格读取固定输入，旧五来源保持原编码。</summary>
    public static (ProjectSnapshot Snapshot, string SourceProfile, ProjectRuntimeBackgroundGrant? Grant) Decode(ReadOnlyMemory<byte> bytes,
        IReadOnlyDictionary<(string, string), EffectPackageContents>? packages = null)
    {
        if (!IsRuntimeInput(bytes.Span))
        { var source = ProjectSnapshotSourceProfile.Decode(bytes); return (source, ProjectSnapshotSourceProfile.Select(source), null); }
        if (bytes.Length is < 9 or > MaximumBytes) throw Size();
        try
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[4..]);
            if (length <= 0 || length >= bytes.Length - 8) throw Invalid();
            using var document = JsonDocument.Parse(bytes.Slice(8, length));
            RejectDuplicateFields(document.RootElement);
            var metadata = JsonSerializer.Deserialize<Metadata>(bytes.Span.Slice(8, length), Options) ?? throw Invalid();
            if (metadata.Profile != Profile || metadata.EngineIdentity != StaticProjectSnapshotCodec.EngineIdentity)
                throw new SnapshotContractException("SnapshotVersionMismatch", "固定运行输入引擎或能力不匹配。");
            if (metadata.Background is not null) ValidateGrant(metadata.Background);
            ValidatePackages(metadata.PackageGrants);
            if (metadata.PackageGrants.Length > 0 && (packages is null || packages.Count != metadata.PackageGrants.Length
                || !packages.Keys.ToHashSet().SetEquals(metadata.PackageGrants.Select(value => (value.PackageId, value.Revision))))) throw Invalid();
            var basis = metadata.PackageGrants.Length == 0 ? ProjectSnapshotSourceProfile.Decode(bytes[(8 + length)..])
                : StaticProjectSnapshotCodec.DecodeCore(bytes[(8 + length)..], true, metadata.BaseProfile == PackagePresetProjectSnapshotCodec.Profile, packages);
            if (basis.Background != BackgroundSource.Color || ProjectSnapshotSourceProfile.Select(basis) != metadata.BaseProfile) throw Invalid();
            var grant = metadata.Background;
            var source = grant is null ? basis : basis with { Background = new() { Kind = grant.Kind, Path = grant.LogicalPath,
                Media = new(grant.Sha256, grant.ByteLength, grant.OriginalName), Fit = grant.Fit, Playback = grant.Playback } };
            source = source with { EffectInputsFingerprint = Fingerprint(source) };
            if (source.EffectInputsFingerprint != metadata.EffectInputsFingerprint) throw FingerprintInvalid();
            if (metadata.Media is { } media) source = source with { MediaFeatureInput = media.ToInput() };
            return (source, Profile, grant);
        }
        catch (SnapshotContractException) { throw; }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NullReferenceException or OverflowException or EffectPackageException or MediaAbException)
        { throw Invalid(); }
    }

    /// <summary>读取媒体特征输入的私有传输；不执行IO，不重建输入。</summary>
    public static ProjectRuntimeMediaGrant? ReadMediaGrant(ReadOnlyMemory<byte> bytes)
        => IsRuntimeInput(bytes.Span) ? ReadMetadata(bytes).Media : null;

    /// <summary>只检查Host私有授权结构，不读取文件。</summary>
    public static void ValidateGrant(ProjectRuntimeBackgroundGrant grant)
    {
        if (grant is null || grant.Kind is not (BackgroundKind.Image or BackgroundKind.Video)
            || !Enum.IsDefined(grant.Fit) || !Enum.IsDefined(grant.Playback) || grant.ByteLength <= 0
            || string.IsNullOrWhiteSpace(grant.FilePath) || !Path.IsPathFullyQualified(grant.FilePath)
            || Path.GetFullPath(grant.FilePath) != grant.FilePath) throw Invalid();
        var media = new MediaFileRevision(grant.Sha256, grant.ByteLength, grant.OriginalName);
        if (media.RelativePath != grant.LogicalPath) throw Invalid();
    }

    /// <summary>只验证私有授权封包结构与内嵌来源，Host不重复读取已固定的包目录。</summary>
    public static string ValidateEnvelope(ReadOnlyMemory<byte> bytes)
    {
        if (!IsRuntimeInput(bytes.Span)) return ProjectSnapshotSourceProfile.Select(ProjectSnapshotSourceProfile.Decode(bytes));
        _ = ReadMetadata(bytes); return Profile;
    }

    /// <summary>只用已持有的固定快照重编码授权引用，核对完整wire；不读取私有目录。</summary>
    public static void ValidateSnapshot(ReadOnlyMemory<byte> bytes, ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        byte[] expected;
        if (IsRuntimeInput(bytes.Span))
        {
            var metadata = ReadMetadata(bytes);
            // The Host-derived media input is attested by its own grant; the source identity excludes it.
            ProjectSnapshot withMedia = snapshot;
            if (snapshot.MediaFeatureInput is null && metadata.Media is { } media)
            {
                try { withMedia = snapshot with { MediaFeatureInput = media.ToInput() }; }
                catch (MediaAbException) { throw Invalid(); }
            }
            expected = Encode(withMedia, metadata.Background, metadata.PackageGrants, metadata.Media?.ArtifactPath);
        }
        else expected = ProjectSnapshotSourceProfile.Encode(snapshot);
        if (!bytes.Span.SequenceEqual(expected)) throw new SnapshotContractException("ProjectRuntimeSourceMismatch", "固定运行输入不属于同一源快照。");
    }

    /// <summary>读取待Worker加载的固定包目录授权；不执行IO。</summary>
    public static ProjectRuntimePackageGrant[] ReadPackageGrants(ReadOnlyMemory<byte> bytes)
        => IsRuntimeInput(bytes.Span) ? ReadMetadata(bytes).PackageGrants : [];

    /// <summary>Host只读固定源的画布和时长声明，不重新加载外置包文件。</summary>
    public static Composition ReadComposition(ReadOnlyMemory<byte> bytes)
    {
        ReadOnlyMemory<byte> source = bytes;
        if (IsRuntimeInput(bytes.Span))
        {
            _ = ReadMetadata(bytes);
            source = bytes[(8 + BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[4..]))..];
        }
        using var document = JsonDocument.Parse(source);
        return document.RootElement.GetProperty("snapshot").GetProperty("composition").Deserialize<Composition>(
            SnapshotJsonContract.CreateOptions()) ?? throw Invalid();
    }

    private static Metadata ReadMetadata(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 9 or > MaximumBytes) throw Size();
        try
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[4..]);
            if (length <= 0 || length >= bytes.Length - 8) throw Invalid();
            using var document = JsonDocument.Parse(bytes.Slice(8, length)); RejectDuplicateFields(document.RootElement);
            var metadata = JsonSerializer.Deserialize<Metadata>(bytes.Span.Slice(8, length), Options) ?? throw Invalid();
            if (metadata.Profile != Profile || metadata.EngineIdentity != StaticProjectSnapshotCodec.EngineIdentity) throw Invalid();
            if (metadata.Background is not null) ValidateGrant(metadata.Background);
            ValidatePackages(metadata.PackageGrants);
            using var source = JsonDocument.Parse(bytes[(8 + length)..]); RejectDuplicateFields(source.RootElement);
            if (source.RootElement.GetProperty("profile").GetString() != metadata.BaseProfile
                || !BackendSnapshotProfiles.IsSupported(metadata.BaseProfile) && !ProjectSnapshotSourceProfile.IsPackage(metadata.BaseProfile)) throw Invalid();
            return metadata;
        }
        catch (SnapshotContractException) { throw; }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or NullReferenceException)
        { throw Invalid(); }
    }
    private static void ValidatePackages(ProjectRuntimePackageGrant[] grants)
    {
        if (grants is null || grants.Any(value => value is null) || grants.Length > EffectPackageContentBudget.Default.MaximumFiles
            || grants.Select(value => (value.PackageId, value.Revision)).Distinct().Count() != grants.Length) throw Invalid();
        foreach (var grant in grants)
        {
            if (grant is null || string.IsNullOrWhiteSpace(grant.PackageId) || !Path.IsPathFullyQualified(grant.DirectoryPath)
                || Path.GetFullPath(grant.DirectoryPath) != grant.DirectoryPath) throw Invalid();
            _ = ProjectEffectsJson.PackagePath(grant.Revision);
        }
    }

    private static string? Fingerprint(ProjectSnapshot value) => ProjectEffectInputs.Fingerprint(ProjectSnapshotSourceProject.Restore(value));
    private static void RejectDuplicateFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject()) { if (!keys.Add(field.Name)) throw Invalid(); RejectDuplicateFields(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateFields(item);
    }
    private sealed record Metadata(string Profile, string EngineIdentity, string BaseProfile,
        ProjectRuntimeBackgroundGrant? Background, string? EffectInputsFingerprint, ProjectRuntimePackageGrant[] PackageGrants,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectRuntimeMediaGrant? Media = null);
    private static SnapshotContractException Invalid() => new("ProjectRuntimeInputInvalid", "固定运行输入或私有媒体授权无效。");
    private static SnapshotContractException Size() => new("SnapshotSizeExceeded", "固定运行输入超过 Worker payload 预算。");
    private static SnapshotContractException FingerprintInvalid() => new("SnapshotFingerprintMismatch", "固定运行输入与效果源指纹不匹配。");
}
