using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>
/// snapshot-v1 的静态歌词/场景源合同；独立于工程文件格式，没有文件读写或执行能力。
/// </summary>
/// <remarks>
/// 全部十五个 Snapshot 源字段都有明确位置；原能力的预设 null 槽和拒绝语义保持。
/// 独立纯静态预设能力保留完整固定记录；效果包与背景媒体仍明确拒绝。
/// 音频路径和媒体身份仅作为声明性源信息保留，
/// 本 codec 不访问路径、读取媒体或授予文件能力；Hosting 必须另行持有授权资源租约。
/// </remarks>
public static class StaticProjectSnapshotCodec
{
    /// <summary>专用输入结构版本；不从工程 schema 推断。</summary>
    public const string ContractVersion = "snapshot-v1";

    /// <summary>明确的有限能力版本；完整资源支持需要另行协商。</summary>
    public const string Profile = BackendSnapshotProfiles.StaticLyricsScene;

    /// <summary>保留完整已接受分析、仍不执行效果脚本的独立静态能力。</summary>
    public const string AnalysisProfile = BackendSnapshotProfiles.StaticLyricsSceneAnalysis;

    /// <summary>保留实际固定预设记录和可选完整分析的纯静态能力。</summary>
    public const string PresetsProfile = BackendSnapshotProfiles.StaticLyricsScenePresets;

    /// <summary>单份输入的固定字节上限。</summary>
    public const int MaximumBytes = 4 * 1024 * 1024;

    /// <summary>源对象/集合元素总数上限，在序列化和领域验证之前检查。</summary>
    public const int MaximumSourceValues = 200_000;

    /// <summary>当前渲染源模型的明确构建身份。</summary>
    public static string EngineIdentity => "MakoKaraGenerator:" + ProductVersion.Current;

    private static readonly JsonSerializerOptions Options = SnapshotJsonContract.CreateOptions();
    private static readonly JsonSerializerOptions PresetOptions = SnapshotJsonContract.CreateOptions(strictEnums: true);
    private static readonly JsonSerializerOptions PackageOptions = SnapshotJsonContract.CreateOptions(packageMaterials: true);
    private static readonly JsonSerializerOptions PackagePresetOptions = SnapshotJsonContract.CreateOptions(packageMaterials: true, strictEnums: true);

    /// <summary>按固定预设及分析源是否存在选择唯一能力；实际支持与预算仍由校验或编码检查。</summary>
    /// <param name="snapshot">完整固定快照。</param>
    /// <returns>非空固定应用选择预设能力，否则按分析空值选择原两能力。</returns>
    public static string SelectProfile(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.PresetApplications is { Items.IsEmpty: false } ? PresetsProfile
            : snapshot.AudioAnalysis is null ? Profile : AnalysisProfile;
    }

    /// <summary>固定全部受支持源字段到独立的 UTF-8 输入字节。</summary>
    public static byte[] Encode(ProjectSnapshot snapshot) => Encode(snapshot, Profile);

    /// <summary>按显式互斥能力编码全部源字段，不因默认选择静默剥离分析。</summary>
    /// <param name="snapshot">完整固定快照。</param>
    /// <param name="profile">原静态能力、完整分析或纯静态固定预设能力。</param>
    /// <returns>有界UTF-8快照字节。</returns>
    public static byte[] Encode(ProjectSnapshot snapshot, string profile) => EncodeCore(snapshot, profile, false);

    internal static byte[] EncodeCore(ProjectSnapshot snapshot, string profile, bool packages)
        => EncodeCore(snapshot, profile, packages, presets: profile == PresetsProfile || profile == BackendSnapshotProfiles.PackageLyricsScenePresets);

