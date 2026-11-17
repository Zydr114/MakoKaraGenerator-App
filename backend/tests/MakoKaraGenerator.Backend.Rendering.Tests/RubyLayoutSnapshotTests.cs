using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class RubyLayoutSnapshotTests
{
    [Fact]
    public void Ruby_style_and_token_overrides_survive_worker_snapshot_round_trip()
    {
        var source = StaticSnapshotFixture.Create();
        var branch = source.Lyrics.PrimaryBranch!;
        var line = branch.Lines[0];
        line = line with { Tokens = line.Tokens.SetItem(0, line.Tokens[0] with
        {
            StyleOverride = new() { Ruby = new()
            {
                RelativeScale = OptionalValue.Set<double?>(.8),
                GapPx = OptionalValue.Set<double?>(null),
            } },
        }) };
        source = source with
        {
            LyricStyle = source.LyricStyle with { Ruby = new() { RelativeScale = .6, GapPx = 8 } },
            Lyrics = source.Lyrics.ReplaceBranch(branch with { Lines = branch.Lines.SetItem(0, line) }),
        };

        byte[] bytes = StaticProjectSnapshotCodec.Encode(source);
        var restored = StaticProjectSnapshotCodec.Decode(bytes);
        Assert.Equal(source.LyricStyle.Ruby, restored.LyricStyle.Ruby);
        Assert.Equal(line.Tokens[0].StyleOverride!.Ruby, restored.Lyrics.PrimaryBranch!.Lines[0].Tokens[0].StyleOverride!.Ruby);
        Assert.Equal(bytes, StaticProjectSnapshotCodec.Encode(restored));
    }
}
