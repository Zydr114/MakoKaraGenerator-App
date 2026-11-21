using System.Collections.Immutable;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Tests;

public sealed class Phase1BeatAndEditingTests
{
    private static ProjectWorkspace CreateWorkspace(params LyricLine[] lines)
    {
        var doc = LyricsDocument.Create(lines);
        var project = Project.Create("p1-test", Composition.Create(1920, 1080, FrameRate.Fps30, outputDurationUs: 60_000_000), doc);
        return ProjectWorkspace.Adopt(project);
    }

    private static LyricLine UntimedLine(string id, string text)
    {
        var pieces = LyricTokenSplitter.Split(text, TokenSplitRule.Mixed);
        var tokens = pieces.Select((p, i) => LyricToken.Create($"{id}-t{i}", p)).ToImmutableArray();
        return new LyricLine
        {
            Id = id,
            Text = text,
            Time = null,
            TimingStatus = TimingStatus.Missing,
            Tokens = tokens,
        };
    }

    [Fact]
    public void StrikeBeat_UntimedPlainTextLine_InitializesAndExpandsLineEnvelopeAndSingleUndo()
    {
        var ws = CreateWorkspace(UntimedLine("l1", "夢見る"));
        string branchId = ws.Project.Lyrics.PrimaryBranchId;
        var t0 = new LyricBeatTarget(branchId, "l1", "l1-t0");
        var t1 = new LyricBeatTarget(branchId, "l1", "l1-t1");
        var t2 = new LyricBeatTarget(branchId, "l1", "l1-t2");

        // Strike 1 on completely untimed line (no line.Time!)
        var r1 = LyricTimingEditUseCase.StrikeBeat(ws, null, t0, 1_000_000);
        Assert.True(r1.Accepted);
        Assert.True(r1.EstimatedPlaceholder);
        Assert.False(r1.CloseGuardApplied);
        var line1 = ws.Project.Lyrics.Lines[0];
        Assert.NotNull(line1.Time);
        Assert.Equal(1_000_000, line1.Time!.Value.StartUs);
        Assert.Equal(1_000_000, line1.Tokens[0].Time!.Value.StartUs);
        Assert.Equal(TimingStatus.Estimated, line1.Tokens[0].TimingStatus);

        // Strike 2 closes t0 and opens t1
        var r2 = LyricTimingEditUseCase.StrikeBeat(ws, t0, t1, 1_500_000);
        Assert.True(r2.Accepted);
        Assert.False(r2.CloseGuardApplied);
        line1 = ws.Project.Lyrics.Lines[0];
        Assert.Equal(new TimeRange(1_000_000, 1_500_000), line1.Tokens[0].Time);
        Assert.Equal(TimingStatus.Edited, line1.Tokens[0].TimingStatus);
        Assert.Equal(1_500_000, line1.Tokens[1].Time!.Value.StartUs);

        // Strike 3 closes t1 and opens t2
        var r3 = LyricTimingEditUseCase.StrikeBeat(ws, t1, t2, 2_000_000);
        Assert.True(r3.Accepted);

        // CloseBeat on t2 (L key)
        var r4 = LyricTimingEditUseCase.CloseBeat(ws, t2, 2_600_000);
        Assert.True(r4.Accepted);
        line1 = ws.Project.Lyrics.Lines[0];
        Assert.Equal(new TimeRange(1_000_000, 2_600_000), line1.Time);
        Assert.Equal(new TimeRange(2_000_000, 2_600_000), line1.Tokens[2].Time);
        Assert.Equal(TimingStatus.Edited, line1.Tokens[2].TimingStatus);

        // Single undo reverts only CloseBeat (t2 returns to placeholder), next undo reverts Strike 3
        ws.Undo();
        Assert.Equal(TimingStatus.Estimated, ws.Project.Lyrics.Lines[0].Tokens[2].TimingStatus);
        ws.Undo();
        Assert.Null(ws.Project.Lyrics.Lines[0].Tokens[2].Time);
    }

