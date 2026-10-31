using System.Security.Cryptography;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class PresetSnapshotRuntimeEquivalenceTests
{
    [Theory]
    [InlineData("node", 13_000_000L, false)] [InlineData("node", 13_000_000L, true)]
    [InlineData("clip", 40_500_000L, false)] [InlineData("clip", 40_500_000L, true)]
    [InlineData("mark", 10_000_000L, false)] [InlineData("mark", 10_000_000L, true)]
    public async Task 真实Runtime固定预设解码保节点片段标记上下文及反事实像素(string applicationId, long time, bool transparent)
    {
        AssertFixedFont();
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var factory = new ProjectRenderSessionFactory(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
            temp.FilePath("cache"), videos, ReplayReason.Initial);
        ProjectSnapshot source = PresetSnapshotFixture.Create();
        byte[] encoded = StaticProjectSnapshotCodec.Encode(source, StaticProjectSnapshotCodec.PresetsProfile);
        ProjectSnapshot decoded = StaticProjectSnapshotCodec.Decode(encoded);
        ProjectSnapshot removed = source with { PresetApplications = new(source.PresetApplications.Items.Where(item => item.Id != applicationId)) };
        await using IRenderFrameSession original = await factory.CreateAsync(source);
        await using IRenderFrameSession restored = await factory.CreateAsync(decoded);
        await using IRenderFrameSession counterfactual = await factory.CreateAsync(removed);
        RenderRequest Request(ProjectSnapshot snapshot) => RenderRequest.At(snapshot, time) with
        { Target = new() { PixelWidth = 320, PixelHeight = 180 }, TransparentBackground = transparent };
        using RenderFrame expected = await original.RenderAsync(Request(source));
        using RenderFrame actual = await restored.RenderAsync(Request(decoded));
        using RenderFrame absent = await counterfactual.RenderAsync(Request(removed));
        Assert.Equal(expected.Target, actual.Target); Assert.Equal(expected.TimeUs, actual.TimeUs);
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        Assert.False(actual.PixelMemory.Span.SequenceEqual(absent.PixelMemory.Span));
        // Source records remain intact: only Runtime's read-only frame projection resolves them.
        Assert.Equal(source.PresetApplications, decoded.PresetApplications);
        Assert.Equal(source.Lyrics, decoded.Lyrics); Assert.Equal(source.LyricPresentation, decoded.LyricPresentation);
        using SkiaRenderPipeline pipeline = new();
        var scene = pipeline.GetSceneFrame(decoded, time);
        if (applicationId == "clip") Assert.Contains(scene.Lines, line => line.DisplayClipId == "chorus-clip");
        if (applicationId == "node")
        {
            var id = decoded.PresetApplications.Items.Single(item => item.Id == "node").Target.Id!;
            Assert.Contains(scene.Lines, line => PresetSceneStyleProjection.NodePath(decoded, line.ElementId, scene).Contains(id));
        }
        if (applicationId == "mark") Assert.NotEmpty(scene.LeadIns);
    }

    private static void AssertFixedFont()
    {
        using SKTypeface typeface = SKFontManager.Default.MatchFamily(StaticSnapshotFixture.Family, SKFontStyle.Normal);
        Assert.Equal(StaticSnapshotFixture.Family, typeface.FamilyName);
        using SKStreamAsset stream = typeface.OpenStream();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024]; int count;
        while ((count = stream.Read(buffer, buffer.Length)) != 0) hash.AppendData(buffer.AsSpan(0, count));
        Assert.Equal("b76b0433203017ca80401b2ee0dd69350349871c4b19d504c34dbdd80541690a", Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}
