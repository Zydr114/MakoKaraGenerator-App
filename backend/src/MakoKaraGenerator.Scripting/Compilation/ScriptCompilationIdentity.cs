using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Scripting.Compilation;

/// <summary>编译器、选项、引用、SDK、执行引擎和分析规则的固定内容身份。</summary>
public sealed record ScriptCompilationIdentity(string CompilerVersion, string OptionsProfile, string ReferenceIdentity,
    string SdkIdentity, string EngineIdentity, string AnalysisRulesVersion)
{
    /// <summary>当前部署固定编译器版本；Worker 不通过加载 Roslyn 核验。</summary>
    public const string PinnedCompilerVersion = "5.0.0.0";
    /// <summary>SDK v2 纯度分析规则；修改规则必须升版并使旧编译制品失效。</summary>
    public const string V2AnalysisRules = "sdk2-purity-6";
    /// <summary>强制纯度分析规则身份；Worker 无须加载 Roslyn 即可核验。</summary>
    public const string CurrentAnalysisRules = V2AnalysisRules;
    /// <summary>固定 v1 编译选项；格式变更与旧缓存隔离。</summary>
    public const string V1Options = "csharp14|net10.0|release|nullable|deterministic|portable-pdb|unsafe:false|concurrentBuild:false|serial-emit-v1|artifact-format3";
    /// <summary>本部署的执行引擎身份，与固定工程封包一致。</summary>
    public static string CurrentEngineIdentity => "MakoKaraGenerator:" + ProductVersion.Current;

    /// <summary>根据实际分发引用和 SDK 建立 Worker 期望身份。</summary>
    public static ScriptCompilationIdentity ForDeployment(string referenceDirectory)
    {
        var references = ScriptReferenceIdentity.Read(referenceDirectory);
        return new(PinnedCompilerVersion, V1Options, references.Identity, references.SdkIdentity, CurrentEngineIdentity, CurrentAnalysisRules);
    }

    /// <summary>拒绝缺失或非规范的身份，不从 PE 文件名猜测部署兼容性。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CompilerVersion) || string.IsNullOrWhiteSpace(OptionsProfile)
            || string.IsNullOrWhiteSpace(EngineIdentity) || string.IsNullOrWhiteSpace(AnalysisRulesVersion))
            throw new InvalidDataException("脚本编译身份不完整。");
        ScriptCompilationInput.ValidateHash(ReferenceIdentity); ScriptCompilationInput.ValidateHash(SdkIdentity);
    }
}

/// <summary>不依赖 Roslyn 的实际分发引用集合身份。</summary>
public sealed record ScriptReferenceIdentity(string Identity, int Count, string SdkIdentity)
{
    /// <summary>按文件名和字节摘要计算参考集；SDK 与宿主共享同一程序集。</summary>
    public static ScriptReferenceIdentity Read(string referenceDirectory)
    {
        string[] paths = EnumeratePaths(referenceDirectory);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] sdkHash = [];
        foreach (string path in paths)
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            byte[] digest = SHA256.HashData(stream);
            ScriptCompilationInput.Add(hash, Path.GetFileName(path)); ScriptCompilationInput.Add(hash, digest); sdkHash = digest;
        }
        return new(Convert.ToHexStringLower(hash.GetHashAndReset()), paths.Length, Convert.ToHexStringLower(sdkHash));
    }

    /// <summary>同一次字节快照同时生成身份和编译引用，避免声明身份与实际 MetadataReference 的读取竞态。</summary>
    public static ScriptReferenceIdentity Read(string referenceDirectory, out ImmutableArray<ImmutableArray<byte>> images)
    {
        string[] paths = EnumeratePaths(referenceDirectory); var fixedImages = ImmutableArray.CreateBuilder<ImmutableArray<byte>>(paths.Length);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in paths)
        {
            byte[] bytes = File.ReadAllBytes(path); fixedImages.Add([.. bytes]);
            ScriptCompilationInput.Add(hash, Path.GetFileName(path)); ScriptCompilationInput.Add(hash, SHA256.HashData(bytes));
        }
        images = fixedImages.MoveToImmutable();
        return new(Convert.ToHexStringLower(hash.GetHashAndReset()), paths.Length,
            Convert.ToHexStringLower(SHA256.HashData(images[^1].AsSpan())));
    }

    /// <summary>完整固定 BCL 参考集以及宿主 SDK 路径；不加载作者程序集。</summary>
    public static string[] EnumeratePaths(string referenceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceDirectory);
        string[] paths = [.. Directory.EnumerateFiles(referenceDirectory, "*.dll").Order(StringComparer.Ordinal)];
        if (paths.Length == 0 || !paths.Any(static p => Path.GetFileName(p) == "System.Runtime.dll"))
            throw new ArgumentException("脚本参考程序集集合不完整。", nameof(referenceDirectory));
        return [.. paths, typeof(TokenId).Assembly.Location];
    }
}

/// <summary>标准化的不可变编译输入；参数、种子及无关包资源不参与内容键。</summary>
public sealed record ScriptCompilationInput(ImmutableArray<ScriptSource> Sources, ImmutableArray<string> EntryTypes,
    ScriptCompilationIdentity Identity, string ArtifactHash)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    /// <summary>排序并校验源码及入口，用全部编译身份计算内容键。</summary>
    public static ScriptCompilationInput Create(IEnumerable<ScriptSource> sources, IEnumerable<string> entryTypes, ScriptCompilationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(sources); ArgumentNullException.ThrowIfNull(entryTypes); ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        string[] entries = [.. entryTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (entries.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("入口集合不能包含空身份。", nameof(entryTypes));
        ScriptSource[] fixedSources = [.. sources];
        if (fixedSources.Length == 0 || fixedSources.Any(static s => s is null || s.Text is null || !ValidPath(s.Path))
            || fixedSources.Select(static s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fixedSources.Length)
            throw new ArgumentException("源码路径必须非空、相对、唯一且使用正斜线。", nameof(sources));
        Array.Sort(fixedSources, static (a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string value in new[] { identity.OptionsProfile, identity.CompilerVersion, identity.ReferenceIdentity,
            identity.SdkIdentity, identity.EngineIdentity, identity.AnalysisRulesVersion, "package-entries-v1", entries.Length.ToString(CultureInfo.InvariantCulture) }) Add(hash, value);
        foreach (string entry in entries) Add(hash, entry);
        foreach (ScriptSource source in fixedSources) { Add(hash, source.Path); Add(hash, source.Text); }
        return new([.. fixedSources], [.. entries], identity, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>校验规范 SHA-256，不接受大小写或路径别名。</summary>
    public static void ValidateHash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(static c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("脚本产物指纹不是规范 SHA-256。");
    }
    internal static void Add(IncrementalHash hash, string value) => Add(hash, StrictUtf8.GetBytes(value));
    internal static void Add(IncrementalHash hash, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length); hash.AppendData(bytes);
    }
    private static bool ValidPath(string path) => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path)
        && !path.Contains('\\') && !path.Contains(':') && !path.Split('/').Any(static s => s is "" or "." or "..");
}
