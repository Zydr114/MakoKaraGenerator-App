using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>固定效果包与静态预设的有界混合 source；只用于明确支持的分析与同帧测量接缝。</summary>
public static class PackagePresetProjectSnapshotCodec
{
    /// <summary>混合输入 source profile 标识。</summary>
    public const string Profile = BackendSnapshotProfiles.PackageLyricsScenePresets;
    /// <summary>完整 UTF-8 输入预算。</summary>
    public const int MaximumBytes = PackageProjectSnapshotCodec.MaximumBytes;
    /// <summary>源图对象与集合元素预算。</summary>
    public const int MaximumSourceValues = PackageProjectSnapshotCodec.MaximumSourceValues;
    /// <summary>固定 Runtime 引擎身份。</summary>
    public static string EngineIdentity => StaticProjectSnapshotCodec.EngineIdentity;

    /// <summary>编码含效果包和固定预设的完整快照。</summary>
    public static byte[] Encode(ProjectSnapshot snapshot) => Guard(() =>
        StaticProjectSnapshotCodec.EncodeCore(snapshot, Profile, packages: true, presets: true));

    /// <summary>严格解码并校验混合快照。</summary>
    public static ProjectSnapshot Decode(ReadOnlyMemory<byte> bytes) => Guard(() =>
        StaticProjectSnapshotCodec.DecodeCore(bytes, packages: true, presets: true));

    /// <summary>检查混合快照可被 Worker 消费。</summary>
    public static void ValidateSupported(ProjectSnapshot snapshot) => Guard(() =>
    {
        StaticProjectSnapshotCodec.ValidateCore(snapshot, Profile, packages: true, presets: true);
        return true;
    });

    private static T Guard<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (SnapshotContractException error) { throw new SnapshotContractException(error.Code, error.Message); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NullReferenceException
            or KeyNotFoundException or OverflowException or NotSupportedException or System.Text.Json.JsonException
            or MakoKaraGenerator.Core.Effects.EffectPackageException)
        { throw new SnapshotContractException("SnapshotInvalid", "快照固定包与预设源结构无效。"); }
    }
}

/// <summary>按完整工程输入选择唯一的 Worker snapshot source profile。</summary>
public static class ProjectSnapshotSourceProfile
{
    /// <summary>按完整输入选择静态、包或混合 profile。</summary>
    public static string Select(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool package = snapshot.EffectInputsFingerprint is not null || !snapshot.Effects.Packages.IsEmpty
            || !snapshot.Effects.Instances.IsEmpty || snapshot.Effects.Budgets != new MakoKaraGenerator.Core.Effects.ProjectEffectBudgets()
            || snapshot.PresetApplications.Items.Any(item => !item.Snapshot.Packages.IsEmpty || !item.Snapshot.EffectRecipes.IsEmpty)
            || !SourceMaterialCatalog.Collect(snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation, snapshot.PresetApplications).IsEmpty;
        bool presets = !snapshot.PresetApplications.Items.IsEmpty;
        if (package) return presets ? PackagePresetProjectSnapshotCodec.Profile : PackageProjectSnapshotCodec.Profile;
        return StaticProjectSnapshotCodec.SelectProfile(snapshot);
    }

    /// <summary>来源需要原效果包 Runtime；包含只有预设配方的输入。</summary>
    public static bool IsPackage(string? profile) => profile is PackageProjectSnapshotCodec.Profile or PackagePresetProjectSnapshotCodec.Profile;

    /// <summary>按选择的 profile 编码完整输入。</summary>
    public static byte[] Encode(ProjectSnapshot snapshot) => Select(snapshot) switch
    {
        PackageProjectSnapshotCodec.Profile => PackageProjectSnapshotCodec.Encode(snapshot),
        PackagePresetProjectSnapshotCodec.Profile => PackagePresetProjectSnapshotCodec.Encode(snapshot),
        var profile => StaticProjectSnapshotCodec.Encode(snapshot, profile),
    };

    /// <summary>按 wire profile 严格解码完整输入。</summary>
    public static ProjectSnapshot Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is <= 0 or > StaticProjectSnapshotCodec.MaximumBytes)
            throw new SnapshotContractException("SnapshotSizeExceeded", "快照输入为空或超过字节上限。");
        using var document = System.Text.Json.JsonDocument.Parse(bytes, new() { MaxDepth = 64 });
        string? profile = document.RootElement.TryGetProperty("profile", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() : null;
        return profile switch
        {
            PackageProjectSnapshotCodec.Profile => PackageProjectSnapshotCodec.Decode(bytes),
            PackagePresetProjectSnapshotCodec.Profile => PackagePresetProjectSnapshotCodec.Decode(bytes),
            _ => StaticProjectSnapshotCodec.Decode(bytes),
        };
    }
}