    internal static byte[] EncodeCore(ProjectSnapshot snapshot, string profile, bool packages, bool presets, bool externalPackages = false)
    {
        Source source = ValidateAndCapture(snapshot, profile, packages, presets, validateWire: false, externalPackages);
        JsonSerializerOptions options = SelectOptions(profile, packages, presets);
        Envelope envelope = new(ContractVersion, profile, EngineIdentity, source);
        try
        {
            using BoundedSnapshotStream stream = new(MaximumBytes);
            JsonSerializer.Serialize(stream, envelope, options);
            byte[] bytes = stream.ToArray();
            using JsonDocument document = JsonDocument.Parse(bytes, new() { MaxDepth = 64 });
            int sourceValues = 0;
            RejectDuplicateFields(document.RootElement, ref sourceValues);
            return bytes;
        }
        catch (SnapshotContractException) { throw; }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        { throw new SnapshotContractException("SnapshotInvalid", "快照源无法按专用合同编码。", error); }
    }

    /// <summary>严格读取专用输入，校验版本/能力/源结构，创建新的不可变快照。</summary>
    public static ProjectSnapshot Decode(ReadOnlyMemory<byte> bytes) => DecodeCore(bytes, false);

    internal static ProjectSnapshot DecodeCore(ReadOnlyMemory<byte> bytes, bool packages)
        => DecodeCore(bytes, packages, presets: packages && TryReadProfile(bytes.Span) == BackendSnapshotProfiles.PackageLyricsScenePresets);