    [Fact]
    public void StrikeBeat_ForgotLGuard_TruncatesAtPhraseEndAndLineEndWhenGapExceeds1500ms()
    {
        // Japanese pause space between "夢" and "光" => HasPhraseEndHint == true on "夢"
        var ws = CreateWorkspace(
            UntimedLine("l1", "夢 光"),
            UntimedLine("l2", "空"));
        string branchId = ws.Project.Lyrics.PrimaryBranchId;
        var tokYume = new LyricBeatTarget(branchId, "l1", "l1-t0"); // "夢" (pause space after)
        var tokHikari = new LyricBeatTarget(branchId, "l1", "l1-t2"); // "光" (last content token)
        var tokSora = new LyricBeatTarget(branchId, "l2", "l2-t0"); // "空" (line 2)

        Assert.True(LyricTimingEditUseCase.StrikeBeat(ws, null, tokYume, 1_000_000).Accepted);

        // Gap = 2.0s (> 1.5s) across Japanese pause space -> guard truncates to 1.0s + 600ms = 1.6s (Estimated)
        var rMid = LyricTimingEditUseCase.StrikeBeat(ws, tokYume, tokHikari, 3_000_000);
        Assert.True(rMid.Accepted);
        Assert.True(rMid.CloseGuardApplied);
        var l1 = ws.Project.Lyrics.Lines[0];
        Assert.Equal(new TimeRange(1_000_000, 1_600_000), l1.Tokens[0].Time);
        Assert.Equal(TimingStatus.Estimated, l1.Tokens[0].TimingStatus);
        Assert.Equal(3_000_000, l1.Tokens[2].Time!.Value.StartUs);

        // Gap = 1.0s (<= 1.5s) across line boundary -> seamless close (Edited, no guard)
        var rCrossFast = LyricTimingEditUseCase.StrikeBeat(ws, tokHikari, tokSora, 4_000_000);
        Assert.True(rCrossFast.Accepted);
        Assert.False(rCrossFast.CloseGuardApplied);
        Assert.Equal(new TimeRange(3_000_000, 4_000_000), ws.Project.Lyrics.Lines[0].Tokens[2].Time);
        Assert.Equal(TimingStatus.Edited, ws.Project.Lyrics.Lines[0].Tokens[2].TimingStatus);

        // Undo cross-line strike reverts both line 1 close and line 2 open atomically
        ws.Undo();
        Assert.Null(ws.Project.Lyrics.Lines[1].Time);
        Assert.Null(ws.Project.Lyrics.Lines[1].Tokens[0].Time);
        Assert.Equal(TimingStatus.Estimated, ws.Project.Lyrics.Lines[0].Tokens[2].TimingStatus);
    }

    [Fact]
    public void StrikeBeat_EnglishSeparatorSpace_DoesNotTriggerPhraseEndGuard()
    {
        var ws = CreateWorkspace(UntimedLine("l1", "Hello world"));
        string branchId = ws.Project.Lyrics.PrimaryBranchId;
        var tokHello = new LyricBeatTarget(branchId, "l1", "l1-t0");
        var tokWorld = new LyricBeatTarget(branchId, "l1", "l1-t2");

        Assert.True(LyricTimingEditUseCase.StrikeBeat(ws, null, tokHello, 1_000_000).Accepted);
        var r2 = LyricTimingEditUseCase.StrikeBeat(ws, tokHello, tokWorld, 3_200_000);
        Assert.True(r2.Accepted);
        Assert.False(r2.CloseGuardApplied);
        Assert.Equal(new TimeRange(1_000_000, 3_200_000), ws.Project.Lyrics.Lines[0].Tokens[0].Time);
        Assert.Equal(TimingStatus.Edited, ws.Project.Lyrics.Lines[0].Tokens[0].TimingStatus);
    }

