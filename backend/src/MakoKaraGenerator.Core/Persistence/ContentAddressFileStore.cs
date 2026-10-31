using System.Security.Cryptography;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>受管文件的流式固定、校验和不可变发布。</summary>
internal static class ContentAddressFileStore
{
    internal static (string Hash, long Length) Inspect(string path, long maximum)
    {
        EnsureNoLinks(path);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        CheckSize(input.Length, maximum);
        long length = input.Length; string hash = Convert.ToHexStringLower(SHA256.HashData(input));
        if (input.Position != length || input.Length != length) throw new IOException("资源在读取期间改变长度。");
        return (hash, length);
    }
    internal static MediaFileRevision PublishMedia(string source, string directory, MediaFileRevision? expected, ProjectStoreLimits limits)
    {
        source = Path.GetFullPath(source); var before = Inspect(source, limits.MaximumMediaBytes);
        if (expected is not null && (before.Hash != expected.Hash || before.Length != expected.ByteLength)) throw new IOException("媒体源已偏离固定修订，未发布新入口。请重新选择或接受新源。");
        var revision = expected ?? new MediaFileRevision(before.Hash, before.Length, Path.GetFileName(source));
        string target = Path.Combine(directory, revision.RelativePath); EnsureNoLinks(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) { Verify(target, before.Hash, before.Length, limits.MaximumMediaBytes); return revision; }
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            {
                byte[] buffer = new byte[65536]; long copied = 0; int count;
                while ((count = input.Read(buffer)) != 0) { copied = checked(copied + count); CheckSize(copied, limits.MaximumMediaBytes); output.Write(buffer, 0, count); }
                output.Flush(flushToDisk: true);
            }
            Verify(temporary, before.Hash, before.Length, limits.MaximumMediaBytes);
            Verify(source, before.Hash, before.Length, limits.MaximumMediaBytes);
            Publish(temporary, target, () => Verify(target, before.Hash, before.Length, limits.MaximumMediaBytes));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return revision;
    }
    internal static void PublishBytes(string directory, string relative, byte[] bytes, long maximum)
    {
        CheckSize(bytes.LongLength, maximum); string target = Path.Combine(directory, relative); EnsureNoLinks(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (File.Exists(target)) { Verify(target, hash, bytes.Length, maximum); return; }
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(flushToDisk: true); }
            Verify(temporary, hash, bytes.Length, maximum); Publish(temporary, target, () => Verify(target, hash, bytes.Length, maximum));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Publish(string temporary, string target, Action verify)
    {
        EnsureNoLinks(target);
        try { File.Move(temporary, target, overwrite: false); }
        catch (IOException) when (File.Exists(target)) { EnsureNoLinks(target); verify(); }
    }
    internal static void Verify(string path, string hash, long? length, long maximum)
    {
        var actual = Inspect(path, maximum);
        if (actual.Hash != hash || length is { } expected && actual.Length != expected) throw new IOException("受管资源的实际hash／长度与内容地址不符，拒绝覆写或使用。");
    }
    internal static byte[] ReadBytes(string path, string hash, int maximum)
    {
        EnsureNoLinks(path);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read); CheckSize(input.Length, maximum);
        byte[] bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash) throw new IOException("分析资源的实际内容与修订地址不符。");
        return bytes;
    }
    internal static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null) throw new IOException("受管资源路径不能经过文件或目录链接。");
    }
    private static void CheckSize(long length, long maximum)
    { if (maximum <= 0 || length < 0 || length > maximum) throw new IOException("资源超过显式持久IO限额，未裁切或减少数据。"); }
}
