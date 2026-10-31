using System.Security.Cryptography;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>实际索引字节的修订；缺失索引与已经写出的空库具有不同身份。</summary>
public readonly record struct LibraryIndexRevision
{
    private LibraryIndexRevision(string hash) => Sha256 = hash;
    /// <summary>索引的SHA-256；缺失索引为null。</summary>
    public string? Sha256 { get; }
    /// <summary>尚未发布索引的修订。</summary>
    public static LibraryIndexRevision Missing => default;
    /// <summary>由已经读取的真实字节取得修订，不重新读盘。</summary>
    public static LibraryIndexRevision FromBytes(ReadOnlySpan<byte> bytes) => new(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    /// <summary>只读当前索引修订；损坏内容仍有字节身份，链接与读取失败拒绝。</summary>
    public static LibraryIndexRevision Read(string path, int maximumBytes)
    {
        byte[]? bytes = ReadBytes(path, maximumBytes);
        return bytes is null ? Missing : FromBytes(bytes);
    }
    internal static byte[]? ReadBytes(string path, int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ContentAddressFileStore.EnsureNoLinks(path);
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximumBytes) throw new IOException("库索引超过显式字节限额。");
            byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new IOException("库索引在读取时改变长度。");
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    /// <summary>提交点前检查实际字节，不能用修改时间或重新编码的语义内容代替。</summary>
    public void RequireCurrent(string path, int maximumBytes)
    {
        if (Read(path, maximumBytes) != this) throw new LibraryRevisionConflictException(path);
    }
}

/// <summary>库已发生变化；调用方必须保留作者草稿并明确处理冲突。</summary>
public sealed class LibraryRevisionConflictException : IOException
{
    /// <summary>为冲突的索引创建失败结果；没有发布新索引。</summary>
    public LibraryRevisionConflictException(string path) : base("Library index revision changed: " + path) { }
}
