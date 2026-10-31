using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class LeadInStaticSnapshotTests
{
    [Fact]
    public void 前奏配置与提示标记在静态Worker源中完整无损往返()
    {
        ProjectSnapshot source = StaticSnapshotFixture.Create();
        ProjectSnapshot decoded = StaticProjectSnapshotCodec.Decode(StaticProjectSnapshotCodec.Encode(source));
        foreach (var program in source.LyricPresentation.Programs)
        {
            var copy = decoded.LyricPresentation.Programs.Single(p => p.Id == program.Id);
            Assert.Equal(program.LeadIns.ToArray(), copy.LeadIns.ToArray());
            Assert.Equal(program.LeadInGeneration, copy.LeadInGeneration);
            foreach (var cue in program.LeadIns)
            {
                var actual = copy.LeadIns.Single(c => c.Id == cue.Id);
                Assert.Equal(cue.Name, actual.Name); Assert.Equal(cue.Enabled, actual.Enabled);
                Assert.Equal(cue.TargetLineId, actual.TargetLineId); Assert.Equal(cue.IndicatorContainerId, actual.IndicatorContainerId);
                Assert.Equal(cue.DurationUs, actual.DurationUs); Assert.Equal(cue.Origin, actual.Origin);
                var sequence = source.LyricPresentation.FindNode(cue.IndicatorContainerId)!;
                var sequenceCopy = decoded.LyricPresentation.FindNode(cue.IndicatorContainerId)!;
                Assert.Equal(sequence.Attachment, sequenceCopy.Attachment);
                Assert.Equal(sequence.Content, sequenceCopy.Content);
                foreach (string id in ((CountdownSequenceContent)sequence.Content!).MarkNodeIds)
                    Assert.Equal(source.LyricPresentation.FindNode(id), decoded.LyricPresentation.FindNode(id));
            }
        }
        Assert.NotEmpty(source.LyricPresentation.Programs.SelectMany(p => p.LeadIns));
    }
}
