using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MakoKaraGenerator.TestSupport;

/// <summary>独立构造内容地址测试数据，不调用生产歌词发布逻辑。</summary>
internal static class ProjectFileFixture
{
    internal static string LyricsPath(string directory)
    {
        JsonNode entry = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "project.json")))!;
        return Path.Combine(directory, entry["lyricsFile"]!.GetValue<string>());
    }

    internal static string ReadLyrics(string directory) => File.ReadAllText(LyricsPath(directory));

    internal static string PublishLyrics(string directory, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        string reference = "data/lyrics/" + Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".json";
        string path = Path.Combine(directory, reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
        else { File.WriteAllBytes(path, bytes); }

        string entryPath = Path.Combine(directory, "project.json");
        if (File.Exists(entryPath))
        {
            // 坏入口原样保留，以便测试其本身的JSON错误。
            try
            {
                JsonObject entry = JsonNode.Parse(File.ReadAllText(entryPath))!.AsObject();
                entry["lyricsFile"] = reference;
                File.WriteAllText(entryPath, entry.ToJsonString());
            }
            catch (System.Text.Json.JsonException) { }
        }
        return reference;
    }
}
