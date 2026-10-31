using System.IO.Compression;
using System.Text;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Application;

/// <summary>可编辑目录与 .mkfx 的完整内容传输；不自动改工程、编译或删除用户资源。</summary>
public static class EffectPackageTransfer
{
    /// <summary>固定目录所有字节并验证闭包；链接和未声明文件不被跟随或忽略。</summary>
    public static Task<EffectPackageContents> ReadDirectoryAsync(string directory, EffectPackageProfile expected, EffectPackageContentBudget? budget = null,
        CancellationToken cancellationToken = default)
        => MakoKaraGenerator.Core.Persistence.EffectPackageStorage.ReadDirectoryAsync(directory, expected, budget, cancellationToken);

    /// <summary>直接固定 ZIP 条目；不先解压，也不使用外部目录补缺资源。</summary>
    public static async Task<EffectPackageContents> ReadArchiveAsync(string path, EffectPackageProfile expected, EffectPackageContentBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        try { return await ReadArchiveCoreAsync(path, expected, budget, cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException exception)
        { throw new EffectPackageException("PackageArchiveInvalid", "package.mkfx", "效果包不是完整有效的 ZIP。", exception); }
    }

    private static async Task<EffectPackageContents> ReadArchiveCoreAsync(string path, EffectPackageProfile expected, EffectPackageContentBudget? budget,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); ArgumentNullException.ThrowIfNull(expected);
        cancellationToken.ThrowIfCancellationRequested(); RejectLink(path, "package.mkfx");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, false, Encoding.UTF8);
        EffectPackageContentUsage usage = new(budget);
        Dictionary<string, ReadOnlyMemory<byte>> files = new(StringComparer.Ordinal);
        HashSet<string> paths = new(StringComparer.Ordinal);
        List<string> directories = [];
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = entry.FullName;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new EffectPackageException("PackageLinkRejected", name, "包内链接不能作为受管资源。");
            bool directory = name.EndsWith('/');
            string logical = directory ? name[..^1] : name; EffectPackageContents.ValidatePath(logical);
            if (!paths.Add(name)) throw new EffectPackageException("PackageFileDuplicate", logical, "ZIP 中有重复条目。");
            if (directory)
            {
                if (entry.Length != 0) throw new EffectPackageException("PackageDirectoryHasContent", logical, "目录条目不能藏有内容。");
                directories.Add(logical);
                continue;
            }
            usage.Claim(logical, entry.Length);
            using var stream = entry.Open();
            files.Add(logical, await ReadFixedAsync(stream, entry.Length, logical, cancellationToken).ConfigureAwait(false));
        }
        EffectPackageContents.ValidatePaths(files.Keys, directories);
        return Complete(files, expected, budget, cancellationToken);
    }

    /// <summary>分享完整源码／资源，成功后一次发布；失败或取消保留原目标。</summary>
    public static async Task WriteArchiveAsync(string path, EffectPackageContents contents, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); ArgumentNullException.ThrowIfNull(contents);
        cancellationToken.ThrowIfCancellationRequested();
        string target = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".mkfx-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
                {
                    await Write(archive, "manifest.json", contents.Manifest.ToArray(), cancellationToken).ConfigureAwait(false);
                    foreach (var file in contents.Files)
                        await Write(archive, file.Path, file.Content.ToArray(), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task Write(ZipArchive archive, string path, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); entry.ExternalAttributes = 0;
        using var stream = entry.Open(); await stream.WriteAsync(bytes, token).ConfigureAwait(false);
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
