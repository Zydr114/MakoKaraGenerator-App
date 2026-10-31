using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Import;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    /// <summary>读取一次固定授权歌词的检测、完整解析报告和预览；没有工程或批准票据副作用。</summary>
    public async Task<BackendOperationResult<LyricsImportPlanResponse>> ReadLyricsImportPlanAsync(
        string clientId, LyricsImportPlanRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.AssetId) || request.Options is { } invalid && !Enum.IsDefined(invalid.SplitRule))
            return BackendOperationResult<LyricsImportPlanResponse>.Rejected(new(BackendErrorCode.InvalidRequest, "assetId 或导入选项无效。", "assetId"));
        if (!BeginTemplateOperation())
            return BackendOperationResult<LyricsImportPlanResponse>.Rejected(new(BackendErrorCode.SessionClosing, "服务正在关闭。"));
        try
        {
            // Reuse the existing authorized, hash-checked fixed source; its lease is released before parsing.
            LyricsImportSource source = await ReadFixedLyricsSourceAsync(clientId, request.AssetId).ConfigureAwait(false);
            return BackendOperationResult<LyricsImportPlanResponse>.Success(await Task.Run(() => BuildLyricsImportPlan(request, source)).ConfigureAwait(false));
        }
        catch (ResourceStoreException error)
        { return BackendOperationResult<LyricsImportPlanResponse>.Rejected(new(error.Code, error.Message, "assetId")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return BackendOperationResult<LyricsImportPlanResponse>.Rejected(new(BackendErrorCode.LyricsImportFailed, "固定歌词资源不可用、损坏或无法完成读取释放。", "assetId")); }
        finally { EndTemplateOperation(); }
    }

    private static LyricsImportPlanResponse BuildLyricsImportPlan(LyricsImportPlanRequest request, LyricsImportSource source)
    {
        LyricsImportDetection? detection = LyricsImportEditUseCase.Detect(source);
        LyricsImportFormatCandidateDto[] formats = detection is null ? [] :
            [.. detection.Matches.Select(match => new LyricsImportFormatCandidateDto(
                match.Importer.Id, match.Importer.DisplayName, match.Importer.SourceFormat, match.Reason))];
        // These are the original user-selectable overrides, not a second encoding detector.
        LyricsImportEncodingCandidateDto[] encodings =
        [
            new("utf-8", "原导入选项支持的 UTF-8 覆盖；省略 encodingName 沿原 BOM→UTF-8→GB18030→近似自动检测。"),
            new("gb18030", "原导入选项支持的 GB18030 覆盖。"),
        ];
        LyricsImportPlanResponse response = new(request.AssetId!, source.FileName, source.Length,
            detection?.Importer.Id, detection?.Reason, detection?.IsAmbiguous ?? false, formats,
            null, false, false, encodings, [], null, null, []);
        try
        {
            var options = request.Options ?? new();
            LyricsImportResult prepared = LyricsImportEditUseCase.Prepare(source, new()
            {
                ImporterId = options.ImporterId, EncodingName = options.EncodingName,
                SplitRule = (TokenSplitRule)options.SplitRule, EstimateSplitTimes = options.EstimateSplitTimes,
                SourceBranchId = options.SourceBranchId,
            });
            var report = prepared.Report;
            LyricBranch? branch = options.SourceBranchId is null ? prepared.Document.PrimaryBranch
                : prepared.Document.Branches.FirstOrDefault(value => value.Id == options.SourceBranchId);
            BackendErrorResponse? error = options.SourceBranchId is not null && branch is null
                ? new(BackendErrorCode.LyricsImportFailed, "指定来源不在解析文档中。", "options.sourceBranchId", FailureCode: "SourceBranchNotFound")
                : prepared.Document.Branches.All(value => value.Lines.IsEmpty)
                    ? new(BackendErrorCode.EmptyLyrics, "源文档没有可导入的歌词行。", "assetId", FailureCode: "EmptyImport") : null;
            return response with
            {
                EncodingName = report.EncodingName, HadByteOrderMark = report.HadByteOrderMark,
                IsEncodingAmbiguous = report.Issues.Any(issue => issue.Kind == LyricsImportIssueKind.EncodingAssumed),
                SourceBranches = [.. prepared.Document.Branches.Select(value =>
                    new LyricsImportBranchCandidateDto(value.Id, value.Name, (ProjectLyricBranchRole)value.Role, value.Lines.Length))],
                SelectedSourceBranchId = branch?.Id,
                Report = ProjectSession.ProjectImportReport(report),
                PreviewLines = branch is null ? [] : ProjectSession.ProjectImportPreview(branch),
                PreparationError = error,
            };
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or System.Xml.XmlException or FormatException or OverflowException)
        {
            return response with { PreparationError = new(BackendErrorCode.LyricsImportFailed,
                "歌词解析未能完成；可改选原导入器或编码后重试。", "options", FailureCode: detection is null ? "UnsupportedLyricsFormat" : "LyricsParseFailed") };
        }
    }
}
