using System.IO.Compression;
using System.Text;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application;

/// <summary>完整模板及包资源的.mktpl分享；没有实例、DLL或机器路径。</summary>
public static class StyleTemplateTransfer
{
    /// <summary>原子分享固定目录闭包的同一份ZIP；失败／取消保持旧文件。</summary>
    public static async Task WriteArchiveAsync(string path, IEnumerable<StyleTemplate> templates, EffectPackageContentBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); ArgumentNullException.ThrowIfNull(templates); cancellationToken.ThrowIfCancellationRequested();
        var items = templates.ToArray(); var bytes = StyleTemplateLibrary.Encode(items); var usage = new EffectPackageContentUsage(budget);
        string target = Path.GetFullPath(path), temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
                {
                    await Write(StyleTemplateLibrary.FileName, bytes).ConfigureAwait(false);
                    foreach (var package in StyleTemplateLibrary.Packages(items))
                    {
                        string prefix = StyleTemplateLibrary.PackagePath(package.Revision) + "/";
                        await Write(prefix + "manifest.json", package.Manifest.ToArray()).ConfigureAwait(false);
                        foreach (var file in package.Files) await Write(prefix + file.Path, file.Content.ToArray()).ConfigureAwait(false);
                    }
                    async Task Write(string logical, byte[] data)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); usage.Claim(logical, data.Length);
                        var entry = archive.CreateEntry(logical, CompressionLevel.Optimal); entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); entry.ExternalAttributes = 0;
                        using var destination = entry.Open(); await destination.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                    }
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(flushToDisk: true);
            }
            var verified = await ReadArchiveAsync(temporary, budget, cancellationToken).ConfigureAwait(false);
            if (!StyleTemplateLibrary.Encode(verified).AsSpan().SequenceEqual(bytes)) throw new IOException("模板分享核验不一致。");
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(target)) throw new IOException($"目标路径是目录，无法覆盖为文件：{target}");
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>严格读取完整ZIP，所有资源在分配前计费；不执行作者。</summary>
    public static async Task<IReadOnlyList<StyleTemplate>> ReadArchiveAsync(string path, EffectPackageContentBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); cancellationToken.ThrowIfCancellationRequested();
        using var archive = ZipFile.OpenRead(path); var usage = new EffectPackageContentUsage(budget);
        Dictionary<string, ReadOnlyMemory<byte>> files = new(StringComparer.Ordinal); List<string> directories = [];
        HashSet<string> entries = new(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested(); string name = entry.FullName;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new EffectPackageException("PackageLinkRejected", name, "模板ZIP不能含链接。");
            bool directory = name.EndsWith('/'); string logical = directory ? name[..^1] : name; EffectPackageContents.ValidatePath(logical);
            if (!entries.Add(name)) throw new EffectPackageException("PackageFileDuplicate", name, "模板ZIP条目重复。");
            if (directory) { if (entry.Length != 0) throw new IOException("模板ZIP目录不能包含字节。"); directories.Add(logical); continue; }
            usage.Claim(logical, entry.Length); byte[] data = new byte[(int)entry.Length];
            using var source = entry.Open(); await source.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            if (source.ReadByte() != -1) throw new IOException("模板ZIP长度不符。"); files.Add(logical, data);
        }
        EffectPackageContents.ValidatePaths(files.Keys, directories);
        if (!files.TryGetValue(StyleTemplateLibrary.FileName, out var index)) throw new IOException("模板ZIP缺少index.json。");
        var loaded = StyleTemplateLibrary.Deserialize(new UTF8Encoding(false, true).GetString(index.Span), relative =>
        {
            string prefix = relative + "/";
            if (!files.TryGetValue(prefix + "manifest.json", out var manifest)) throw new IOException("模板ZIP缺少包清单。");
            return EffectPackageContents.CreateSupported(manifest, files.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) && pair.Key != prefix + "manifest.json")
                .ToDictionary(pair => pair.Key[prefix.Length..], pair => pair.Value, StringComparer.Ordinal), budget, cancellationToken);
        });
        if (loaded.Issues.Count != 0) throw new IOException(string.Join("；", loaded.Issues));
        var allowed = new HashSet<string>(StringComparer.Ordinal) { StyleTemplateLibrary.FileName };
        foreach (var package in StyleTemplateLibrary.Packages(loaded.Templates))
        {
            string prefix = StyleTemplateLibrary.PackagePath(package.Revision) + "/"; allowed.Add(prefix + "manifest.json");
            foreach (var file in package.Files) allowed.Add(prefix + file.Path);
        }
        if (!allowed.SetEquals(files.Keys) || directories.Any(dir => !allowed.Any(file => file.StartsWith(dir + "/", StringComparison.Ordinal))))
            throw new IOException("模板ZIP含未声明文件或无归属目录。");
        return loaded.Templates;
    }
}
