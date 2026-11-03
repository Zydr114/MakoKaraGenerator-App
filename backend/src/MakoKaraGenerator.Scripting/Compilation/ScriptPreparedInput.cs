using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Scripting.Compilation;

/// <summary>固定失败编译；没有 PE，保留本次源码和入口的全部诊断。</summary>
public sealed record ScriptFailedCompilation(string ArtifactHash, ScriptCompilationIdentity Identity,
    ImmutableArray<string> EntryTypes, ImmutableArray<ScriptCompileDiagnostic> Diagnostics)
{
    /// <summary>只接受真实错误、规范入口与完整编译身份。</summary>
    public void Validate()
    {
        if (Identity is null) throw new InvalidDataException("固定失败编译缺少身份。");
        ScriptCompilationInput.ValidateHash(ArtifactHash); Identity.Validate();
        if (EntryTypes.IsDefault || EntryTypes.Any(string.IsNullOrWhiteSpace)
            || !EntryTypes.SequenceEqual(EntryTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            || Diagnostics.IsDefaultOrEmpty || Diagnostics.Length > 4096 || !Diagnostics.Any(d => d is { IsError: true })
            || Diagnostics.Any(d => d is null || string.IsNullOrWhiteSpace(d.Code) || d.Code.Length > 128 || d.Message is null
                || d.Message.Length > 65536 || d.Path is { Length: > 1024 } || d.Line < 0 || d.Column < 0
                || d.Path is null && (d.Line != 0 || d.Column != 0) || d.Path is not null && (d.Line == 0 || d.Column == 0)))
            throw new InvalidDataException("固定失败编译身份或诊断无效。");
        if (JsonSerializer.SerializeToUtf8Bytes(this).LongLength > ScriptArtifactBudget.Default.MaximumManifestBytes)
            throw new InvalidDataException("固定失败编译诊断超过清单预算。");
    }

    /// <summary>诊断位置只允许属于这次固定源码，并保持真实行列范围。</summary>
    public void ValidateSources(ScriptCompilationInput input)
    {
        Validate();
        if (Identity != input.Identity || ArtifactHash != input.ArtifactHash || !EntryTypes.SequenceEqual(input.EntryTypes))
            throw new InvalidDataException("固定失败诊断不属于请求源码或入口。");
        var sources = input.Sources.ToDictionary(source => source.Path, StringComparer.Ordinal);
        foreach (var diagnostic in Diagnostics)
        {
            if (diagnostic.Path is null) continue;
            if (!sources.TryGetValue(diagnostic.Path, out var source)) throw new InvalidDataException("固定失败诊断引用了其他源码路径。");
            string[] lines = source.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split(['\r', '\n', '\u0085', '\u2028', '\u2029']);
            if (diagnostic.Line > lines.Length || diagnostic.Column > lines[diagnostic.Line - 1].Length + 1)
                throw new InvalidDataException("固定失败诊断行列超出源码范围。");
        }
    }
}

/// <summary>Host 生成的私有输入目录；生命周期必须覆盖 Worker 的关闭或取消回执。</summary>
public sealed class ScriptPreparedInputLease : IDisposable
{
    private readonly object _gate = new();
    private string? _root;
    internal ScriptPreparedInputLease(string root, byte[] envelope, long copiedArtifactBytes, string sourceSha256)
    { _root = root; Envelope = envelope; CopiedArtifactBytes = copiedArtifactBytes; SourceSha256 = sourceSha256; }
    /// <summary>原始来源摘要；内部授权目录变化不改变业务来源身份。</summary>
    public string SourceSha256 { get; }
    /// <summary>仅通过私有 pipe 发送的授权封包；不内嵌 PE、PDB 或原始源码。</summary>
    public byte[] Envelope { get; }
    /// <summary>本次实际固定的 PE、PDB、清单总字节。</summary>
    public long CopiedArtifactBytes { get; }
    /// <summary>Worker 已停止消费后移除本次目录。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_root is not { } root) return;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            _root = null;
        }
    }
}

/// <summary>Worker 读取完成的原始输入与固定编译提供者。</summary>
public sealed record ScriptPreparedInput(byte[] Source, FixedScriptArtifactProvider Compiler);

