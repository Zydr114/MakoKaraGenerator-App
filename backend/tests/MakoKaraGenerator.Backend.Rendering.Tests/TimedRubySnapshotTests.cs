using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class TimedRubySnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 静态与包快照完整保留片段身份时间来源且重新编码逐字节相等(bool package)
    {
        var source = TimedRubyFixture.Create([
            RubyTextSegment.Create("early", "e\u0301", new(100_000, 300_000), TimingStatus.Imported),
            RubyTextSegment.Create("missing", "👩‍💻"),
            RubyTextSegment.Create("late", "せ", new(700_000, 1_100_000), TimingStatus.Confirmed)]);
        if (package) source = PackageSnapshotContractFixture.Fingerprint(source with
            { Effects = PackageRuntimeFixture.Create("scale").Effects });
        var bytes = package ? PackageProjectSnapshotCodec.Encode(source) : StaticProjectSnapshotCodec.Encode(source);
        var copy = package ? PackageProjectSnapshotCodec.Decode(bytes) : StaticProjectSnapshotCodec.Decode(bytes);
        Assert.Equal(source.Lyrics, copy.Lyrics);
        Assert.Equal(source.Lyrics.Lines[0].RubyAnnotations[0].Segments.ToArray(), copy.Lyrics.Lines[0].RubyAnnotations[0].Segments.ToArray());
        Assert.Equal(bytes, package ? PackageProjectSnapshotCodec.Encode(copy) : StaticProjectSnapshotCodec.Encode(copy));
        var root = JsonNode.Parse(bytes)!;
        var ruby = root["snapshot"]!["lyrics"]!["branches"]![0]!["lines"]![0]!["rubyAnnotations"]![0]!;
        var segment = ruby["segments"]![0]!;
        Assert.Equal(["id", "text", "time", "timingStatus"], segment.AsObject().Select(item => item.Key).Order(StringComparer.Ordinal));
        segment["accidentalComputedProperty"] = true;
        var failure = Assert.Throws<SnapshotContractException>(() => {
            byte[] invalid = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
            _ = package ? PackageProjectSnapshotCodec.Decode(invalid) : StaticProjectSnapshotCodec.Decode(invalid);
        });
        Assert.Equal("SnapshotInvalid", failure.Code);
    }

    [Fact]
    public void 片段数量进入完整源图预算并在序列化前明确拒绝()
    {
        var source = TimedRubyFixture.Create([.. Enumerable.Range(0, 45_000).Select(index => RubyTextSegment.Create("part-" + index, "a"))]);
        var failure = Assert.Throws<SnapshotContractException>(() => StaticProjectSnapshotCodec.Encode(source));
        Assert.Equal("SnapshotSizeExceeded", failure.Code);
    }
}
