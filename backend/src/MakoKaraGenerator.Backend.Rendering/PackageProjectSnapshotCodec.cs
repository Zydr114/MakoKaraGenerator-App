using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>
/// 固定效果包源的有界 snapshot-v1 合同；只校验和复制内存源，不读取路径、编译或执行代码。
/// </summary>
/// <remarks>本 codec 自身不授予执行能力；公共项目预览另行实际握手与路由，正式任务仍拒包。预设应用和背景媒体仍明确拒绝。</remarks>
public static class PackageProjectSnapshotCodec
{
    /// <summary>专用源结构版本。</summary>
    public const string ContractVersion = StaticProjectSnapshotCodec.ContractVersion;
    /// <summary>固定包源能力；公共预览和内部 Worker 均须独立协商。</summary>
    public const string Profile = BackendSnapshotProfiles.PackageLyricsScene;
    /// <summary>包括包资源 base64 在内的完整 UTF-8 输入上限。</summary>
    public const int MaximumBytes = StaticProjectSnapshotCodec.MaximumBytes;
    /// <summary>全部源对象和集合元素的数量上限。</summary>
    public const int MaximumSourceValues = StaticProjectSnapshotCodec.MaximumSourceValues;
    /// <summary>精确的当前引擎身份。</summary>
    public static string EngineIdentity => StaticProjectSnapshotCodec.EngineIdentity;
    /// <summary>固定完整受支持快照；超限、无效或不支持的源明确拒绝。</summary>
    public static byte[] Encode(ProjectSnapshot snapshot) => Guard(() => StaticProjectSnapshotCodec.EncodeCore(snapshot, Profile, true));
    /// <summary>严格读取固定源并重新验证完整包闭包、领域结构和输入指纹。</summary>
    public static ProjectSnapshot Decode(ReadOnlyMemory<byte> bytes) => Guard(() => StaticProjectSnapshotCodec.DecodeCore(bytes, packages: true, presets: false));
    /// <summary>验证有界内存源及其输入指纹；不创建或执行渲染会话。</summary>
    public static void ValidateSupported(ProjectSnapshot snapshot) => Guard(() => { StaticProjectSnapshotCodec.ValidateCore(snapshot, Profile, true); return true; });

    /// <summary>检查工程包资源的不可变身份、内容预算和已实现的 role 引用；不执行作者代码。</summary>
    /// <remarks>
    /// 该检查专供资源读模型和审计使用。它会重新构造同一内容闭包并读取包定义，
    /// 但不会编译 C#、解码图片或打开媒体，因此不能把结果解释为完整运行能力。
    /// </remarks>
    public static PackageClosureInspection InspectResourceClosure(EffectPackageContents package)
    {
        ArgumentNullException.ThrowIfNull(package);
        string manifestHash = Convert.ToHexStringLower(SHA256.HashData(package.Manifest.AsSpan()));
        long totalBytes = package.Manifest.Length + package.Files.Sum(file => (long)file.Content.Length);
        string[] unsupported = [.. package.Files.Where(file => !SupportedResourceRoles.Contains(file.Role))
            .Select(file => file.Path).Order(StringComparer.Ordinal)];
        bool identity = false, references = false; string? failureCode = null;
        try
        {
            EffectPackageContentBudget budget = new()
            {
                MaximumBytes = MaximumBytes,
                MaximumFileBytes = MaximumBytes,
                MaximumFiles = MaximumSourceValues,
            };
            EffectPackageContents copy = EffectPackageContents.CreateSupported(package.Manifest.AsMemory(),
                package.Files.Select(file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray())),
                budget);
            identity = copy.PackageId == package.PackageId && copy.Revision == package.Revision
                && copy.Manifest.AsSpan().SequenceEqual(package.Manifest.AsSpan())
                && copy.Files.Length == package.Files.Length
                && copy.Files.Zip(package.Files).All(pair => pair.First.Path == pair.Second.Path
                    && pair.First.Role == pair.Second.Role && pair.First.Sha256 == pair.Second.Sha256
                    && pair.First.Content.AsSpan().SequenceEqual(pair.Second.Content.AsSpan()));
            if (!identity) failureCode = "PackageClosureIdentityMismatch";
            else
            {
                _ = EffectPackageDefinition.Read(copy);
                references = true;
            }
        }
        catch (EffectPackageException error) { failureCode = error.Code; }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException
            or KeyNotFoundException or OverflowException)
        { failureCode = "PackageClosureInvalid"; }
        return new(package.PackageId, package.Revision, manifestHash, package.Manifest.Length, totalBytes,
            package.Files.Length, identity, references, failureCode, unsupported);
    }

    /// <summary>当前资源读模型能够识别的包文件 role；未知 role 仍保留并标为 unsupported。</summary>
    public static IReadOnlySet<string> SupportedResourceRoles { get; } = new HashSet<string>([
        "asset", "csharp", "graph", "image", "shader", "shaderProgram", "svg", "svgResource"], StringComparer.Ordinal);

    private static T Guard<T>(Func<T> operation)
    {
        try { return operation(); }
        // Preserve stable codes and sanitized public messages, never parser/source details.
        catch (SnapshotContractException error) { throw new SnapshotContractException(error.Code, error.Message); }
        catch (Exception error) when (error is EffectPackageException or JsonException or InvalidOperationException
            or ArgumentException or NullReferenceException or KeyNotFoundException or OverflowException or NotSupportedException)
        { throw new SnapshotContractException("SnapshotInvalid", "快照固定包源结构无效。"); }
    }
}

/// <summary>固定效果包资源闭包的只读检查结果。</summary>
public sealed record PackageClosureInspection(string PackageId, string PackageRevision, string ManifestSha256,
    int ManifestSizeBytes, long TotalSizeBytes, int FileCount, bool IdentityValid, bool RoleReferencesValid,
    string? FailureCode, string[] UnsupportedPaths)
{
    /// <summary>完整闭包身份、语义引用和文件 role 均属于当前有限观察能力。</summary>
    public bool IsValidated => IdentityValid && RoleReferencesValid && UnsupportedPaths.Length == 0;
    /// <summary>当前可识别 role 的文件数。</summary>
    public int ValidatedFileCount => FileCount - UnsupportedPaths.Length;
}
