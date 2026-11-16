using MakoKaraGenerator.Scripting.Compilation;
using System.Security.Cryptography;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class StaticSnapshotRuntimeEquivalenceTests
{
    // This is the explicitly fixed local font fixture. Do not silently substitute
    // another font and call a same-environment comparison a portable golden.
    private const string FontSha256 = "b76b0433203017ca80401b2ee0dd69350349871c4b19d504c34dbdd80541690a";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 固定字体真实Runtime歌词注音场景普通透明逐字节等价(bool transparent)
    {
        AssertFixedFont();
        using TemporaryDirectory temp = new();
        using FfmpegVideoFrameProvider videos = new();
        var factory = Factory(temp, videos);
        ProjectSnapshot source = StaticSnapshotFixture.Create() with
        {
            AudioPath = "/nonexistent-no-audio-io-snapshot-fixture/song.wav",
            AudioMedia = new(new string('a', 64), 42, "song.wav"),
        };
        ProjectSnapshot copy = StaticProjectSnapshotCodec.Decode(StaticProjectSnapshotCodec.Encode(source));
        await using IRenderFrameSession original = await factory.CreateAsync(source);
        await using IRenderFrameSession restored = await factory.CreateAsync(copy);
        foreach (long time in new long[] { 10_000_000, 12_500_000, 40_500_000 })
        {
            using RenderFrame expected = await original.RenderAsync(Request(source, time, transparent));
            using RenderFrame actual = await restored.RenderAsync(Request(copy, time, transparent));
            Assert.Equal(expected.Target, actual.Target);
            Assert.Equal(expected.TimeUs, actual.TimeUs);
            Assert.Equal(expected.Format, actual.Format);
            Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
            Assert.Contains(actual.PixelMemory.ToArray(), channel => channel != 0);
            if (transparent)
            {
                byte[] alpha = actual.PixelMemory.ToArray().Where((_, index) => index % 4 == 3).ToArray();
                Assert.Contains((byte)0, alpha);
                Assert.Contains(alpha, channel => channel > 0 && channel < 255);
            }
        }
    }

    [Fact]
    public async Task 固定夹具注音与倒计时场景确实贡献像素不只是非空背景()
    {
        AssertFixedFont();
        using TemporaryDirectory temp = new();
        using FfmpegVideoFrameProvider videos = new();
        var factory = Factory(temp, videos);
        ProjectSnapshot snapshot = StaticSnapshotFixture.Create();
        var primary = snapshot.Lyrics.PrimaryBranch!;
        ProjectSnapshot noRuby = snapshot with
        {
            Lyrics = snapshot.Lyrics.ReplaceBranch(primary with
            { Lines = primary.Lines.SetItem(0, primary.Lines[0] with { RubyAnnotations = [] }) }),
        };
        ProjectSnapshot hiddenMarks = snapshot with
        {
            LyricPresentation = snapshot.LyricPresentation with
            {
                Nodes = [.. snapshot.LyricPresentation.Nodes.Select(node => node.Content is CountdownSequenceContent countdown
                    ? node with { Content = countdown with { Appearance = countdown.Appearance with { Opacity = 0 } } } : node)],
            },
        };
        await using IRenderFrameSession full = await factory.CreateAsync(snapshot);
        await using IRenderFrameSession withoutRuby = await factory.CreateAsync(noRuby);
        await using IRenderFrameSession withoutMarks = await factory.CreateAsync(hiddenMarks);
        using RenderFrame expectedRuby = await full.RenderAsync(Request(snapshot, 12_500_000, true));
        using RenderFrame removedRuby = await withoutRuby.RenderAsync(Request(noRuby, 12_500_000, true));
        Assert.False(expectedRuby.PixelMemory.Span.SequenceEqual(removedRuby.PixelMemory.Span));
        using RenderFrame expectedMarks = await full.RenderAsync(Request(snapshot, 10_000_000, true));
        using RenderFrame removedMarks = await withoutMarks.RenderAsync(Request(hiddenMarks, 10_000_000, true));
        Assert.False(expectedMarks.PixelMemory.Span.SequenceEqual(removedMarks.PixelMemory.Span));
    }

    private static ProjectRenderSessionFactory Factory(TemporaryDirectory temp, FfmpegVideoFrameProvider videos) => new(new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("cache")), videos, ReplayReason.Initial);

    private static RenderRequest Request(ProjectSnapshot snapshot, long time, bool transparent) =>
        RenderRequest.At(snapshot, time) with { Target = new() { PixelWidth = 320, PixelHeight = 180 }, TransparentBackground = transparent };

    private static void AssertFixedFont()
    {
        string family = SkiaTextLayoutEngine.DefaultFamilyName;
        using SKTypeface typeface = SKFontManager.Default.MatchFamily(family, SKFontStyle.Normal);
        Assert.NotNull(typeface);
        Assert.Equal(family, typeface.FamilyName);
        using SKStreamAsset stream = typeface.OpenStream();
        Assert.NotNull(stream);
        Assert.True(stream.Length > 0);
        if (OperatingSystem.IsLinux())
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, buffer.Length)) != 0) hash.AppendData(buffer, 0, read);
            Assert.Equal(FontSha256, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        using SkiaTextLayoutEngine engine = new();
        var shape = engine.Shape("歌詞か●Translation", new() { FamilyName = family, FontSizePx = 32 });
        Assert.NotEmpty(shape.Elements);
        Assert.All(shape.Elements, element =>
        {
            Assert.False(element.HasMissingGlyph);
            Assert.False(element.IsFallback);
            Assert.Equal(family, element.FamilyName);
        });
    }
}
