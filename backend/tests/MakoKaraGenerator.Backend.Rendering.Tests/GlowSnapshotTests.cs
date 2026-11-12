using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class GlowSnapshotTests
{
    private static readonly GlowSpec Glow = new() { Color = new(10, 170, 250, 180), Opacity = .7f, BlurRadiusPx = 9, SpreadPx = 3 };

    [Fact]
    public void 辉光完整值局部覆盖显式关闭与标记外观跨Worker源无损()
    {
        var source = Source();
        byte[] bytes = StaticProjectSnapshotCodec.Encode(source);
        var copy = StaticProjectSnapshotCodec.Decode(bytes);
        Assert.Equal(source.LyricStyle, copy.LyricStyle);
        Assert.Equal(source.Lyrics, copy.Lyrics);
        Assert.Equal(source.LyricPresentation, copy.LyricPresentation);
        Assert.Equal(bytes, StaticProjectSnapshotCodec.Encode(copy));
        var wire = JsonNode.Parse(bytes)!;
        var glow = wire["snapshot"]!["lyricStyle"]!["unsung"]!["glow"]!.AsObject();
        Assert.Equal(["blurRadiusPx", "color", "opacity", "spreadPx"], glow.Select(x => x.Key).Order(StringComparer.Ordinal));
        Assert.False(glow.ContainsKey("isEnabled"));
        Assert.False(glow.ContainsKey("isValid"));
    }

    [Fact]
    public void 固定预设源携带辉光五项选择与当前模板格式()
    {
        var source = PresetSnapshotFixture.Create();
        source = source with { PresetApplications = new(source.PresetApplications!.Items.Select(item => new PresetApplication(item.Id, item.Target, StyleTemplate.Create(item.Snapshot.Id, item.Snapshot.Name, StyleTemplateKind.Line,
                item.Snapshot.Style with { Glow = Glow },
                [LyricStyleProperty.UnsungGlow, LyricStyleProperty.UnsungGlowColor, LyricStyleProperty.UnsungGlowOpacity,
                    LyricStyleProperty.UnsungGlowBlurRadiusPx, LyricStyleProperty.UnsungGlowSpreadPx]), enabled: item.Enabled))) };
        byte[] bytes = StaticProjectSnapshotCodec.Encode(source, StaticProjectSnapshotCodec.PresetsProfile);
        var copy = StaticProjectSnapshotCodec.Decode(bytes);
        Assert.Equal(source.PresetApplications, copy.PresetApplications);
        Assert.Equal(bytes, StaticProjectSnapshotCodec.Encode(copy, StaticProjectSnapshotCodec.PresetsProfile));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("negative")]
    public void 辉光快照拒绝缺字段未知字段及非法数值(string mutation)
    {
        var root = JsonNode.Parse(StaticProjectSnapshotCodec.Encode(Source()))!;
        var glow = root["snapshot"]!["lyricStyle"]!["unsung"]!["glow"]!.AsObject();
        if (mutation == "missing") glow.Remove("spreadPx");
        else if (mutation == "unknown") glow["angleDegrees"] = 20;
        else glow["blurRadiusPx"] = -1;
        Assert.Throws<SnapshotContractException>(() => StaticProjectSnapshotCodec.Decode(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    private static ProjectSnapshot Source()
    {
        var source = StaticSnapshotFixture.Create();
        var branch = source.Lyrics.PrimaryBranch!;
        var line = branch.Lines[0];
        line = line with
        {
            StyleOverride = (line.StyleOverride ?? new()) with { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { SpreadPx = 4 }) },
            Tokens = line.Tokens.SetItem(0, line.Tokens[0] with
            { StyleOverride = new() { Glow = OptionalValue.Set<GlowSpecOverride?>(null) } })
        };
        return source with
        {
            LyricStyle = source.LyricStyle with { Glow = Glow },
            Lyrics = source.Lyrics.ReplaceBranch(branch with { Lines = branch.Lines.SetItem(0, line) }),
            LyricPresentation = source.LyricPresentation with
            {
                Nodes = [.. source.LyricPresentation.Nodes.Select(node => node with
                {
                    Content = node.Content switch
                    {
                        CountdownSequenceContent sequence => sequence with { Appearance = sequence.Appearance with { Glow = Glow } },
                        TextMarkContent mark => mark with { AppearanceOverride = new() { Pending = new() { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { Opacity = .4f }) } } },
                        _ => node.Content,
                    }
                })]
            }
        };
    }
}
