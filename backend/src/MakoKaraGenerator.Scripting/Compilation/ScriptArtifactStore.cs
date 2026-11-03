using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Scripting.Compilation;

/// <summary>固定磁盘产物及其在用租约；释放后缓存才允许淘汰该代次。</summary>
public sealed class ScriptArtifactLease : IDisposable
{
    private FileStream? _lease;
    internal ScriptArtifactLease(ScriptCompilationResult artifact, FileStream lease) { Artifact = artifact; _lease = lease; }
    /// <summary>读前预算及摘要验证后的不可变产物。</summary>
    public ScriptCompilationResult Artifact { get; }
    /// <summary>归还跨进程租约。</summary>
    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}

/// <summary>原子内容地址存储；读取、租约与配额淘汰使用同一跨进程互斥边界。</summary>
public static class ScriptArtifactStore
{
    /// <summary>默认持久缓存配额 256 MiB；源码与 IPC 预算保持独立。</summary>
    public const long DefaultMaximumCacheBytes = 256L * 1024 * 1024;
    private static readonly string[] ArtifactFileNames = ["build.json", "effect.dll", "effect.pdb"];
    private static readonly JsonSerializerOptions Json = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, MaxDepth = 16 };

    /// <summary>发布成功结果，已有代次必须完全匹配；配额不足明确失败且不发布新代次。</summary>
    public static string Publish(string root, ScriptCompilationResult result, ScriptArtifactBudget? budget = null,
        long maximumCacheBytes = DefaultMaximumCacheBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root); ArgumentNullException.ThrowIfNull(result);
        budget ??= ScriptArtifactBudget.Default; budget.Validate();
        if (result.Descriptor is not { } descriptor) throw new InvalidDataException("脚本产物缺少完整身份描述。");
        descriptor.ValidateResult(result, budget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCacheBytes);
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new ArtifactManifest(3, descriptor, result.Diagnostics), Json);
        if (manifest.LongLength > budget.MaximumManifestBytes) throw new InvalidDataException("脚本产物清单超过字节预算。");
        long bytes = checked(result.PeImage.Length + (long)result.PdbImage.Length + manifest.Length);
        Directory.CreateDirectory(root); using var gate = AcquireLock(root, "cache", cancellationToken);
        CleanupStaging(root, cancellationToken);
        string final = Path.Combine(root, result.ArtifactHash);
        if (Directory.Exists(final))
        {
            using var lease = OpenLease(root, result.ArtifactHash, FileShare.Read);
            ValidateExisting(final, result, budget);
            PruneCore(root, maximumCacheBytes, 0, cancellationToken);
            return final;
        }
        PruneCore(root, maximumCacheBytes, bytes, cancellationToken);
        string staging = Path.Combine(root, ".build-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        try
        {
            WriteFresh(Path.Combine(staging, "effect.dll"), result.PeImage.AsSpan());
            WriteFresh(Path.Combine(staging, "effect.pdb"), result.PdbImage.AsSpan());
            WriteFresh(Path.Combine(staging, "build.json"), manifest);
            ValidateFileClosure(staging);
            VerifyWritten(Path.Combine(staging, "effect.dll"), result.PeImage.AsSpan(), budget.MaximumPeBytes, cancellationToken);
            VerifyWritten(Path.Combine(staging, "effect.pdb"), result.PdbImage.AsSpan(), budget.MaximumPdbBytes, cancellationToken);
            VerifyWritten(Path.Combine(staging, "build.json"), manifest, budget.MaximumManifestBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); Directory.Move(staging, final); return final;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    /// <summary>在全局清理闸门下读取固定产物；文件长度在分配前检查。</summary>
    public static ScriptCompilationResult Load(string directory, ScriptArtifactBudget? budget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory); budget ??= ScriptArtifactBudget.Default; budget.Validate();
        string full = Path.GetFullPath(directory);
        string root = Path.GetDirectoryName(full)!;
        using var gate = AcquireLock(root, "cache", CancellationToken.None); CleanupStaging(root, CancellationToken.None);
        return LoadCore(full, budget);
    }

    /// <summary>取用与清理互斥；共享读租约在 Load 完成后仍保护目录免于跨进程淘汰。</summary>
    public static ScriptArtifactLease AcquireLease(string root, string artifactHash, ScriptArtifactBudget? budget = null,
        CancellationToken cancellationToken = default)
        => TryAcquireLease(root, artifactHash, budget, cancellationToken) ?? throw new DirectoryNotFoundException("固定脚本产物不存在。");

    /// <summary>在同一清理闸门内检查存在并取得租约；正常淘汰竞争返回缓存未命中。</summary>
    public static ScriptArtifactLease? TryAcquireLease(string root, string artifactHash, ScriptArtifactBudget? budget = null,
        CancellationToken cancellationToken = default)
        => TryAcquireLease(root, artifactHash, budget, null, cancellationToken);

    /// <summary>取用时可在同一闸门内执行配额清理，当前产物的租约保持有效。</summary>
    public static ScriptArtifactLease? TryAcquireLease(string root, string artifactHash, ScriptArtifactBudget? budget,
        long? maximumCacheBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root); ScriptCompilationInput.ValidateHash(artifactHash);
        if (maximumCacheBytes is { } maximum) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        budget ??= ScriptArtifactBudget.Default; budget.Validate();
        using var gate = AcquireLock(root, "cache", cancellationToken); CleanupStaging(root, cancellationToken);
        string directory = Path.Combine(root, artifactHash);
        if (!Directory.Exists(directory)) return null;
        var lease = OpenLease(root, artifactHash, FileShare.Read);
        try
        {
            var artifact = LoadCore(directory, budget);
            if (artifact.ArtifactHash != artifactHash) throw new InvalidDataException("脚本缓存目录与完整编译身份不一致。");
            if (maximumCacheBytes is { } limit) PruneCore(root, limit, 0, cancellationToken);
            return new(artifact, lease);
        }
        catch
        {
            lease.Dispose();
            if (!Directory.Exists(directory)) File.Delete(LeasePath(root, artifactHash));
            throw;
        }
    }

    /// <summary>跨进程编译使用 256 个固定条带锁；同键互斥且失败源码不会无限产生锁文件。</summary>
    public static async Task<IDisposable> AcquireCompilationLockAsync(string root, string artifactHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root); ScriptCompilationInput.ValidateHash(artifactHash);
        return await AcquireLockAsync(root, "compile-" + artifactHash[..2], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按最旧目录先淘汰，跳过跨进程在用租约；不能满足配额时明确失败。</summary>
    public static void Prune(string root, long maximumCacheBytes = DefaultMaximumCacheBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCacheBytes);
        Directory.CreateDirectory(root); using var gate = AcquireLock(root, "cache", cancellationToken);
        CleanupStaging(root, cancellationToken);
        PruneCore(root, maximumCacheBytes, 0, cancellationToken);
    }

    private static ScriptCompilationResult LoadCore(string directory, ScriptArtifactBudget budget)
    {
        ValidateFileClosure(directory);
        byte[] manifestBytes = ReadBounded(Path.Combine(directory, "build.json"), budget.MaximumManifestBytes);
        try
        {
            using var document = JsonDocument.Parse(manifestBytes); RejectDuplicateFields(document.RootElement);
            var manifest = JsonSerializer.Deserialize<ArtifactManifest>(manifestBytes, Json) ?? throw new InvalidDataException("脚本产物清单为空。");
            if (manifest.Format != 3 || manifest.Descriptor is not { } descriptor) throw new InvalidDataException("脚本产物格式或完整身份无效。");
            descriptor.Validate(budget); ValidateDiagnostics(manifest.Diagnostics);
            ImmutableArray<byte> pe = [.. ReadBounded(Path.Combine(directory, "effect.dll"), budget.MaximumPeBytes, descriptor.PeBytes)];
            ImmutableArray<byte> pdb = [.. ReadBounded(Path.Combine(directory, "effect.pdb"), budget.MaximumPdbBytes, descriptor.PdbBytes)];
            var result = new ScriptCompilationResult(descriptor.ArtifactHash, pe, pdb, manifest.Diagnostics) { Descriptor = descriptor };
            descriptor.ValidateResult(result, budget); return result;
        }
        catch (JsonException error) { throw new InvalidDataException("脚本产物清单无效。", error); }
    }
    private static void ValidateExisting(string directory, ScriptCompilationResult expected, ScriptArtifactBudget budget)
    {
        var actual = LoadCore(directory, budget);
        if (actual.ArtifactHash != expected.ArtifactHash || actual.Descriptor!.Identity != expected.Descriptor!.Identity
            || !actual.Descriptor.EntryTypes.SequenceEqual(expected.Descriptor.EntryTypes)
            || !actual.PeImage.AsSpan().SequenceEqual(expected.PeImage.AsSpan()) || !actual.PdbImage.AsSpan().SequenceEqual(expected.PdbImage.AsSpan())
            || !actual.Diagnostics.SequenceEqual(expected.Diagnostics)) throw new InvalidDataException("脚本缓存目录与请求的固定编译代次不一致。");
    }
    private static byte[] ReadBounded(string path, long maximum, long? expected = null)
    {
        RejectLink(path); using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > maximum || expected is { } length && stream.Length != length)
            throw new InvalidDataException("脚本产物文件超过预算或声明长度不符。");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || stream.Length != bytes.LongLength) throw new InvalidDataException("脚本产物文件读取时已改变。");
        return bytes;
    }
    private static void WriteFresh(string path, ReadOnlySpan<byte> bytes)
    {
        RejectLink(path);
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }
    private static void VerifyWritten(string path, ReadOnlySpan<byte> expected, long maximum, CancellationToken token)
    {
        RejectLink(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximum || stream.Length != expected.Length)
            throw new InvalidDataException("脚本产物写入长度超过预算或与固定字节不符。");
        Span<byte> buffer = stackalloc byte[4096];
        for (int offset = 0; offset < expected.Length;)
        {
            token.ThrowIfCancellationRequested();
            int length = Math.Min(buffer.Length, expected.Length - offset);
            stream.ReadExactly(buffer[..length]);
            if (!buffer[..length].SequenceEqual(expected.Slice(offset, length)))
                throw new InvalidDataException("脚本产物写入字节与固定产物不符。");
            offset += length;
        }
        if (stream.ReadByte() != -1 || stream.Length != expected.Length)
            throw new InvalidDataException("脚本产物验证写入时已改变。");
    }
    private static void ValidateFileClosure(string directory)
    {
        RejectLink(directory);
        if (Directory.EnumerateDirectories(directory).Any()
            || !Directory.EnumerateFiles(directory).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)
                .SequenceEqual(ArtifactFileNames))
            throw new InvalidDataException("脚本产物目录不符合固定三文件闭包。");
    }
    private static void ValidateDiagnostics(ImmutableArray<ScriptCompileDiagnostic> diagnostics)
    {
        if (diagnostics.IsDefault || diagnostics.Any(d => d is null || d.IsError || string.IsNullOrWhiteSpace(d.Code)
            || d.Message is null || d.Line < 0 || d.Column < 0)) throw new InvalidDataException("脚本产物诊断无效。");
    }
    private static void PruneCore(string root, long maximum, long reserve, CancellationToken token)
    {
        var directories = Directory.EnumerateDirectories(root).Where(path => Path.GetFileName(path) is { Length: 64 } name
            && name.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f')).Select(path => new DirectoryInfo(path)).OrderBy(d => d.CreationTimeUtc).ThenBy(d => d.Name, StringComparer.Ordinal).ToArray();
        long total = 0; Dictionary<string, long> sizes = [];
        foreach (var directory in directories)
        {
            token.ThrowIfCancellationRequested(); RejectLink(directory.FullName);
            if (Directory.EnumerateDirectories(directory.FullName).Any()) throw new InvalidDataException("脚本产物目录含有未授权子目录。");
            long size = Directory.EnumerateFiles(directory.FullName, "*", SearchOption.TopDirectoryOnly).Sum(path => { RejectLink(path); return new FileInfo(path).Length; });
            sizes.Add(directory.Name, size); total = checked(total + size);
        }
        if (reserve > maximum) throw new IOException("脚本产物超过持久缓存配额。");
        foreach (var directory in directories)
        {
            if (total <= maximum - reserve) return;
            token.ThrowIfCancellationRequested();
            FileStream lease;
            try { lease = OpenLease(root, directory.Name, FileShare.None); }
            catch (IOException error) when (IsSharingViolation(error)) { continue; }
            using (lease) { Directory.Delete(directory.FullName, recursive: true); total -= sizes[directory.Name]; }
            // All lease openers hold the cache gate, so after exclusive close no opener can race file deletion.
            File.Delete(LeasePath(root, directory.Name));
        }
        if (total > maximum - reserve) throw new IOException("脚本缓存在用租约导致配额不足。");
    }
    private static void CleanupStaging(string root, CancellationToken token)
    {
        foreach (string directory in Directory.EnumerateDirectories(root, ".build-*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(directory);
            if (name.Length != 39 || !Guid.TryParseExact(name.AsSpan(7), "N", out _)) continue;
            token.ThrowIfCancellationRequested(); RejectLink(directory);
            // Publish owns the same cache gate for its entire staging lifetime; no live compliant writer can be here.
            if (Directory.EnumerateDirectories(directory).Any()) throw new InvalidDataException("脚本暂存目录含有异常子目录。");
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                RejectLink(path);
                if (!ArtifactFileNames.Contains(Path.GetFileName(path), StringComparer.Ordinal))
                    throw new InvalidDataException("脚本暂存目录包含未知文件；拒绝清理非本存储闭包。");
            }
            Directory.Delete(directory, recursive: true);
        }
    }
    private static FileStream OpenLease(string root, string hash, FileShare share)
    {
        string folder = Path.Combine(root, ".locks"); Directory.CreateDirectory(folder);
        string path = LeasePath(root, hash); RejectLink(path);
        return new(path, FileMode.OpenOrCreate, FileAccess.Read, share);
    }
    private static string LeasePath(string root, string hash) => Path.Combine(root, ".locks", "lease-" + hash);
    private static FileStream AcquireLock(string root, string name, CancellationToken token)
        => AcquireLockAsync(root, name, token).GetAwaiter().GetResult();
    private static async Task<FileStream> AcquireLockAsync(string root, string name, CancellationToken token)
    {
        string folder = Path.Combine(root, ".locks"); Directory.CreateDirectory(folder); RejectLink(folder);
        string path = Path.Combine(folder, name); RejectLink(path);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when (IsSharingViolation(error)) { await Task.Delay(20, token).ConfigureAwait(false); }
        }
    }
    private static bool IsSharingViolation(IOException error) => (error.HResult & 0xffff) is 11 or 32 or 33;
    private static void RejectLink(string path)
    {
        FileSystemInfo? item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        while (item is not null)
        {
            if (item.LinkTarget is not null) throw new InvalidDataException("脚本产物路径不允许符号链接。");
            item = item is DirectoryInfo directory ? directory.Parent : ((FileInfo)item).Directory;
        }
    }
    private static void RejectDuplicateFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject()) { if (!keys.Add(field.Name)) throw new InvalidDataException("脚本产物清单字段重复。"); RejectDuplicateFields(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateFields(item);
    }
    private sealed record ArtifactManifest(int Format, ScriptArtifactDescriptor Descriptor, ImmutableArray<ScriptCompileDiagnostic> Diagnostics);
}