/// <summary>保留原始输入预算的独立内部封包；原始解码器和业务来源 profile 保持不变。</summary>
public static class ScriptPreparedInputCodec
{
    /// <summary>原始输入独立预算，不挤占既有私有 pipe 的 4 MiB 预算。</summary>
    public const int MaximumSourceBytes = 4 * 1024 * 1024;
    /// <summary>一次固定拷贝所有产物的总预算。</summary>
    public const long MaximumCopiedArtifactBytes = 256L * 1024 * 1024;
    private static readonly byte[] Magic = "MKSI1\0\0\0"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, MaxDepth = 24 };
    private sealed record PreparedArtifact(string Path, ScriptArtifactDescriptor Descriptor, string ManifestSha256);
    private sealed record Envelope(string Root, string SourcePath, long SourceBytes, string SourceSha256,
        ScriptCompilationIdentity Identity, PreparedArtifact[] Artifacts, ScriptFailedCompilation[] Failures, long CopiedArtifactBytes);

    /// <summary>判定封包，不将普通来源字节误解为文件授权。</summary>
    public static bool IsPrepared(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Magic);

    /// <summary>固定原始字节及编译结果；失败只保存诊断，不发布可执行产物。</summary>
    public static ScriptPreparedInputLease Prepare(ReadOnlyMemory<byte> source, string parentRoot,
        ScriptCompilationIdentity identity, IEnumerable<(ScriptCompilationInput Input, ScriptCompilationResult Result)> compilations,
        CancellationToken cancellationToken = default)
    {
        if (source.Length is <= 0 or > MaximumSourceBytes) throw new InvalidDataException("固定原始输入超过字节预算。");
        ArgumentNullException.ThrowIfNull(compilations); identity.Validate();
        var fixedCompilations = compilations.ToArray();
        long plannedBytes = 0;
        HashSet<string> planned = [];
        foreach (var (input, result) in fixedCompilations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Succeeded && planned.Add(input.ArtifactHash))
            {
                long bytes = ScriptArtifactStore.MeasurePublicationBytes(result);
                if (bytes > MaximumCopiedArtifactBytes - plannedBytes) throw new InvalidDataException("固定产物拷贝超过总预算。");
                plannedBytes += bytes;
            }
        }
        string root = Path.Combine(Path.GetFullPath(parentRoot), "input-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            EnsureNoLinks(root); File.WriteAllBytes(Path.Combine(root, "source.bin"), source.Span);
            List<PreparedArtifact> artifacts = []; List<ScriptFailedCompilation> failures = []; HashSet<string> seen = [];
            long copied = 0;
            foreach (var (input, result) in fixedCompilations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (input.Identity != identity || result.ArtifactHash != input.ArtifactHash) throw new InvalidDataException("固定编译结果身份不匹配。");
                if (!seen.Add(input.ArtifactHash)) continue;
                if (result.Succeeded)
                {
                    if (result.Descriptor is not { } descriptor || descriptor.Identity != identity || !descriptor.EntryTypes.SequenceEqual(input.EntryTypes))
                        throw new InvalidDataException("固定编译产物缺少完整身份。");
                    long publicationBytes = ScriptArtifactStore.MeasurePublicationBytes(result);
                    if (publicationBytes > MaximumCopiedArtifactBytes - copied) throw new InvalidDataException("固定产物拷贝超过总预算。");
                    // This root belongs to one prepared closure; never evict an earlier member while copying the next.
                    string directory = ScriptArtifactStore.Publish(Path.Combine(root, "scripts"), result, maximumCacheBytes: long.MaxValue,
                        cancellationToken: cancellationToken);
                    long length = Directory.EnumerateFiles(directory).Sum(path => new FileInfo(path).Length);
                    copied = checked(copied + length);
                    if (copied > MaximumCopiedArtifactBytes) throw new InvalidDataException("固定产物拷贝超过总预算。");
                    artifacts.Add(new("scripts/" + input.ArtifactHash, descriptor, ScriptArtifactStore.PublicationManifestHash(result)));
                }
                else
                {
                    if (!result.PeImage.IsDefaultOrEmpty || !result.PdbImage.IsDefaultOrEmpty || result.Descriptor is not null)
                        throw new InvalidDataException("失败编译不能携带可执行产物。");
                    var failure = new ScriptFailedCompilation(input.ArtifactHash, identity, input.EntryTypes, result.Diagnostics);
                    failure.ValidateSources(input); failures.Add(failure);
                }
            }
            var envelope = new Envelope(root, "source.bin", source.Length, Hash(source.Span), identity, [.. artifacts], [.. failures], copied);
            byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
            if (metadata.Length > ScriptArtifactBudget.Default.MaximumManifestBytes) throw new InvalidDataException("固定输入清单超过字节预算。");
            byte[] bytes = new byte[Magic.Length + metadata.Length]; Magic.CopyTo(bytes, 0); metadata.CopyTo(bytes, Magic.Length);
            cancellationToken.ThrowIfCancellationRequested(); return new(root, bytes, copied, envelope.SourceSha256);
        }
        catch { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); throw; }
    }

    /// <summary>在任何源解码与程序集装载前验证授权根、完整原始字节和实际产物。</summary>
    public static ScriptPreparedInput Read(ReadOnlyMemory<byte> bytes, ScriptCompilationIdentity expectedIdentity,
        CancellationToken cancellationToken = default)
    {
        if (!IsPrepared(bytes.Span) || bytes.Length <= Magic.Length || bytes.Length - Magic.Length > ScriptArtifactBudget.Default.MaximumManifestBytes)
            throw new InvalidDataException("固定输入封包无效或超过预算。");
        expectedIdentity.Validate();
        Envelope metadata;
        try
        {
            using var document = JsonDocument.Parse(bytes[Magic.Length..]); RejectDuplicateFields(document.RootElement);
            metadata = JsonSerializer.Deserialize<Envelope>(bytes.Span[Magic.Length..], Json) ?? throw new InvalidDataException("固定输入清单为空。");
        }
        catch (JsonException error) { throw new InvalidDataException("固定输入清单无效。", error); }
        if (metadata.Identity != expectedIdentity || string.IsNullOrWhiteSpace(metadata.Root) || !Path.IsPathFullyQualified(metadata.Root)
            || Path.GetFullPath(metadata.Root) != metadata.Root || metadata.SourcePath != "source.bin"
            || metadata.SourceBytes is <= 0 or > MaximumSourceBytes || metadata.Artifacts is null || metadata.Failures is null
            || metadata.Artifacts.Length + metadata.Failures.Length > 1024 || metadata.CopiedArtifactBytes is < 0 or > MaximumCopiedArtifactBytes)
            throw new InvalidDataException("固定输入身份、路径或预算无效。");
        EnsureNoLinks(metadata.Root); ScriptCompilationInput.ValidateHash(metadata.SourceSha256);
        string sourcePath = Resolve(metadata.Root, metadata.SourcePath); EnsureNoLinks(sourcePath);
        byte[] source;
        using (FileStream stream = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length != metadata.SourceBytes) throw new InvalidDataException("固定原始输入长度已改变。");
            source = new byte[checked((int)metadata.SourceBytes)]; stream.ReadExactly(source);
            if (stream.ReadByte() != -1 || stream.Length != metadata.SourceBytes || Hash(source) != metadata.SourceSha256)
                throw new InvalidDataException("固定原始输入摘要已改变。");
        }
        List<ScriptCompilationResult> artifacts = []; HashSet<string> seen = []; long copied = 0;
        foreach (var prepared in metadata.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (prepared is null || prepared.Descriptor is null) throw new InvalidDataException("固定产物缺少可信描述。");
            string path = prepared.Path;
            if (path is null || !path.StartsWith("scripts/", StringComparison.Ordinal)) throw new InvalidDataException("固定产物授权路径无效。");
            string hash = path[8..]; ScriptCompilationInput.ValidateHash(hash);
            if (!seen.Add(hash)) throw new InvalidDataException("固定产物授权身份重复。");
            string directory = Resolve(metadata.Root, path); EnsureNoLinks(directory);
            if (prepared.Descriptor.ArtifactHash != hash || prepared.Descriptor.Identity != expectedIdentity)
                throw new InvalidDataException("固定产物授权与可信描述不符。");
            var (artifact, length) = ScriptArtifactStore.LoadPrepared(directory, prepared.Descriptor, prepared.ManifestSha256,
                Math.Min(MaximumCopiedArtifactBytes, metadata.CopiedArtifactBytes) - copied);
            copied = checked(copied + length);
            if (copied > MaximumCopiedArtifactBytes) throw new InvalidDataException("固定产物拷贝超过总预算。");
            artifacts.Add(artifact);
        }
        if (copied != metadata.CopiedArtifactBytes) throw new InvalidDataException("固定产物拷贝字节与清单不符。");
        foreach (var failure in metadata.Failures)
        {
            if (failure is null || failure.Identity != expectedIdentity || !seen.Add(failure.ArtifactHash)) throw new InvalidDataException("固定失败编译身份重复或不匹配。");
            failure.Validate();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(source, new(expectedIdentity, artifacts, failures: metadata.Failures));
    }

    private static string Resolve(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':')
            || path.Split('/').Any(part => part is "" or "." or "..")) throw new InvalidDataException("固定输入路径越界。");
        string full = Path.GetFullPath(Path.Combine(root, path));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("固定输入路径越界。");
        return full;
    }
    private static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static void EnsureNoLinks(string path)
    {
        FileSystemInfo? item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        while (item is not null)
        {
            if (!item.Exists || item.LinkTarget is not null) throw new InvalidDataException("固定输入路径不存在或含符号链接。");
            item = item is DirectoryInfo directory ? directory.Parent : ((FileInfo)item).Directory;
        }
    }
    private static void RejectDuplicateFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("固定输入清单包含重复字段。"); RejectDuplicateFields(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) RejectDuplicateFields(value);
    }
}
