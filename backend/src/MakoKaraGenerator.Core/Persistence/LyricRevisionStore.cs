using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Core.Diagnostics;
using static MakoKaraGenerator.Core.Diagnostics.DiagnosticList;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>不可变歌词内容地址；只发布缺失修订，绝不覆写历史内容。</summary>
internal static class LyricRevisionStore
{
    private const string Prefix = ProjectJson.LyricsDirectoryName + "/";
    private const string Suffix = ".json";
    private static readonly SearchValues<char> HashCharacters = SearchValues.Create("0123456789abcdef");

    internal static string Publish(string directoryPath, LyricsFileDto dto)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(dto, ProjectJson.Options);
        string reference = Prefix + Hash(bytes) + Suffix;
        EnsureNoLinks(directoryPath, reference);
        string path = Path.Combine(directoryPath, reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            VerifyExisting(path, bytes);
            return reference;
        }

        string temporary = path + "." + Guid.NewGuid().ToString("N") + ProjectJson.TemporaryFileSuffix;
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            VerifyExisting(temporary, bytes);
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            {
                // 另一保存先发布相同内容时可以复用；损坏内容仍必须拒绝。
                EnsureNoLinks(directoryPath, reference);
                VerifyExisting(path, bytes);
            }
        }
        finally
        {
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
        return reference;
    }

    internal static byte[]? Read(string directoryPath, string? reference, List<ProjectIssue> issues)
    {
        if (!IsCanonical(reference))
        {
            Add(issues, DiagnosticCode.InvalidLyricsReference, IssueSeverity.Error,
                "lyricsFile 必须是 data/lyrics/<64位小写SHA-256>.json，已取消打开。", "project.json.lyricsFile");
            return null;
        }
        try { EnsureNoLinks(directoryPath, reference!); }
        catch (IOException exception)
        {
            Add(issues, DiagnosticCode.InvalidLyricsReference, IssueSeverity.Error,
                exception.Message, "project.json.lyricsFile");
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            Add(issues, DiagnosticCode.InvalidLyricsReference, IssueSeverity.Error,
                exception.Message, "project.json.lyricsFile");
            return null;
        }
        string path = Path.Combine(directoryPath, reference!);
        if (!File.Exists(path))
        {
            Add(issues, DiagnosticCode.MissingLyricsFile, IssueSeverity.Error,
                $"工程引用的歌词修订不存在：{reference}。已取消打开，未生成空歌词。", reference);
            return null;
        }
        byte[]? bytes = ProjectJsonReader.ReadBytes(path, issues);
        if (bytes is null) { return null; }
        if (!string.Equals(Hash(bytes), reference!.Substring(Prefix.Length, 64), StringComparison.Ordinal))
        {
            Add(issues, DiagnosticCode.LyricsContentMismatch, IssueSeverity.Error,
                $"歌词修订内容与SHA-256不符：{reference}。文件未更改。", reference);
            return null;
        }
        return bytes;
    }

    private static bool IsCanonical(string? reference) => reference is not null
        && reference.Length == Prefix.Length + 64 + Suffix.Length
        && reference.StartsWith(Prefix, StringComparison.Ordinal)
        && reference.EndsWith(Suffix, StringComparison.Ordinal)
        && reference.AsSpan(Prefix.Length, 64).IndexOfAnyExcept(HashCharacters) < 0;

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void VerifyExisting(string path, byte[] bytes)
    {
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            throw new IOException($"歌词内容地址已被不同内容占用，拒绝覆写：{path}");
        }
    }

    private static void EnsureNoLinks(string directoryPath, string reference)
    {
        string path = directoryPath;
        foreach (string segment in reference.Split('/'))
        {
            path = Path.Combine(path, segment);
            if (new FileInfo(path).LinkTarget is not null)
            {
                throw new IOException($"歌词修订路径不能经过文件或目录链接：{path}");
            }
        }
    }
}
