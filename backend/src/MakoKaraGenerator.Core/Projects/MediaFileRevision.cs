using System.Buffers;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>真实媒体字节的不可变身份；文件名只供显示，不进入内容摘要。</summary>
public sealed record MediaFileRevision
{
    private static readonly SearchValues<char> HashCharacters = SearchValues.Create("0123456789abcdef");
    /// <summary>创建完整身份；哈希必须为64位小写SHA-256，长度可为零。</summary>
    public MediaFileRevision(string hash, long byteLength, string originalName)
    {
        ValidateHash(hash); ArgumentOutOfRangeException.ThrowIfNegative(byteLength); ArgumentException.ThrowIfNullOrWhiteSpace(originalName);
        if (originalName.Contains('/') || originalName.Contains('\\') || originalName.Any(char.IsControl) || originalName is "." or "..")
            throw new ArgumentException("原文件名不能包含路径或控制字符。", nameof(originalName));
        Hash = hash; ByteLength = byteLength; OriginalName = originalName;
    }
    /// <summary>完整内容SHA-256。</summary>
    public string Hash { get; }
    /// <summary>实际字节数。</summary>
    public long ByteLength { get; }
    /// <summary>用户选择时的文件名。</summary>
    public string OriginalName { get; }
    /// <summary>唯一受管位置，解码器按文件内容探测。</summary>
    public string RelativePath => "data/media/" + Hash + ".media";
    /// <summary>严格校验所有内容地址共用的哈希编码。</summary>
    public static void ValidateHash(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (hash.Length != 64 || hash.AsSpan().IndexOfAnyExcept(HashCharacters) >= 0)
            throw new ArgumentException("内容摘要必须为64位小写SHA-256。", nameof(hash));
    }
}
