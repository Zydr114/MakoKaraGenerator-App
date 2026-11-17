using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Lyrics.Import;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    // Optional deterministic observer for in-process integration tests; production composition leaves it unset.
    internal Func<Task>? ImportPreparationCheckpoint { get; set; }
    internal Func<Task>? EditPreparationCheckpoint { get; set; }
    internal Func<ResourceReadLease, ResourceReadLease>? ImportReadLeaseDecorator { get; set; }
    private readonly object _lyricsPreparationGate = new();
    private readonly HashSet<string> _lyricsPreparations = new(StringComparer.Ordinal);

    private async Task<ProjectCommandExecutionResult> ExecuteImportCommandAsync(string clientId, ProjectSession session, ProjectCommandRequest request)
    {
        var captureResult = await session.CaptureImportAsync(request).ConfigureAwait(false);
        if (captureResult.Error is { } captureError) return ProjectCommandExecutionResult.Rejected(captureError);
        var capture = captureResult.Capture!;
        lock (_lyricsPreparationGate)
        {
            if (_lyricsPreparations.Count >= 2 || !_lyricsPreparations.Add(clientId))
                return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.RequestQuotaExceeded, "歌词准备名额已用尽，请稍后以新请求重试。", "command"));
        }
        try
        {
            // The receipt owner pins its client and remains in accepted-operation/shutdown drain.
            // Fixed bytes and all lease cleanup precede candidate commit; caller cancellation only ends waiting.
            LyricsImportSource? source = null;
            if (capture.Command is ImportAssetCommandDto import) source = await ReadFixedLyricsSourceAsync(clientId, import.AssetId!).ConfigureAwait(false);
            if (ImportPreparationCheckpoint is { } checkpoint) await checkpoint().ConfigureAwait(false);
            var work = await Task.Run(() =>
            {
                long issueBytes = 2;
                HashSet<LyricsImportIssue> observed = new(ReferenceEqualityComparer.Instance);
                var guard = new LyricsImportPreparationGuard(64L * 1024 * 1024, issue =>
                {
                    if (!observed.Add(issue)) return;
                    // A necessary lower bound for the complete receipt, checked before retaining each issue.
                    if ((long)issue.RawText.Length + issue.Message.Length > capture.ReceiptLimit) throw new LyricsImportPreparationLimitException();
                    issueBytes += JsonSerializer.SerializeToUtf8Bytes(ProjectSession.ProjectImportIssue(issue)).Length + 1L;
                    if (issueBytes > capture.ReceiptLimit) throw new LyricsImportPreparationLimitException();
                });
                var rowBudget = new ProjectSession.ImportRowBudget(capture);
                LyricsImportResult? parsed = null; PreparedLyricsImportEdit candidate;
                if (capture.Command is ImportAssetCommandDto file)
                {
                    var options = file.Options ?? new();
                    parsed = LyricsImportEditUseCase.Prepare(source!, new() { ImporterId = options.ImporterId, EncodingName = options.EncodingName,
                        SplitRule = (TokenSplitRule)options.SplitRule, EstimateSplitTimes = options.EstimateSplitTimes, SourceBranchId = options.SourceBranchId }, capture.AudioDuration, guard);
                    candidate = file.Destination is AllSourcesLyricsDestinationDto
                        ? LyricsImportEditUseCase.PrepareImportAllCandidate(capture.Snapshot, capture.Baseline, parsed, options.SourceBranchId, guard, rowBudget.Observe)
                        : LyricsImportEditUseCase.PrepareImportCandidate(capture.Snapshot, capture.Baseline, parsed, capture.Name, capture.Role, guard, rowBudget.Observe, options.SourceBranchId);
                }
                else candidate = LyricsImportEditUseCase.PrepareAppendCandidate(capture.Snapshot, capture.Baseline,
                    ((AppendTextCommandDto)capture.Command).Text!, capture.Name, guard, rowBudget.Observe);
                if (!candidate.Result.Accepted) return (Candidate: candidate, Feedback: (LyricsImportCommandResultDto?)null);
                var feedback = ProjectSession.BudgetImportCandidate(capture, candidate, candidate.ImportReport);
                return (Candidate: candidate, Feedback: feedback);
            }).ConfigureAwait(false);
            if (!work.Candidate.Result.Accepted) return ProjectCommandExecutionResult.Rejected(ProjectSession.ImportFailure(work.Candidate.Result.Error,
                work.Candidate.Result.Error?.Code is LyricsImportEditErrorCode.SourceBranchRequired or LyricsImportEditErrorCode.SourceBranchNotFound
                    ? "command.options.sourceBranchId" : work.Candidate.Result.Error?.Code == LyricsImportEditErrorCode.EmptyText ? "command.text" : "command"));
            return await session.CommitImportAsync(capture, work.Candidate, work.Feedback!).ConfigureAwait(false);
        }
        catch (ProjectSession.InvalidImportIdentifierException) { return ProjectCommandExecutionResult.Rejected(ProjectSession.InvalidImport("command")); }
        catch (Exception error) when (error is LyricsImportPreparationLimitException || ProjectSession.IsImportReadBudgetFailure(error))
        { return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.ResourceTooLarge, "歌词准备、完整读投影或完整回执超过预算；工程未修改。", "command")); }
        catch (ResourceStoreException error)
        { return ProjectCommandExecutionResult.Rejected(new(error.Code, error.Message, "command.assetId")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.LyricsImportFailed, "固定歌词资源不可用、已损坏或无法完成读取释放。", "command.assetId")); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or System.Xml.XmlException or FormatException or OverflowException)
        { return ProjectCommandExecutionResult.Rejected(new(BackendErrorCode.LyricsImportFailed, "歌词解析未能完成。", "command")); }
        finally { lock (_lyricsPreparationGate) _lyricsPreparations.Remove(clientId); }
    }

    private async Task<LyricsImportSource> ReadFixedLyricsSourceAsync(string clientId, string assetId)
    {
        ResourceReadLease? read = null; Exception? primary = null;
        try
        {
            read = _resources.AcquireRead(clientId, assetId);
            if (ImportReadLeaseDecorator is { } decorate) read = decorate(read);
            StoredAsset asset = read.Asset;
            if (!(asset.ContentType.StartsWith("text/", StringComparison.Ordinal) || asset.ContentType is "application/octet-stream" or "application/ttml+xml"))
                throw new ResourceStoreException(BackendErrorCode.UnsupportedMediaType, "歌词资源必须是文本或固定二进制上传。", "command.assetId");
            ProjectDirectoryHandles.EnsurePathHasNoLinks(asset.StoragePath);
            if (asset.Size < 0 || asset.Size > _resources.MaxAssetBytes || read.Stream.Length != asset.Size) throw new IOException("固定资源大小改变。");
            byte[] bytes = new byte[checked((int)asset.Size)];
            await read.Stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
            if (read.Stream.ReadByte() != -1 || read.Stream.Length != asset.Size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != asset.Sha256)
                throw new IOException("固定资源完整字节不匹配。");
            ProjectDirectoryHandles.EnsurePathHasNoLinks(asset.StoragePath);
            return new(asset.FileName, bytes);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try { if (read is not null) await read.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["LyricsReadCleanupFailure"] = cleanup; }
        }
    }
}