    [Fact]
    public void StrikeBeat_AndCloseBeat_OnMultiBeatRuby_RecomputesParentTokenAndLineEnvelope()
    {
        var tok = LyricToken.Create("t0", "二人");
        var ruby = RubyAnnotation.Create("r0", "t0", "ふたり") with
        {
            Segments =
            [
                RubyTextSegment.Create("s0", "ふ"),
                RubyTextSegment.Create("s1", "た"),
                RubyTextSegment.Create("s2", "り"),
            ],
        };
        var line = new LyricLine
        {
            Id = "l1",
            Text = "二人",
            Time = null,
            TimingStatus = TimingStatus.Missing,
            Tokens = [tok],
            RubyAnnotations = [ruby],
        };
        var ws = CreateWorkspace(line);
        string b = ws.Project.Lyrics.PrimaryBranchId;
        var s0 = new LyricBeatTarget(b, "l1", "t0", "r0", "s0");
        var s1 = new LyricBeatTarget(b, "l1", "t0", "r0", "s1");
        var s2 = new LyricBeatTarget(b, "l1", "t0", "r0", "s2");

        Assert.True(LyricTimingEditUseCase.StrikeBeat(ws, null, s0, 1_000_000).Accepted);
        Assert.True(LyricTimingEditUseCase.StrikeBeat(ws, s0, s1, 1_300_000).Accepted);
        Assert.True(LyricTimingEditUseCase.StrikeBeat(ws, s1, s2, 1_600_000).Accepted);
        Assert.True(LyricTimingEditUseCase.CloseBeat(ws, s2, 2_000_000).Accepted);

        var updated = ws.Project.Lyrics.Lines[0];
        Assert.Equal(new TimeRange(1_000_000, 2_000_000), updated.Time);
        Assert.Equal(new TimeRange(1_000_000, 2_000_000), updated.Tokens[0].Time);
        Assert.Equal(TimingStatus.Edited, updated.Tokens[0].TimingStatus);
        var segs = updated.RubyAnnotations[0].Segments;
        Assert.Equal(new TimeRange(1_000_000, 1_300_000), segs[0].Time);
        Assert.Equal(new TimeRange(1_300_000, 1_600_000), segs[1].Time);
        Assert.Equal(new TimeRange(1_600_000, 2_000_000), segs[2].Time);
    }

    [Fact]
    public void TogglePhraseEndHint_FlipsAutoAndClearsBackToNull()
    {
        var ws = CreateWorkspace(UntimedLine("l1", "Hello world"));
        string b = ws.Project.Lyrics.PrimaryBranchId;
        var hello = new LyricTimingEditTarget(b, "l1", "l1-t0");
        var space = new LyricTimingEditTarget(b, "l1", "l1-t1");

        // Auto is false -> first toggle sets explicit true
        var t1 = LyricTimingEditUseCase.TogglePhraseEndHint(ws, hello);
        Assert.True(t1.Accepted);
        Assert.True(ws.Project.Lyrics.Lines[0].Tokens[0].PhraseEndHint);

        // Second toggle restores null (auto false)
        var t2 = LyricTimingEditUseCase.TogglePhraseEndHint(ws, hello);
        Assert.True(t2.Accepted);
        Assert.Null(ws.Project.Lyrics.Lines[0].Tokens[0].PhraseEndHint);

        // Non-content whitespace token rejected
        var bad = LyricTimingEditUseCase.TogglePhraseEndHint(ws, space);
        Assert.False(bad.Accepted);
    }

