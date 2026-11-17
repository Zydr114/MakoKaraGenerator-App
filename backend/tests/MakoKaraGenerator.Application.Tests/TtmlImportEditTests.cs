using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Export;
using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class TtmlImportEditTests
{
    private static LyricsImportResult Prepared(bool multiple = false, string? link = null)
    {
        string tokenId = new('t', 128);
        string rubyId = new('r', 128);
        LyricToken token = LyricToken.Create(tokenId, "字");
        RubyAnnotation ruby = new() { Id = rubyId, BaseTokenId = tokenId, Text = "じ", Segments = [RubyTextSegment.Create(new string('s', 128), "じ")] };
        LyricLine line = LyricLine.Create(new string('l', 128), "独立行正文", tokens: [token]) with { RubyAnnotations = [ruby], PrimaryLineId = link };
        LyricsDocument source = LyricsDocument.Create([line]);
        if (multiple) source = source with { Branches = source.Branches.Add(LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
            [line with { Id = "translated", Tokens = [], RubyAnnotations = [], PrimaryLineId = line.Id }], "ja", LyricBranchTimingMode.FollowPrimary)) };
        using MemoryStream output = new();
        new TtmlProjectExporter().Export(source, output, LyricsExportOptions.Default);
        return LyricsImportEditUseCase.Prepare(new("source.ttml", output.ToArray()), new());
    }

    [Fact]
    public void Repeated_project_import_remaps_every_identity_and_binding_to_bounded_targets()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Project.Create("导入"));
        LyricsImportResult prepared = Prepared();
        string? previousSegment = null;
        for (int i = 0; i < 2; i++)
        {
            var baseline = new LyricsImportEditBaseline(workspace, workspace.Generation, null, ExpectedLyrics: workspace.Project.Lyrics);
            PreparedLyricsImportEdit candidate = LyricsImportEditUseCase.PrepareImportCandidate(LyricsImportEditUseCase.Capture(workspace), baseline,
                prepared, "注音来源" + i, LyricBranchRole.Annotation, new LyricsImportPreparationGuard(4 * 1024 * 1024));
            Assert.True(candidate.Result.Accepted, candidate.Result.Error?.Detail);
            Assert.Equal(0 + i, workspace.History.UndoCount);
            Assert.True(LyricsImportEditUseCase.CommitCandidate(workspace, candidate).Accepted);
            LyricLine imported = workspace.Project.Lyrics.Branches[^1].Lines[0];
            RubyAnnotation ruby = Assert.Single(imported.RubyAnnotations);
            Assert.Equal(imported.Tokens[0].Id, ruby.BaseTokenId);
            Assert.NotEqual(previousSegment, ruby.Segments[0].Id);
            Assert.NotEqual(prepared.Document.Lines[0].RubyAnnotations[0].Segments[0].Id, ruby.Segments[0].Id);
            Assert.Equal("独立行正文", imported.Text);
            Assert.All(new[] { imported.Id, imported.Tokens[0].Id, ruby.Id, ruby.Segments[0].Id }, id => Assert.InRange(id.Length, 1, 128));
            previousSegment = ruby.Segments[0].Id;
        }
        string[] ids = [.. workspace.Project.Lyrics.Branches.Select(branch => branch.Id), .. workspace.Project.Lyrics.AllLines.Select(line => line.Id),
            .. workspace.Project.Lyrics.EnumerateTokens().Select(pair => pair.Token.Id), .. workspace.Project.Lyrics.AllLines.SelectMany(line => line.RubyAnnotations)
                .SelectMany(ruby => ruby.Segments.Select(segment => segment.Id).Prepend(ruby.Id))];
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, workspace.History.UndoCount);
    }

    [Fact]
    public void Multiple_source_import_requires_explicit_selection_and_keeps_omitted_primary_link_with_warning()
    {
        LyricsImportResult prepared = Prepared(multiple: true);
        Assert.Equal(2, prepared.Document.Branches.Length);
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Project.Create("目标"));
        var snapshot = LyricsImportEditUseCase.Capture(workspace);
        var baseline = new LyricsImportEditBaseline(workspace, workspace.Generation, null, ExpectedLyrics: workspace.Project.Lyrics);
        Assert.Equal(LyricsImportEditErrorCode.SourceBranchRequired,
            LyricsImportEditUseCase.PrepareImportCandidate(snapshot, baseline, prepared, "翻译").Result.Error!.Code);
        Assert.Equal(LyricsImportEditErrorCode.SourceBranchNotFound,
            LyricsImportEditUseCase.PrepareImportCandidate(snapshot, baseline, prepared, "翻译", sourceBranchId: "gone").Result.Error!.Code);
        PreparedLyricsImportEdit selected = LyricsImportEditUseCase.PrepareImportCandidate(snapshot, baseline, prepared, "翻译", sourceBranchId: "translation");
        Assert.True(selected.Result.Accepted, selected.Result.Error?.Detail);
        LyricLine line = selected.Result.Project.Lyrics.Branches[^1].Lines[0];
        Assert.Equal(prepared.Document.Lines[0].Id, line.PrimaryLineId);
        Assert.Contains(selected.ImportReport!.Issues, issue => issue.Severity == IssueSeverity.Warning && issue.Message.Contains("SourceBranchLinkUnavailable", StringComparison.Ordinal));
        Assert.Contains(selected.ImportReport.DiscardedFeatures, feature => feature.Contains("省略其它来源", StringComparison.Ordinal));
        Assert.Equal(1, selected.ImportReport.LineCount);
        Assert.Equal(0, workspace.History.UndoCount);
        var primaryBaseline = new LyricsImportEditBaseline(workspace, workspace.Generation, "primary", workspace.Project.Lyrics.PrimaryBranch);
        Assert.Equal(LyricsImportEditErrorCode.DomainValidationFailed,
            LyricsImportEditUseCase.PrepareImportCandidate(snapshot, primaryBaseline, prepared, sourceBranchId: "translation").Result.Error!.Code);
    }

    [Fact]
    public void All_sources_import_is_one_undo_and_keeps_existing_branches()
    {
        LyricsImportResult prepared = Prepared(multiple: true, link: null);
        LyricLine linked = prepared.Document.Branches[1].Lines[0];
        Assert.Equal(prepared.Document.Lines[0].Id, linked.PrimaryLineId);
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Project.Create("目标"));
        Assert.Empty(workspace.Project.Lyrics.Lines);
        var baseline = new LyricsImportEditBaseline(workspace, workspace.Generation, null, ExpectedLyrics: workspace.Project.Lyrics);
        var snapshot = LyricsImportEditUseCase.Capture(workspace);
        Assert.Equal(LyricsImportEditErrorCode.InvalidTarget,
            LyricsImportEditUseCase.PrepareImportAllCandidate(snapshot, baseline, prepared, sourceBranchId: "primary").Result.Error!.Code);
        PreparedLyricsImportEdit candidate = LyricsImportEditUseCase.PrepareImportAllCandidate(snapshot, baseline, prepared);
        Assert.True(candidate.Result.Accepted, candidate.Result.Error?.Detail);
        Assert.Equal(2, candidate.Result.LineCount);
        Assert.Equal(3, candidate.Result.Project.Lyrics.Branches.Length);
        Assert.Empty(candidate.Result.Project.Lyrics.Lines);
        Assert.Equal(LyricBranchRole.Primary, candidate.Result.Project.Lyrics.PrimaryBranch!.Role);
        Assert.All(candidate.Result.Project.Lyrics.Branches.Skip(1), branch => Assert.NotEqual(LyricBranchRole.Primary, branch.Role));
        LyricLine importedTranslation = candidate.Result.Project.Lyrics.Branches[^1].Lines[0];
        LyricLine importedMain = candidate.Result.Project.Lyrics.Branches[1].Lines[0];
        Assert.Equal(importedMain.Id, importedTranslation.PrimaryLineId);
        Assert.NotEqual(prepared.Document.Lines[0].Id, importedMain.Id);
        Assert.Equal(importedMain.Tokens[0].Id, Assert.Single(importedMain.RubyAnnotations).BaseTokenId);
        Assert.Contains(candidate.ImportReport!.Issues, issue => issue.Message.Contains("未替换工程已有来源", StringComparison.Ordinal));
        Assert.Contains(candidate.ImportReport.Issues, issue => issue.Message.Contains("跨来源主行关联已随全部来源导入重映射", StringComparison.Ordinal));
        Assert.True(LyricsImportEditUseCase.CommitCandidate(workspace, candidate).Accepted);
        Assert.Equal(1, workspace.History.UndoCount);
        workspace.Undo();
        Assert.Empty(workspace.Project.Lyrics.Lines);
        Assert.Single(workspace.Project.Lyrics.Branches);
        workspace.Redo();
        Assert.Equal(3, workspace.Project.Lyrics.Branches.Length);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Equal(0, workspace.History.RedoCount);
    }

    [Fact]
    public void All_sources_import_handles_document_with_empty_primary_branch()
    {
        LyricsDocument doc = new()
        {
            PrimaryBranchId = "primary",
            Branches =
            [
                LyricBranch.Create("primary", "空主", LyricBranchRole.Primary),
                LyricBranch.Create("secondary", "副歌词", LyricBranchRole.Custom, [LyricLine.Create("s1", "副行")]),
            ],
        };
        LyricsImportResult prepared = new(doc, Prepared(multiple: false, link: null).Report);
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Project.Create("目标"));
        var baseline = new LyricsImportEditBaseline(workspace, workspace.Generation, null, ExpectedLyrics: workspace.Project.Lyrics);
        var snapshot = LyricsImportEditUseCase.Capture(workspace);
        PreparedLyricsImportEdit candidate = LyricsImportEditUseCase.PrepareImportAllCandidate(snapshot, baseline, prepared);
        Assert.True(candidate.Result.Accepted, candidate.Result.Error?.Detail);
        Assert.Equal(1, candidate.Result.LineCount);
        Assert.Equal(2, candidate.Result.Project.Lyrics.Branches.Length);
        Assert.Equal("副行", Assert.Single(candidate.Result.Project.Lyrics.Branches[1].Lines).Text);
    }

    [Fact]
    public void Segment_id_collection_and_new_mapping_are_charged_before_any_history()
    {
        LyricsImportResult prepared = Prepared();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Project.Create("配额", lyrics: prepared.Document));
        var snapshot = LyricsImportEditUseCase.Capture(workspace);
        var baseline = new LyricsImportEditBaseline(workspace, workspace.Generation, "primary", workspace.Project.Lyrics.PrimaryBranch);
        Assert.Throws<LyricsImportPreparationLimitException>(() => LyricsImportEditUseCase.PrepareImportCandidate(snapshot, baseline,
            prepared, preparationGuard: new LyricsImportPreparationGuard(1500)));
        Assert.Same(snapshot.Project, workspace.Project);
        Assert.Equal(0, workspace.History.UndoCount);
    }
}
