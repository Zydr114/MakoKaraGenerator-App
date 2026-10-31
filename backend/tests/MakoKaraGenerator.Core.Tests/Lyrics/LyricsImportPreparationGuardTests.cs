using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Import;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

public sealed class LyricsImportPreparationGuardTests
{
    [Theory]
    [InlineData("txt", "汉汉汉汉汉")]
    [InlineData("lrc", "[00:01.00]汉汉汉汉汉")]
    [InlineData("lrc", "[00:01.00]<00:01.00>汉<00:02.00>汉")]
    [InlineData("srt", "1\n00:00:01,000 --> 00:00:03,000\n汉汉汉汉汉")]
    [InlineData("vtt", "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\n汉汉汉汉汉")]
    [InlineData("ass", "[Script Info]\n[Events]\nFormat: Layer, Start, End, Text\nDialogue: 0,0:00:01.00,0:00:03.00,{\\k50}汉{\\k50}汉")]
    [InlineData("txt", "#TITLE:x\n#ARTIST:y\n#BPM:30\n: 0 4 60 汉\n: 4 2 60 汉")]
    [InlineData("ttml", "<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"1s\" end=\"3s\"><span begin=\"1s\" end=\"2s\">汉</span><span begin=\"2s\" end=\"3s\">汉</span></p></div></body></tt>")]
    public void 八格式guard不改变文档完整报告且耗尽不返回部分结果(string extension,string text)
    {
        var registry = LyricsImporterRegistry.CreateDefault(); byte[] bytes = Encoding.UTF8.GetBytes(text);
        var unbounded = registry.Import(new MemoryStream(bytes), new() { FileName = "song."+extension, MediaDurationUs=5_000_000 });
        var bounded = registry.Import(new MemoryStream(bytes), new() { FileName = "song."+extension, MediaDurationUs=5_000_000, PreparationGuard = new(64*1024*1024) });
        Assert.Equal(Serialize(unbounded),Serialize(bounded));
        Assert.Throws<LyricsImportPreparationLimitException>(() => registry.Import(new MemoryStream(bytes), new() { FileName="song."+extension,PreparationGuard=new(1) }));
    }

    [Fact]
    public void 大量换行在创建行数组前耗尽且长单grapheme按UTF16文本计费()
    {
        var guard = new LyricsImportPreparationGuard(4096);
        Assert.Throws<LyricsImportPreparationLimitException>(() => guard.Lines(new string('\n',100000)));
        Assert.Equal(0,guard.UsedBytes);
        var input = Encoding.UTF8.GetBytes("e"+new string('\u0301',4000));
        Assert.Throws<LyricsImportPreparationLimitException>(() => LyricsImporterRegistry.CreateDefault().Import(new MemoryStream(input),
            new() { FileName="song.txt",PreparationGuard=new(4096) }));
    }

    [Fact]
    public void 诊断observer在保留前可拒绝并保持原报告绝不截断()
    {
        int seen=0; var guard = new LyricsImportPreparationGuard(64*1024*1024, _ => { seen++; throw new LyricsImportPreparationLimitException(); });
        Assert.Throws<LyricsImportPreparationLimitException>(() => LyricsImporterRegistry.CreateDefault().Import(new MemoryStream("[unknown]\n[00:01.00]有效"u8.ToArray()),
            new() { FileName="song.lrc",PreparationGuard=guard }));
        Assert.Equal(1,seen);
    }
    [Fact]
    public void 原公开与受保护扩展CLR签名仍存在()
    {
        Assert.NotNull(typeof(MakoKaraGenerator.Core.Lyrics.Lrc.LrcParser).GetMethod("Parse",[typeof(string),typeof(MakoKaraGenerator.Core.Lyrics.Lrc.LrcParserOptions)]));
        Assert.NotNull(typeof(LyricsDocumentTokenSplitter).GetMethod("Apply",[typeof(LyricsDocument),typeof(TokenSplitRule),typeof(bool),typeof(IReadOnlyCollection<int>),typeof(Func<string>)]));
        Assert.NotNull(typeof(ParsedLyricsImporterBase).GetMethod("Parse",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance,
            null,[typeof(string),typeof(LyricsImportContext),typeof(List<LyricsImportIssue>)],null));
        Assert.NotNull(typeof(LrcImporterBase).GetMethod("BuildLine",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance,
            null,[typeof(MakoKaraGenerator.Core.Lyrics.Lrc.LrcEntry),typeof(MakoKaraGenerator.Core.Time.TimeRange),typeof(LyricsIdAllocator),typeof(List<LyricsImportIssue>),typeof(int).MakeByRefType()],null));
    }

    private static readonly JsonSerializerOptions ComparisonJson = CreateComparisonJson();
    private static string Serialize(object value) => JsonSerializer.Serialize(value, ComparisonJson);
    private static JsonSerializerOptions CreateComparisonJson()
    {
        var resolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(LyricToken) || info.Type == typeof(LyricLine))
                for (int i = info.Properties.Count - 1; i >= 0; i--)
                    if (info.Properties[i].Name == "DurationUs") info.Properties.RemoveAt(i);
        });
        return new JsonSerializerOptions { TypeInfoResolver = resolver };
    }

}