    [Fact]
    public void SplitLine_AndMergeLines_PreserveTokensRubiesAndTimingEnvelopes()
    {
        var line = new LyricLine
        {
            Id = "l1",
            Text = "夜空星",
            Time = new TimeRange(1_000_000, 4_000_000),
            TimingStatus = TimingStatus.Edited,
            Tokens =
            [
                LyricToken.Create("t0", "夜", new TimeRange(1_000_000, 2_000_000), TimingStatus.Edited),
                LyricToken.Create("t1", "空", new TimeRange(2_000_000, 3_000_000), TimingStatus.Edited),
                LyricToken.Create("t2", "星", new TimeRange(3_000_000, 4_000_000), TimingStatus.Edited),
            ],
            RubyAnnotations =
            [
                RubyAnnotation.Create("r0", "t0", "よ"),
                RubyAnnotation.Create("r2", "t2", "ほし"),
            ],
        };
        var ws = CreateWorkspace(line);
        string b = ws.Project.Lyrics.PrimaryBranchId;

        var baseline = new LyricContentEditBaseline(ws, ws.Generation, new(b, "l1"), line);
        var split = LyricContentEditUseCase.SplitLine(ws, baseline, 2);
        Assert.True(split.Accepted);
        Assert.Equal(2, ws.Project.Lyrics.Lines.Length);
        var left = ws.Project.Lyrics.Lines[0];
        var right = ws.Project.Lyrics.Lines[1];
        Assert.Equal("l1", left.Id);
        Assert.Equal("夜空", left.Text);
        Assert.Equal(new TimeRange(1_000_000, 3_000_000), left.Time);
        Assert.Single(left.RubyAnnotations);
        Assert.Equal("星", right.Text);
        Assert.Equal(new TimeRange(3_000_000, 4_000_000), right.Time);
        Assert.Single(right.RubyAnnotations);
        Assert.Equal("t2", right.RubyAnnotations[0].BaseTokenId);

        // Merge back
        var mergeBaseline = new LyricContentEditBaseline(ws, ws.Generation, new(b, right.Id), right);
        var merged = LyricContentEditUseCase.MergeLines(ws, mergeBaseline);
        Assert.True(merged.Accepted);
        Assert.Single(ws.Project.Lyrics.Lines);
        var combined = ws.Project.Lyrics.Lines[0];
        Assert.Equal("l1", combined.Id);
        Assert.Equal("夜空星", combined.Text);
        Assert.Equal(new TimeRange(1_000_000, 4_000_000), combined.Time);
        Assert.Equal(2, combined.RubyAnnotations.Length);
    }

    [Fact]
    public void SetRubySegments_PreservesExistingSegmentTimesByIndexAndRemovesWhenEmpty()
    {
        var line = new LyricLine
        {
            Id = "l1",
            Text = "二人",
            Time = new TimeRange(1_000_000, 2_000_000),
            TimingStatus = TimingStatus.Edited,
            Tokens = [LyricToken.Create("t0", "二人", new TimeRange(1_000_000, 2_000_000), TimingStatus.Edited)],
            RubyAnnotations =
            [
                RubyAnnotation.Create("r0", "t0", "ふた") with
                {
                    Segments =
                    [
                        RubyTextSegment.Create("s0", "ふ", new TimeRange(1_000_000, 1_400_000), TimingStatus.Edited),
                        RubyTextSegment.Create("s1", "た", new TimeRange(1_400_000, 1_800_000), TimingStatus.Edited),
                    ],
                },
            ],
        };
        var ws = CreateWorkspace(line);
        string b = ws.Project.Lyrics.PrimaryBranchId;

        var prep = RubySegmentEditUseCase.PrepareSetSegments(ws, b, "l1", "t0", ["ふ", "た", "り"]);
        var committed = RubySegmentEditUseCase.Commit(ws, prep);
        Assert.True(committed.Accepted);
        var ruby = Assert.Single(ws.Project.Lyrics.Lines[0].RubyAnnotations);
        Assert.Equal("r0", ruby.Id);
        Assert.Equal(3, ruby.Segments.Length);
        Assert.Equal("s0", ruby.Segments[0].Id);
        Assert.Equal(new TimeRange(1_000_000, 1_400_000), ruby.Segments[0].Time);
        Assert.Equal("s1", ruby.Segments[1].Id);
        Assert.Equal(new TimeRange(1_400_000, 1_800_000), ruby.Segments[1].Time);
        Assert.Equal("り", ruby.Segments[2].Text);
        Assert.Null(ruby.Segments[2].Time);

        // Empty array removes ruby
        var prepClear = RubySegmentEditUseCase.PrepareSetSegments(ws, b, "l1", "t0", []);
        var cleared = RubySegmentEditUseCase.Commit(ws, prepClear);
        Assert.True(cleared.Accepted);
        Assert.Empty(ws.Project.Lyrics.Lines[0].RubyAnnotations);
    }

