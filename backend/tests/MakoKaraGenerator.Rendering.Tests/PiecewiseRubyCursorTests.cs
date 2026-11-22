using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.V2;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>Phase 0 契约 B1：SDK v2 源索引播放头与渲染采样多拍注音分段进度 100% 同步。</summary>
public sealed class PiecewiseRubyCursorTests
{
    [Fact]
    public void PureSourceIndexes与PlaybackProjection多拍注音播放头进度与LyricSampler严格一致()
    {
        RubyAnnotation ruby = RubyAnnotation.Create("r1", "t1", "きょう") with
        {
            Segments =
            [
                RubyTextSegment.Create("s1", "きょ", new(1_000_000, 1_200_000), TimingStatus.Confirmed),
                RubyTextSegment.Create("s2", "う", new(1_400_000, 2_000_000), TimingStatus.Confirmed),
            ],
        };
        LyricLine line = LyricLine.Create(
            "l1", "郷", new(0, 3_000_000), TimingStatus.Confirmed,
            [LyricToken.Create("t1", "郷", new(1_000_000, 2_000_000), TimingStatus.Confirmed)]) with
        {
            RubyAnnotations = [ruby],
        };
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(Project.Create(
            "piecewise-cursor",
            Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 3_000_000),
            LyricsDocument.Create([line])) with { EnterAnimation = null });

        using SkiaRenderPipeline geometry = new();
        TimelineProjection timeline = TimelineProjection.Create(snapshot, Fx.ReplayReason.Initial);
        var indexes = PureSourceIndexes.Create(snapshot, timeline, geometry, new Fx.EffectBinding.Project(), new Fx.SourceSubscription.Project());

        long[] checkpoints = [1_100_000, 1_200_000, 1_300_000, 1_700_000];
        double[] expected = [1d / 3d, 2d / 3d, 2d / 3d, 5d / 6d];

        for (int i = 0; i < checkpoints.Length; i++)
        {
            long timeUs = checkpoints[i];
            double expectedProgress = expected[i];

            double samplerProgress = LyricSampler.ComputeTokenProgress(line, line.Tokens[0], timeUs);
            Assert.Equal(expectedProgress, samplerProgress, 10);

            Fx.PlaybackCursor indexCursor = Assert.Single(indexes.Cursors.GetBaseAt(timeUs, new Fx.SourceFilter.Project(), Fx.CursorAnchorMode.Baseline));
            Assert.Equal(expectedProgress, indexCursor.Progress, 10);

            SceneFrame scene = geometry.GetSceneFrame(snapshot, timeUs);
            Fx.PlaybackFrame playback = PlaybackProjection.Create(snapshot, scene, scene.TokenGeometries);
            Fx.PlaybackCursor renderCursor = Assert.Single(playback.GetCursors(Fx.CursorAnchorMode.Baseline));
            Assert.Equal(expectedProgress, renderCursor.Progress, 10);
            Assert.Equal(indexCursor.Position.X, renderCursor.Position.X, 6);
        }

        // 验证分段断点用于 V2CursorCuts。
        var cuts = LyricSampler.GetPiecewiseBreakpoints(line, line.Tokens[0]);
        Assert.Equal([1_200_000L, 1_400_000L], cuts.ToArray());
    }
}