    internal static ProjectSnapshot DecodeCore(ReadOnlyMemory<byte> bytes, bool packages, bool presets,
        IReadOnlyDictionary<(string, string), EffectPackageContents>? externalPackages = null)
    {
        if (bytes.Length is <= 0 or > MaximumBytes)
            throw new SnapshotContractException("SnapshotSizeExceeded", "快照输入为空或超过字节上限。");
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new() { MaxDepth = 64 });
            int sourceValues = 0;
            RejectDuplicateFields(document.RootElement, ref sourceValues);
            string? profile = document.RootElement.TryGetProperty("profile", out var profileValue) && profileValue.ValueKind == JsonValueKind.String
                ? profileValue.GetString() : null;
            JsonSerializerOptions options = SelectOptions(profile ?? string.Empty, packages, presets || profile == PresetsProfile);
            Envelope envelope = JsonSerializer.Deserialize<Envelope>(bytes.Span, options)
                ?? throw new SnapshotContractException("SnapshotInvalid", "专用快照根不能为空。");
            if (envelope.ContractVersion != ContractVersion || !IsSupported(envelope.Profile, packages, presets || envelope.Profile == PresetsProfile)
                || envelope.EngineIdentity != EngineIdentity)
                throw new SnapshotContractException("SnapshotVersionMismatch", "快照结构、能力或引擎身份不匹配。");
            ProjectSnapshot snapshot = envelope.Snapshot.ToSnapshot(envelope.Profile, packages, presets || envelope.Profile == PresetsProfile, externalPackages);
            ValidateAndCapture(snapshot, envelope.Profile, packages, presets || envelope.Profile == PresetsProfile, true, externalPackages is not null);
            return snapshot;
        }
        catch (SnapshotContractException) { throw; }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException
            or InvalidOperationException or NullReferenceException)
        { throw new SnapshotContractException("SnapshotInvalid", "专用快照字段或领域结构无效。", error); }
    }

    /// <summary>明确检查本能力是否能无损消费输入；无 IO、不创建或执行渲染会话。</summary>
    public static void ValidateSupported(ProjectSnapshot snapshot) => ValidateSupported(snapshot, Profile);

    /// <summary>检查显式互斥能力、完整领域源与提前源图预算，无IO或渲染执行。</summary>
    /// <param name="snapshot">完整固定快照。</param>
    /// <param name="profile">原静态能力、完整分析或纯静态固定预设能力。</param>
    public static void ValidateSupported(ProjectSnapshot snapshot, string profile) => ValidateCore(snapshot, profile, false);

    internal static void ValidateCore(ProjectSnapshot snapshot, string profile, bool packages)
        => ValidateCore(snapshot, profile, packages, presets: profile == PresetsProfile || profile == BackendSnapshotProfiles.PackageLyricsScenePresets);

    internal static void ValidateCore(ProjectSnapshot snapshot, string profile, bool packages, bool presets)
        => ValidateAndCapture(snapshot, profile, packages, presets, validateWire: true);

    private static Source ValidateAndCapture(ProjectSnapshot snapshot, string profile, bool packages, bool presets, bool validateWire, bool externalPackages = false)
    {
        JsonSerializerOptions options = SelectOptions(profile, packages, presets);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.MediaFeatureInput is not null)
            throw new SnapshotContractException("SnapshotMediaFeatureInputUnsupported", "此源快照协议不能传输固定媒体运行输入。");
        if (!IsSupported(profile, packages, presets))
            throw new SnapshotContractException("SnapshotVersionMismatch", "快照能力不受支持。");
        string[] modelMembers = typeof(ProjectSnapshot).GetProperties().Where(property => property.SetMethod is not null && property.Name != nameof(ProjectSnapshot.MediaFeatureInput)).Select(property => property.Name).ToArray();
        if (!modelMembers.ToHashSet(StringComparer.Ordinal).SetEquals(typeof(Source).GetProperties().Select(property => property.Name)))
            throw new SnapshotContractException("SnapshotTypeUnsupported", "Snapshot源字段与专用合同不匹配。");
        if (snapshot.Effects is null || snapshot.PresetApplications is null || snapshot.Background is null
            || snapshot.Composition is null || snapshot.Lyrics is null || snapshot.LyricPresentation is null || snapshot.LyricStyle is null)
            throw new SnapshotContractException("SnapshotInvalid", "快照必填源不能为空。");
        if (!packages && (snapshot.EffectInputsFingerprint is not null || !snapshot.Effects.Packages.IsEmpty
            || !snapshot.Effects.Instances.IsEmpty || snapshot.Effects.Budgets != new ProjectEffectBudgets()
            || profile != PresetsProfile && !snapshot.PresetApplications.Items.IsEmpty))
            throw new SnapshotContractException("SnapshotEffectsUnsupported", "本能力尚不支持效果包、固定预设或效果预算。");
        if (packages && !presets && !snapshot.PresetApplications.Items.IsEmpty)
            throw new SnapshotContractException("SnapshotEffectsUnsupported", "本能力尚不支持固定预设源。");
        if (presets && snapshot.PresetApplications.Items.IsEmpty)
            throw new SnapshotContractException("SnapshotPresetInvalid", "固定预设能力必须含实际应用记录。");
        if (packages && !externalPackages) SnapshotEffectSourceCodec.ValidateBudget(snapshot.Effects);
        if (profile == Profile && snapshot.AudioAnalysis is not null)
            throw new SnapshotContractException("SnapshotMediaUnsupported", "原静态能力尚不支持分析源。");
        if (profile == AnalysisProfile && snapshot.AudioAnalysis is null)
            throw new SnapshotContractException("SnapshotAnalysisInvalid", "完整分析静态能力必须包含非空已接受分析。");
        if (snapshot.Background.Kind != BackgroundKind.Color
            || snapshot.Background.Path is not null || snapshot.Background.Media is not null)
            throw new SnapshotContractException("SnapshotMediaUnsupported", "本能力尚不支持背景媒体。");
        if (snapshot.AudioPath is not null && string.IsNullOrWhiteSpace(snapshot.AudioPath)
            || snapshot.AudioMedia is not null && snapshot.AudioPath is null)
            throw new SnapshotContractException("SnapshotInvalid", "音频声明必须有非空源路径；媒体身份不能脱离源。");
        if ((profile == AnalysisProfile || (packages || profile == PresetsProfile) && snapshot.AudioAnalysis is not null) && (string.IsNullOrWhiteSpace(snapshot.AudioPath) || snapshot.AudioMedia is null
            || snapshot.AudioAnalysis?.Data is { } data && data.SourceHash != snapshot.AudioMedia.Hash))
            throw new SnapshotContractException("SnapshotAnalysisInvalid", "完整分析必须属于同一固定音频身份与源声明。");

        if (packages && !externalPackages)
        {
            // Count the complete source graph and effect metadata together before any package base64 is created.
            int sourceValues = SnapshotJsonContract.ValidateSourceGraph(
                new Envelope(ContractVersion, profile, EngineIdentity, Source.FromSnapshot(snapshot, packages, includeEffects: false, presets)),
                options, MaximumSourceValues, MaximumBytes, packageMaterials: true);
            // The null effects slot in the source graph is replaced by the full effects DTO.
            int effectValues = SnapshotEffectSourceCodec.ValidateGraph(snapshot.Effects, options);
            if ((long)sourceValues + effectValues - 1 > MaximumSourceValues)
                throw new SnapshotContractException("SnapshotSizeExceeded", "快照源对象/集合数量超过上限。");
        }
        Source source = Source.FromSnapshot(snapshot, packages, presets: presets || profile == PresetsProfile, externalPackages: externalPackages);
        SnapshotJsonContract.ValidateSourceGraph(new Envelope(ContractVersion, profile, EngineIdentity, source),
            options, MaximumSourceValues, MaximumBytes, packages);
        if (snapshot.SchemaVersion != Project.CurrentSchemaVersion || snapshot.Lyrics.SchemaVersion != LyricsDocument.CurrentSchemaVersion)
            throw new SnapshotContractException("SnapshotVersionMismatch", "快照工程或歌词源版本不匹配。");
        Project project = ProjectSnapshotSourceProject.Restore(snapshot);
        try
        {
            if (!packages && !SourceMaterialCatalog.Collect(project).IsEmpty)
                throw new SnapshotContractException("SnapshotEffectsUnsupported", "本能力尚不支持包材质。");
            if (packages && !StringComparer.Ordinal.Equals(snapshot.EffectInputsFingerprint, ProjectEffectInputs.Fingerprint(project)))
                throw new SnapshotContractException("SnapshotFingerprintMismatch", "快照效果输入指纹与完整源不匹配。");
            ValidationReport report = ProjectValidator.Validate(project, ValidationContext.Open);
            if (string.IsNullOrWhiteSpace(snapshot.Name) || report.HasErrors)
                throw new SnapshotContractException("SnapshotInvalid", packages ? "快照含无效领域源。" : "快照含无效领域源：" + report.ToSummary());
            if ((packages || presets || profile == PresetsProfile) && validateWire)
            {
                using BoundedSnapshotStream stream = new(MaximumBytes);
                JsonSerializer.Serialize(stream, new Envelope(ContractVersion, profile, EngineIdentity, source), options);
            }
        }
        catch (SnapshotContractException) { throw; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NullReferenceException
            or JsonException or EffectPackageException)
        { throw new SnapshotContractException("SnapshotInvalid", "快照领域源结构无效。", packages ? null : error); }
        return source;
    }

    private static void RejectDuplicateFields(JsonElement value, ref int values)
    {
        if (++values > MaximumSourceValues)
            throw new SnapshotContractException("SnapshotSizeExceeded", "快照输入对象/集合数量超过上限。");
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> fields = new(StringComparer.Ordinal);
            foreach (JsonProperty field in value.EnumerateObject())
            {
                if (!fields.Add(field.Name))
                    throw new SnapshotContractException("SnapshotDuplicateField", "快照不能含重复字段。");
                RejectDuplicateFields(field.Value, ref values);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicateFields(item, ref values);
    }

    private sealed record Envelope(string ContractVersion, string Profile, string EngineIdentity, Source Snapshot);

    private static JsonSerializerOptions SelectOptions(string profile, bool packages, bool presets)
        => packages ? presets ? PackagePresetOptions : PackageOptions : presets || profile == PresetsProfile ? PresetOptions : Options;

    private static bool IsSupported(string? profile, bool packages, bool presets)
        => packages ? profile == PackageProjectSnapshotCodec.Profile || presets && profile == BackendSnapshotProfiles.PackageLyricsScenePresets
            : BackendSnapshotProfiles.IsSupported(profile);

    private static string? TryReadProfile(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray());
            return document.RootElement.TryGetProperty("profile", out JsonElement profile) && profile.ValueKind == JsonValueKind.String
                ? profile.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private sealed record Source(
        string Name, int SchemaVersion, Composition Composition, LyricsDocument Lyrics,
        LyricPresentationDefinition LyricPresentation, LyricStyle LyricStyle, string? LyricStylePresetId,
        BackgroundSource Background, string? AudioPath,
        MediaFileRevision? AudioMedia, JsonElement? AudioAnalysis, JsonElement? Effects,
        JsonElement? PresetApplications, string? EffectInputsFingerprint)
    {
        internal static Source FromSnapshot(ProjectSnapshot snapshot, bool packages, bool includeEffects = true, bool presets = false, bool externalPackages = false) => new(
            snapshot.Name, snapshot.SchemaVersion, snapshot.Composition, snapshot.Lyrics,
            snapshot.LyricPresentation, snapshot.LyricStyle, snapshot.LyricStylePresetId,
            snapshot.Background, snapshot.AudioPath, snapshot.AudioMedia, SnapshotAudioAnalysisSourceCodec.Encode(snapshot.AudioAnalysis),
            packages && includeEffects ? externalPackages ? SnapshotEffectSourceCodec.EncodeExternal(snapshot.Effects, PackageOptions)
                : SnapshotEffectSourceCodec.Encode(snapshot.Effects, PackageOptions) : null,
            presets ? SnapshotPresetApplicationsSourceCodec.Encode(snapshot.PresetApplications,
                packages ? PackagePresetOptions : PresetOptions, packages ? snapshot.Effects : null) : null, snapshot.EffectInputsFingerprint);

        internal ProjectSnapshot ToSnapshot(string profile, bool packages, bool presets,
            IReadOnlyDictionary<(string, string), EffectPackageContents>? externalPackages = null)
        {
            if (!packages && Effects is not null || !presets && profile != PresetsProfile && PresetApplications is not null)
                throw new SnapshotContractException("SnapshotEffectsUnsupported", "本能力尚不支持非空包或预设源。");
            if (profile == Profile && AudioAnalysis is not null)
                throw new SnapshotContractException("SnapshotMediaUnsupported", "本能力尚不支持非空分析源。");
            ProjectEffects effects = packages ? externalPackages is not null
                ? SnapshotEffectSourceCodec.DecodeExternal(Effects, SelectOptions(profile, true, presets), externalPackages)
                : SnapshotEffectSourceCodec.Decode(Effects, SelectOptions(profile, packages: true, presets)) : ProjectEffects.Empty;
            return new()
            {
                Name = Name, SchemaVersion = SchemaVersion, Composition = Composition, Lyrics = Lyrics,
                LyricPresentation = LyricPresentation, LyricStyle = LyricStyle, LyricStylePresetId = LyricStylePresetId,
                Background = Background, AudioPath = AudioPath,
                AudioMedia = AudioMedia, AudioAnalysis = SnapshotAudioAnalysisSourceCodec.Decode(AudioAnalysis, AudioMedia?.Hash), Effects = effects,
                PresetApplications = presets || profile == PresetsProfile ? SnapshotPresetApplicationsSourceCodec.Decode(PresetApplications,
                    packages ? PackagePresetOptions : PresetOptions, packages ? effects : null)
                    : Core.Projects.PresetApplications.Empty, EffectInputsFingerprint = EffectInputsFingerprint,
            };
        }
    }

    internal sealed class BoundedSnapshotStream(int limit) : Stream
    {
        private readonly MemoryStream _inner = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public byte[] ToArray() => _inner.ToArray();
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > limit - Length)
                throw new SnapshotContractException("SnapshotSizeExceeded", "快照编码超过字节上限。");
            _inner.Write(buffer);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
