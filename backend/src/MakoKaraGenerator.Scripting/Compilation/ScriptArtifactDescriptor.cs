using System.Collections.Immutable;
using System.Security.Cryptography;

namespace MakoKaraGenerator.Scripting.Compilation;

/// <summary>内部编译产物读前预算；独立于既有源码与 IPC 输入预算。</summary>
public sealed record ScriptArtifactBudget
{
    /// <summary>单份 PE 上限；默认 32 MiB。</summary>
    public long MaximumPeBytes { get; init; } = 32L * 1024 * 1024;
    /// <summary>单份 portable PDB 上限；默认 32 MiB。</summary>
    public long MaximumPdbBytes { get; init; } = 32L * 1024 * 1024;
    /// <summary>身份与完整诊断清单上限；默认 1 MiB。</summary>
    public long MaximumManifestBytes { get; init; } = 1024 * 1024;
    /// <summary>默认内部预算。</summary>
    public static ScriptArtifactBudget Default { get; } = new();
    /// <summary>拒绝非正数或无法以单数组承载的长度。</summary>
    public void Validate()
    {
        if (MaximumPeBytes is <= 0 or > int.MaxValue || MaximumPdbBytes is <= 0 or > int.MaxValue
            || MaximumManifestBytes is <= 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(MaximumPeBytes));
    }
}

/// <summary>固定输入指纹与实际 PE／PDB 字节摘要分开存储的完整产物描述。</summary>
public sealed record ScriptArtifactDescriptor(string ArtifactHash, ScriptCompilationIdentity Identity,
    ImmutableArray<string> EntryTypes, string PeHash, string PdbHash, long PeBytes, long PdbBytes)
{
    /// <summary>从成功编译及其固定输入建立描述；不能给失败编译制造可运行身份。</summary>
    public static ScriptArtifactDescriptor Create(ScriptCompilationInput input, ScriptCompilationResult result)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded || input.ArtifactHash != result.ArtifactHash) throw new InvalidDataException("失败或错配编译不能描述为固定产物。");
        return new(input.ArtifactHash, input.Identity, input.EntryTypes, Hash(result.PeImage), Hash(result.PdbImage), result.PeImage.Length, result.PdbImage.Length);
    }
    /// <summary>分配字节以前校验身份、规范入口和长度预算。</summary>
    public void Validate(ScriptArtifactBudget? budget = null)
    {
        budget ??= ScriptArtifactBudget.Default; budget.Validate();
        ScriptCompilationInput.ValidateHash(ArtifactHash); ScriptCompilationInput.ValidateHash(PeHash); ScriptCompilationInput.ValidateHash(PdbHash);
        if (Identity is null) throw new InvalidDataException("脚本产物缺少编译身份。");
        Identity.Validate();
        if (EntryTypes.IsDefault || EntryTypes.Any(string.IsNullOrWhiteSpace)
            || !EntryTypes.SequenceEqual(EntryTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            || PeBytes <= 0 || PdbBytes <= 0 || PeBytes > budget.MaximumPeBytes || PdbBytes > budget.MaximumPdbBytes)
            throw new InvalidDataException("脚本产物入口或字节预算无效。");
    }
    /// <summary>校验结果的实际字节、完整身份及可运行诊断。</summary>
    public void ValidateResult(ScriptCompilationResult result, ScriptArtifactBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(result); Validate(budget);
        if (result.Diagnostics.IsDefault || result.Diagnostics.Any(d => d is null || d.IsError || string.IsNullOrWhiteSpace(d.Code)
            || d.Message is null || d.Line < 0 || d.Column < 0)
            || !result.Succeeded || result.ArtifactHash != ArtifactHash || result.PeImage.Length != PeBytes || result.PdbImage.Length != PdbBytes
            || Hash(result.PeImage) != PeHash || Hash(result.PdbImage) != PdbHash)
            throw new InvalidDataException("脚本编译产物摘要或固定身份校验失败。");
    }
    private static string Hash(ImmutableArray<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan()));
}

/// <summary>严格消费固定产物；没有编译器、SDK 搜索或旧产物回退路径。</summary>
public sealed class FixedScriptArtifactProvider : IScriptCompilationService
{
    private readonly ScriptCompilationIdentity _identity;
    private readonly Dictionary<string, ScriptCompilationResult> _artifacts;
    private readonly ScriptArtifactBudget _budget;
    private readonly Dictionary<string, ScriptFailedCompilation> _failures;
    /// <inheritdoc/>
    public ScriptCompilationIdentity Identity => _identity;
    /// <summary>建立同部署产物集合；装载前校验全部身份、字节摘要和预算。</summary>
    public FixedScriptArtifactProvider(ScriptCompilationIdentity identity, IEnumerable<ScriptCompilationResult> artifacts, ScriptArtifactBudget? budget = null,
        IEnumerable<ScriptFailedCompilation>? failures = null)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(artifacts); identity.Validate();
        _identity = identity; _budget = budget ?? ScriptArtifactBudget.Default; _budget.Validate(); _artifacts = []; _failures = [];
        foreach (var artifact in artifacts)
        {
            if (artifact?.Descriptor is not { } descriptor || descriptor.Identity != identity)
                throw new InvalidDataException("固定脚本产物与当前 SDK、引用或执行部署不匹配。");
            descriptor.ValidateResult(artifact, _budget);
            if (!_artifacts.TryAdd(artifact.ArtifactHash, artifact)) throw new InvalidDataException("固定产物身份重复。");
        }
        foreach (var failure in failures ?? [])
        {
            if (failure is null || failure.Identity != identity) throw new InvalidDataException("固定失败编译与当前部署不匹配。");
            failure.Validate();
            if (_artifacts.ContainsKey(failure.ArtifactHash) || !_failures.TryAdd(failure.ArtifactHash, failure))
                throw new InvalidDataException("固定编译结果身份重复。");
        }
    }
    /// <inheritdoc/>
    public Task<ScriptCompilationResult> CompileAsync(IEnumerable<ScriptSource> sources, IEnumerable<string> entryTypes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = ScriptCompilationInput.Create(sources, entryTypes, _identity);
        if (_failures.TryGetValue(input.ArtifactHash, out var failure))
        {
            failure.ValidateSources(input);
            return Task.FromResult(new ScriptCompilationResult(input.ArtifactHash, [], [], failure.Diagnostics));
        }
        if (!_artifacts.TryGetValue(input.ArtifactHash, out var artifact) || !artifact.Descriptor!.EntryTypes.SequenceEqual(input.EntryTypes))
            throw new InvalidDataException("固定脚本产物缺失或不属于请求的源码及入口。");
        artifact.Descriptor.ValidateResult(artifact, _budget); cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(artifact);
    }
}
