using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>完整包目录的固定读取与内容地址发布，不执行源码。</summary>
public static class EffectPackageStorage
{
    /// <summary>逐文件刷盘后发布不可变包目录；已存在时只验证，不覆写。</summary>
    public static void Publish(string directory, EffectPackageContents contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory); ArgumentNullException.ThrowIfNull(contents);
        EnsureNoLinks(directory);
        string target = Path.GetFullPath(directory); string parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent); EnsureNoLinks(target);
        if (Directory.Exists(target)) { Verify(target, contents); return; }
        string temporary = Path.Combine(parent, ".package-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(temporary);
            Write("manifest.json", contents.Manifest.ToArray());
            foreach (var file in contents.Files) Write(file.Path, file.Content.ToArray());
            Verify(temporary, contents); EnsureNoLinks(target);
            try { Directory.Move(temporary, target); }
            catch (IOException) when (Directory.Exists(target)) { EnsureNoLinks(target); Verify(target, contents); }
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
        void Write(string relative, byte[] bytes)
        {
            string file = Path.Combine(temporary, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes); stream.Flush(flushToDisk: true);
        }
    }
    private static void Verify(string directory, EffectPackageContents expected)
    {
        var actual = ReadDirectoryAsync(directory, expected.Profile).GetAwaiter().GetResult();
        if (actual.PackageId != expected.PackageId || actual.Revision != expected.Revision)
            throw new EffectPackageException("EffectPackageContentMismatch", directory, "包内容地址已被不同内容占用，拒绝覆写。");
    }
    private static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null)
                throw new EffectPackageException("PackageLinkRejected", path, "包路径不能经过文件或目录链接。");
    }
    /// <summary>固定目录全部字节，严格验证声明、预算、链接和内容。</summary>
    public static Task<EffectPackageContents> ReadDirectoryAsync(string directory, EffectPackageProfile expected, EffectPackageContentBudget? budget = null,
        CancellationToken cancellationToken = default)
        => ReadDirectoryWithUsageAsync(directory, expected, budget, null, cancellationToken);

    /// <summary>复用一次闭包的总预算账，在分配每个实际文件前同时登记单包和闭包预算。</summary>
    public static async Task<EffectPackageContents> ReadDirectoryWithUsageAsync(string directory, EffectPackageProfile expected, EffectPackageContentBudget? budget,
        EffectPackageContentUsage? aggregate, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory); ArgumentNullException.ThrowIfNull(expected);
        cancellationToken.ThrowIfCancellationRequested(); EnsureNoLinks(directory); RejectLink(directory, "package");
        EffectPackageContentUsage usage = new(budget);
        Dictionary<string, ReadOnlyMemory<byte>> files = new(StringComparer.Ordinal);
        List<string> directories = [];
        await Read(directory, "").ConfigureAwait(false);
        EffectPackageContents.ValidatePaths(files.Keys, directories);
        return Complete(files, expected, budget, cancellationToken);

        async Task Read(string physical, string prefix)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(physical).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string logical = prefix + Path.GetFileName(entry); EffectPackageContents.ValidatePath(logical); RejectLink(entry, logical);
                if (Directory.Exists(entry)) { directories.Add(logical); await Read(entry, logical + "/").ConfigureAwait(false); }
                else
                {
                    using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                    long length = stream.Length; usage.Claim(logical, length); aggregate?.Claim(logical, length);
                    files.Add(logical, await ReadFixedAsync(stream, length, logical, cancellationToken).ConfigureAwait(false));
                }
            }
        }
    }

    private static EffectPackageContents Complete(Dictionary<string, ReadOnlyMemory<byte>> files, EffectPackageProfile expected, EffectPackageContentBudget? budget,
        CancellationToken token)
    {
        if (!files.Remove("manifest.json", out var manifest))
            throw new EffectPackageException("PackageManifestMissing", "manifest.json", "包必须有根清单。");
        return EffectPackageContents.Create(manifest, files, expected, budget, token);
    }
    private static void RejectLink(string physical, string logical)
    {
        if ((File.GetAttributes(physical) & FileAttributes.ReparsePoint) != 0)
            throw new EffectPackageException("PackageLinkRejected", logical, "包目录及资源不能为链接。");
    }
    private static async Task<byte[]> ReadFixedAsync(Stream stream, long length, string logical, CancellationToken token)
    {
        byte[] bytes = new byte[(int)length];
        try
        {
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
                throw new EffectPackageException("PackageFileChanged", logical, "资源实际长度与读取前的声明不一致。");
        }
        catch (EndOfStreamException error) { throw new EffectPackageException("PackageFileChanged", logical, "资源实际长度短于读取前的声明。", error); }
        return bytes;
    }
}