    [Fact]
    public void ApplyTextDiff_PreservesUnchangedTokensRubiesAndTimesInSingleCommit()
    {
        var line1 = new LyricLine
        {
            Id = "l1",
            Text = "青い空を見る",
            Time = new TimeRange(1_000_000, 6_000_000),
            TimingStatus = TimingStatus.Edited,
            Tokens =
            [
                LyricToken.Create("t0", "青", new TimeRange(1_000_000, 2_000_000), TimingStatus.Edited),
                LyricToken.Create("t1", "い", new TimeRange(2_000_000, 3_000_000), TimingStatus.Edited),
                LyricToken.Create("t2", "空", new TimeRange(3_000_000, 4_000_000), TimingStatus.Edited, phraseEndHint: true),
                LyricToken.Create("t3", "を", new TimeRange(4_000_000, 4_500_000), TimingStatus.Edited),
                LyricToken.Create("t4", "見", new TimeRange(4_500_000, 5_200_000), TimingStatus.Edited),
                LyricToken.Create("t5", "る", new TimeRange(5_200_000, 6_000_000), TimingStatus.Edited),
            ],
            RubyAnnotations =
            [
                RubyAnnotation.Create("r0", "t0", "あお"),
                RubyAnnotation.Create("r4", "t4", "み"),
            ],
        };
        var ws = CreateWorkspace(line1);
        string b = ws.Project.Lyrics.PrimaryBranchId;

        // Change "空" -> "海" in middle + append a new line
        var res = LyricContentEditUseCase.ApplyTextDiff(ws, b,
        [
            new LyricTextDiffLineInput("青い海を見る", "l1"),
            new LyricTextDiffLineInput("Hello, world!"),
        ]);

        Assert.True(res.Accepted);
        Assert.Equal(5, res.PreservedCount); // 青, い, を, 見, る preserved!
        Assert.Equal(2, ws.Project.Lyrics.Lines.Length);
        var updatedL1 = ws.Project.Lyrics.Lines[0];
        Assert.Equal("l1", updatedL1.Id);
        Assert.Equal("t0", updatedL1.Tokens[0].Id);
        Assert.Equal(new TimeRange(1_000_000, 2_000_000), updatedL1.Tokens[0].Time);
        Assert.Equal("海", updatedL1.Tokens[2].Text);
        Assert.Null(updatedL1.Tokens[2].Time); // changed char reset to untimed
        Assert.Equal("t4", updatedL1.Tokens[4].Id);
        Assert.Equal(new TimeRange(4_500_000, 5_200_000), updatedL1.Tokens[4].Time);
        Assert.Equal(2, updatedL1.RubyAnnotations.Length); // both r0 and r4 preserved!

        // New line uses Mixed contextual split (standalone punctuation & whitespace)
        var newL2 = ws.Project.Lyrics.Lines[1];
        Assert.Equal(["Hello", ",", " ", "world", "!"], newL2.Tokens.Select(t => t.Text).ToArray());

        // Single undo restores original single-line document
        ws.Undo();
        Assert.Single(ws.Project.Lyrics.Lines);
        Assert.Equal("青い空を見る", ws.Project.Lyrics.Lines[0].Text);
    }
}
