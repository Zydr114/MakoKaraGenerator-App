namespace MakoKaraGenerator.Application.Editing;

/// <summary>一次文件选择取得的固定歌词字节；识别与解析始终消费同一份输入。</summary>
/// <remarks>文件读取、选择器与取消由调用方负责，本类型不再次访问文件系统。</remarks>
public sealed class LyricsImportSource
{
    private readonly byte[] _bytes;

    /// <summary>防御复制输入字节，建立与原数组独立的固定来源。</summary>
    /// <param name="fileName">用于格式识别的文件名，含扩展名。</param>
    /// <param name="bytes">本次读取的完整歌词字节。</param>
    /// <exception cref="ArgumentException">文件名为空白。</exception>
    public LyricsImportSource(string fileName, ReadOnlySpan<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        FileName = fileName;
        _bytes = bytes.ToArray();
    }

    /// <summary>固定来源的文件名。</summary>
    public string FileName { get; }

    /// <summary>完整输入的字节数。</summary>
    public int Length => _bytes.Length;

    internal ReadOnlySpan<byte> Header => _bytes.AsSpan(0, Math.Min(_bytes.Length, 4096));
    internal Stream OpenRead() => new MemoryStream(_bytes, writable: false);
}
