using System.Collections.Immutable;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class Phase1CommandHostingTests
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private static (ProjectSession Session, string BranchId) CreateSession(params LyricLine[] lines)
    {
        var project = Project.Create("p1-host",
            Composition.Create(1920, 1080, FrameRate.Fps30, outputDurationUs: 60_000_000),
            LyricsDocument.Create(lines));
        var workspace = ProjectWorkspace.Adopt(project);
        var session = new ProjectSession("sess-p1", "host-p1", "client-p1", workspace, audioAssetId: string.Empty,
            audioDurationUs: 60_000_000, TimeProvider.System, new ProjectCommandRetentionOptions(),
            new ClientEventJournal("host-p1", 128));
        return (session, project.Lyrics.PrimaryBranchId);
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

    private static async Task<ProjectCommandResponse> SendJsonAsync(ProjectSession session, string revision, string commandJson)
    {
        var dto = JsonSerializer.Deserialize<ProjectCommandDto>(commandJson, WireJson)!;
        var res = await session.ExecuteCommandAsync(new ProjectCommandRequest(revision, dto), CancellationToken.None);
        Assert.True(res.Accepted, res.Error?.Message);
        return res.Response!;
    }

    [Fact]
    public async Task AllSevenPhase1Commands_WireRoundTripAndExecution_SucceedCleanly()
    {
        var (session, branchId) = CreateSession(UntimedLine("l1", "夢 光"));
        string rev = "0";

        // 1. lyrics.strikeBeat on untimed line
        var r1 = await SendJsonAsync(session, rev, $$"""
        {"type":"lyrics.strikeBeat","branchId":"{{branchId}}","openTarget":{"branchId":"{{branchId}}","lineId":"l1","tokenId":"l1-t0"},"playheadUs":"1000000"}
        """);
        rev = r1.ProjectRevision;
        Assert.True(r1.Changed);
        Assert.True(r1.TimingResult?.EstimatedPlaceholder);
        Assert.Equal(false, r1.TimingResult?.CloseGuardApplied);

        // 2. lyrics.strikeBeat with >1.5s gap after Japanese pause space -> CloseGuardApplied=true
        var r2 = await SendJsonAsync(session, rev, $$"""
        {"type":"lyrics.strikeBeat","branchId":"{{branchId}}","closeTarget":{"branchId":"{{branchId}}","lineId":"l1","tokenId":"l1-t0"},"openTarget":{"branchId":"{{branchId}}","lineId":"l1","tokenId":"l1-t2"},"playheadUs":"3000000"}
        """);
        rev = r2.ProjectRevision;
        Assert.Equal(true, r2.TimingResult?.CloseGuardApplied);

        // 3. lyrics.closeBeat
        var r3 = await SendJsonAsync(session, rev, $$"""
        {"type":"lyrics.closeBeat","target":{"branchId":"{{branchId}}","lineId":"l1","tokenId":"l1-t2"},"playheadUs":"3800000"}
        """);
        rev = r3.ProjectRevision;
        Assert.Equal(false, r3.TimingResult?.EstimatedPlaceholder);

        // 4. lyrics.togglePhraseEndHint + verify ReadModel exposes phraseEndHint
        var r4 = await SendJsonAsync(session, rev, $$"""
        {"type":"lyrics.togglePhraseEndHint","target":{"branchId":"{{branchId}}","lineId":"l1","tokenId":"l1-t0"} }
        """);
        rev = r4.ProjectRevision;
        var win = await session.ReadLyricsWindowAsync(branchId, rev, 0, 10, CancellationToken.None);
        Assert.True(win.Accepted);
        Assert.Equal(false, win.Response!.Lines[0].Tokens[0].PhraseEndHint); // flipped auto(true) -> false
        Assert.Null(win.Response.Lines[0].Tokens[2].PhraseEndHint);

        // 5. lyrics.setRubySegments
        var r5 = await SendJsonAsync(session, rev, $$"""
        {"type":"lyrics.setRubySegments","target":{"branchId":"{{branchId}}","lineId":"l1","tokenId":"l1-t0"},"segments":["ゆ","め"]}
        """);
        rev = r5.ProjectRevision;
        Assert.Equal(2, r5.RubyResult?.ChangedSegmentCount);
        Assert.NotNull(r5.RubyResult?.Target);

        // 6. lyrics.splitLine
        var r6 = await SendJsonAsync(session, rev, $$"""
        {"type":"lyrics.splitLine","target":{"branchId":"{{branchId}}","lineId":"l1"},"tokenIndex":2}
        """);
        rev = r6.ProjectRevision;
        string createdLineId = r6.Feedback!.CreatedLineId!;
        Assert.NotEmpty(createdLineId);

        // 7. lyrics.mergeLines
        var r7 = await SendJsonAsync(session, rev, $$"""
        {"type":"lyrics.mergeLines","target":{"branchId":"{{branchId}}","lineId":"{{createdLineId}}"} }
        """);
        rev = r7.ProjectRevision;
        Assert.Equal(createdLineId, r7.Feedback?.DeletedLineId);

        // 8. lyrics.applyTextDiff
        var r8 = await SendJsonAsync(session, rev, $$"""
        {"type":"lyrics.applyTextDiff","branchId":"{{branchId}}","lines":[{"text":"夢 星","sourceLineId":"l1"},{"text":"新行"}]}
        """);
        rev = r8.ProjectRevision;
        Assert.True(r8.Changed);
        Assert.Equal(2, r8.Feedback?.PreservedTokenCount); // "夢" and " " preserved
        var winAfter = await session.ReadLyricsWindowAsync(branchId, rev, 0, 10, CancellationToken.None);
        Assert.Equal(2, winAfter.Response!.Lines.Length);
        Assert.Single(winAfter.Response.Lines[0].RubyAnnotations); // "夢"'s ruby preserved!
    }

    [Fact]
    public void BeatCommandJson_RejectsHalfPopulatedRubyTargetAndUnknownFields()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(
            """{"type":"lyrics.closeBeat","target":{"branchId":"primary","lineId":"l1","tokenId":"t1","rubyId":"r1"},"playheadUs":"1000"}""",
            WireJson));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(
            """{"type":"lyrics.splitLine","target":{"branchId":"primary","lineId":"l1"},"tokenIndex":1,"extra":true}""",
            WireJson));
    }
}
