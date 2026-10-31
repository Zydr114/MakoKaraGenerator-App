using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal static bool IsRubyCommand(ProjectCommandDto command) => command.Type is "lyrics.setRubySegmentTime"
        or "lyrics.clearRubySegmentTime" or "lyrics.setRubySegmentText" or "lyrics.addRubySegment"
        or "lyrics.removeRubySegment" or "lyrics.reorderRubySegments" or "lyrics.setRubySegmentTimes";

    internal static BackendErrorResponse? CheckRubyIntent(ProjectCommandDto command)
    {
        if (!IsRubyCommand(command)) return null;
        BackendErrorResponse Invalid() => new(BackendErrorCode.InvalidRequest, "注音片段命令的类型、字段、目标或数量不合法。", "command");
        bool Group(RubyGroupTargetDto? target) => target is not null && IsStableId(target.BranchId)
            && IsStableId(target.LineId) && IsStableId(target.TokenId) && IsStableId(target.RubyId);
        bool Segment(RubySegmentTargetDto? target) => target is not null
            && Group(new(target.BranchId, target.LineId, target.TokenId, target.RubyId)) && IsStableId(target.SegmentId);
        if (command.Target is not null || command.StartUs is not null || command.EndUs is not null || command.AnalysisJobId is not null) return Invalid();
        bool valid = command switch
        {
            SetRubySegmentTimeCommandDto value => value.Type == "lyrics.setRubySegmentTime" && Segment(value.RubyTarget)
                && TryParseMicroseconds(value.SegmentStartUs, out _) && TryParseMicroseconds(value.SegmentEndUs, out _),
            ClearRubySegmentTimeCommandDto value => value.Type == "lyrics.clearRubySegmentTime" && Segment(value.RubyTarget),
            SetRubySegmentTextCommandDto value => value.Type == "lyrics.setRubySegmentText" && Segment(value.RubyTarget) && value.Text is not null,
            AddRubySegmentCommandDto value => value.Type == "lyrics.addRubySegment" && Group(value.RubyTarget) && value.Text is not null
                && (value.AfterSegmentId is null || IsStableId(value.AfterSegmentId)),
            RemoveRubySegmentCommandDto value => value.Type == "lyrics.removeRubySegment" && Segment(value.RubyTarget),
            ReorderRubySegmentsCommandDto value => value.Type == "lyrics.reorderRubySegments" && Group(value.RubyTarget)
                && value.OrderedSegmentIds is { Length: > 0 and <= ProjectReadModelLimits.MaxItems } ids && ids.All(IsStableId),
            SetRubySegmentTimesCommandDto value => value.Type == "lyrics.setRubySegmentTimes"
                && value.Changes is { Length: > 0 and <= RubySegmentEditingLimits.MaxChanges } changes
                && changes.All(item => item is not null && Segment(item.Target) && TryParseMicroseconds(item.StartUs, out _) && TryParseMicroseconds(item.EndUs, out _)),
            _ => false,
        };
        if (!valid) return Invalid();
        if (command is SetRubySegmentTextCommandDto { Text.Length: > RubySegmentEditingLimits.MaxIntentBytes }
            or AddRubySegmentCommandDto { Text.Length: > RubySegmentEditingLimits.MaxIntentBytes }) return RubyBudget();
        try
        {
            // Count encoded JSON before fingerprint/candidate allocation, including escaped Unicode and all parent IDs.
            using var counter = new RubyIntentStream(); JsonSerializer.Serialize(counter, command, ReadModelJson);
        }
        catch (ReadModelBudgetExceededException) { return RubyBudget(); }
        return null;
    }

    private static ProjectCommandDto CanonicalRubyCommand(ProjectCommandDto command)
    {
        string? Time(string? value) => TryParseMicroseconds(value, out long time) ? time.ToString(CultureInfo.InvariantCulture) : value;
        return command switch
        {
            SetRubySegmentTimeCommandDto value => value with { SegmentStartUs = Time(value.SegmentStartUs), SegmentEndUs = Time(value.SegmentEndUs) },
            SetRubySegmentTimesCommandDto value => value with { Changes = value.Changes?.Select(item => item is null ? null! : item with { StartUs = Time(item.StartUs), EndUs = Time(item.EndUs) }).ToArray() },
            ReorderRubySegmentsCommandDto value => value with { OrderedSegmentIds = value.OrderedSegmentIds?.ToArray() },
            _ => command,
        };
    }

    private bool TryExecuteRubyCommand(Project before, ProjectCommandDto command, out Project after,
        out ProjectChangeSetDto? changes, out RubySegmentCommandResultDto? feedback, out BackendErrorResponse? error)
    {
        after = before; changes = null; feedback = null; error = null;
        if (!IsRubyCommand(command)) return false;
        if (CheckRubyIntent(command) is { } invalid) { error = invalid; return true; }
        RubyGroupTargetDto? group = null;
        string? segment = null;
        var targets = new List<RubyEditTarget>();
        RubySegmentEditBaseline? Baseline(RubyGroupTargetDto dto)
        {
            RubyEditTarget target = new(dto.BranchId, dto.LineId, dto.TokenId, dto.RubyId);
            RubyAnnotation? ruby = RubySegmentEditUseCase.Read(before, target);
            if (ruby is null) return null;
            if (command is not RemoveRubySegmentCommandDto)
            {
                var sourceLine = before.Lyrics.Branches.First(branch => branch.Id == target.BranchId).Lines.First(line => line.Id == target.LineId);
                long items = 2L + sourceLine.Tokens.Length + sourceLine.RubyAnnotations.Length;
                foreach (var sourceRuby in sourceLine.RubyAnnotations) items += sourceRuby.Segments.Length;
                if (items + (command is AddRubySegmentCommandDto ? 1 : 0) > ProjectReadModelLimits.MaxItems) throw new ReadModelBudgetExceededException();
            }
            targets.Add(target);
            return new(Workspace, Workspace.Generation, target, ruby);
        }
        RubyGroupTargetDto Group(RubySegmentTargetDto dto) { segment = dto.SegmentId; return new(dto.BranchId, dto.LineId, dto.TokenId, dto.RubyId); }
        group = command switch
        {
            SetRubySegmentTimeCommandDto value => Group(value.RubyTarget!),
            ClearRubySegmentTimeCommandDto value => Group(value.RubyTarget!),
            SetRubySegmentTextCommandDto value => Group(value.RubyTarget!),
            AddRubySegmentCommandDto value => value.RubyTarget,
            RemoveRubySegmentCommandDto value => Group(value.RubyTarget!),
            ReorderRubySegmentsCommandDto value => value.RubyTarget,
            _ => null,
        };
        PreparedRubySegmentEdit prepared;
        try
        {
            if (command is SetRubySegmentTimesCommandDto batch)
            {
                var items = ImmutableArray.CreateBuilder<RubySegmentTimeEdit>(batch.Changes!.Length);
                var seen = new HashSet<RubySegmentTargetDto>();
                // Duplicate targets are an input error and must not be masked by stale target lookup.
                foreach (var item in batch.Changes) if (!seen.Add(item.Target!)) { error = RubyRejected("DuplicateTarget", "command.changes"); return true; }
                foreach (var item in batch.Changes)
                {
                    var target = item.Target!;
                    var baseline = Baseline(new(target.BranchId, target.LineId, target.TokenId, target.RubyId));
                    if (baseline is null) { error = RubyRejected("TargetStale", "command.changes"); return true; }
                    items.Add(new(baseline, target.SegmentId, long.Parse(item.StartUs!, CultureInfo.InvariantCulture), long.Parse(item.EndUs!, CultureInfo.InvariantCulture)));
                }
                prepared = RubySegmentEditUseCase.PrepareBatch(Workspace, items.ToImmutable());
            }
            else
            {
                if (command is SetRubySegmentTextCommandDto { Text: { } text }) ValidateContentTextBudget(text);
                if (command is AddRubySegmentCommandDto { Text: { } addText }) ValidateContentTextBudget(addText);
                var baseline = Baseline(group!);
                if (baseline is null) { error = RubyRejected("TargetStale", "command.target"); return true; }
                // Reject known item/text overflow before cloning a potentially large segment sequence.
                if (command is not RemoveRubySegmentCommandDto)
                {
                    long textLength = baseline.Ruby.Text.Length;
                    if (command is AddRubySegmentCommandDto add) textLength += add.Text!.Length;
                    if (command is SetRubySegmentTextCommandDto edit)
                        textLength += edit.Text!.Length - (baseline.Ruby.Segments.FirstOrDefault(value => value.Id == segment)?.Text.Length ?? 0);
                    if (textLength > ProjectReadModelLimits.MaxResponseBytes) throw new ReadModelBudgetExceededException();
                }
                ReadModelBudget identifiers = new();
                prepared = command switch
                {
                    SetRubySegmentTimeCommandDto value => RubySegmentEditUseCase.PrepareSetTime(Workspace, baseline, segment!, long.Parse(value.SegmentStartUs!, CultureInfo.InvariantCulture), long.Parse(value.SegmentEndUs!, CultureInfo.InvariantCulture)),
                    ClearRubySegmentTimeCommandDto => RubySegmentEditUseCase.PrepareClearTime(Workspace, baseline, segment!),
                    SetRubySegmentTextCommandDto value => RubySegmentEditUseCase.PrepareSetText(Workspace, baseline, segment!, value.Text!),
                    AddRubySegmentCommandDto value => RubySegmentEditUseCase.PrepareAdd(Workspace, baseline, value.Text!, value.AfterSegmentId, id => { identifiers.Item(); identifiers.Text(id); }),
                    RemoveRubySegmentCommandDto => RubySegmentEditUseCase.PrepareRemove(Workspace, baseline, segment!),
                    ReorderRubySegmentsCommandDto value => RubySegmentEditUseCase.PrepareReorder(Workspace, baseline, [.. value.OrderedSegmentIds!]),
                    _ => throw new InvalidOperationException(),
                };
            }
            RubySegmentEditResult preview = prepared.Result;
            if (!preview.Accepted) { error = RubyRejected(preview.Error!.Value.ToString(), RubyErrorField(command, preview.Error), preview.Detail); return true; }
            if (preview.Changed)
            {
                long bytes = 0;
                foreach (var target in targets.DistinctBy(target => (target.BranchId, target.LineId)))
                {
                    var branch = preview.Project.Lyrics.Branches.First(branch => branch.Id == target.BranchId);
                    int index = Array.FindIndex(branch.Lines.ToArray(), line => line.Id == target.LineId);
                    var oldLine = before.Lyrics.Branches.First(branch => branch.Id == target.BranchId).Lines.First(line => line.Id == target.LineId);
                    if (oldLine == branch.Lines[index]) continue;
                    long length = ValidatePreparedTokenLineBudget(branch, index, branch.Lines[index]);
                    if (length > RubySegmentEditingLimits.MaxPreparedBytes - bytes) throw new ReadModelBudgetExceededException();
                    bytes += length;
                }
            }
            changes = RubyChanges(preview.Changed, targets, command is SetRubySegmentTimeCommandDto or ClearRubySegmentTimeCommandDto or SetRubySegmentTimesCommandDto);
            string? selected = command is SetRubySegmentTimeCommandDto or ClearRubySegmentTimeCommandDto or SetRubySegmentTextCommandDto
                ? segment : preview.SelectedSegmentId;
            feedback = new(preview.ChangedSegmentCount, group, selected, preview.CreatedSegmentId,
                command is AddRubySegmentCommandDto or RemoveRubySegmentCommandDto);
            ValidateRubyReceipt(preview, command, changes, feedback);
            RubySegmentEditResult result = RubySegmentEditUseCase.Commit(Workspace, prepared);
            if (!result.Accepted) { error = RubyRejected(result.Error!.Value.ToString(), "command.target", result.Detail); return true; }
            after = result.Project;
            return true;
        }
        catch (ReadModelBudgetExceededException) { error = RubyBudget(); return true; }
    }
    private static ProjectChangeSetDto RubyChanges(bool changed, IEnumerable<RubyEditTarget> targets, bool timing)
    {
        if (!changed) return new([], [], [], [], false);
        // TokenIds remain base-token identities; segment identities live only in the dedicated result and read model.
        var result = new ProjectChangeSetDto(timing ? ["lyricsRubyTiming", "timeline", "renderInputs"] : ["lyricsRubyContent", "lyricsStructure", "timeline", "renderInputs"],
            [.. targets.Select(target => target.BranchId).Distinct(StringComparer.Ordinal)],
            [.. targets.Select(target => target.LineId).Distinct(StringComparer.Ordinal)],
            [.. targets.Select(target => target.TokenId).Distinct(StringComparer.Ordinal)], true);
        return JsonSerializer.SerializeToUtf8Bytes(result).Length > ProjectCommandLimits.MaxChangeSetBytes
            ? result with { BranchIds = [], LineIds = [], TokenIds = [] } : result;
    }
    private void ValidateRubyReceipt(RubySegmentEditResult preview, ProjectCommandDto command, ProjectChangeSetDto changes, RubySegmentCommandResultDto feedback)
    {
        string revision = preview.Changed ? checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture) : RevisionText;
        var upper = new ProjectCommandResponse(ProjectSessionId, RevisionText, revision, preview.Changed, false, false, false,
            preview.Changed ? command.Type : Workspace.UndoName, preview.Changed ? null : Workspace.RedoName, changes,
            EventCursor: HostInstanceId + ":" + OwnerClientId + ":" + long.MaxValue.ToString(CultureInfo.InvariantCulture), RubyResult: feedback);
        if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upper)).Length > _branchReceiptMaxBytes) throw new ReadModelBudgetExceededException();
    }
    private static ProjectChangeSetDto IncludeRubyHistoryChanges(Project before, Project after, ProjectChangeSetDto fallback)
    {
        if (before == after || after with { Lyrics = before.Lyrics } != before
            || after.Lyrics with { Branches = before.Lyrics.Branches } != before.Lyrics
            || before.Lyrics.Branches.Length != after.Lyrics.Branches.Length) return fallback;
        var targets = new List<RubyEditTarget>();
        bool timing = true;
        for (int i = 0; i < before.Lyrics.Branches.Length; i++)
        {
            var oldBranch = before.Lyrics.Branches[i]; var newBranch = after.Lyrics.Branches[i];
            if (newBranch with { Lines = oldBranch.Lines } != oldBranch || oldBranch.Lines.Length != newBranch.Lines.Length) return fallback;
            for (int j = 0; j < oldBranch.Lines.Length; j++)
            {
                var oldLine = oldBranch.Lines[j]; var newLine = newBranch.Lines[j];
                if (newLine with { RubyAnnotations = oldLine.RubyAnnotations } != oldLine) return fallback;
                if (oldLine.RubyAnnotations.SequenceEqual(newLine.RubyAnnotations)) continue;
                foreach (var ruby in oldLine.RubyAnnotations.Concat(newLine.RubyAnnotations).DistinctBy(ruby => ruby.Id))
                    targets.Add(new(oldBranch.Id, oldLine.Id, ruby.BaseTokenId, ruby.Id));
                if (oldLine.RubyAnnotations.Length != newLine.RubyAnnotations.Length) { timing = false; continue; }
                for (int k = 0; k < oldLine.RubyAnnotations.Length; k++)
                {
                    var oldRuby = oldLine.RubyAnnotations[k]; var newRuby = newLine.RubyAnnotations[k];
                    if (newRuby with { Text = oldRuby.Text, Segments = oldRuby.Segments } != oldRuby || oldRuby.Text != newRuby.Text
                        || oldRuby.Segments.Length != newRuby.Segments.Length) { timing = false; continue; }
                    for (int n = 0; n < oldRuby.Segments.Length; n++)
                        if (newRuby.Segments[n] with { Time = oldRuby.Segments[n].Time, TimingStatus = oldRuby.Segments[n].TimingStatus } != oldRuby.Segments[n]) timing = false;
                }
            }
        }
        return targets.Count > 0 ? RubyChanges(true, targets, timing) : fallback;
    }
    private static RubySegmentCommandResultDto? RubyHistoryFeedback(Project before, Project after, ProjectChangeSetDto changes)
    {
        if (!changes.Areas.Any(area => area is "lyricsRubyTiming" or "lyricsRubyContent")) return null;
        var groups = new List<(RubyGroupTargetDto Target, RubyAnnotation? Old, RubyAnnotation? New)>();
        foreach (var branch in before.Lyrics.Branches)
        foreach (var line in branch.Lines)
        {
            var nextLine = after.Lyrics.Branches.FirstOrDefault(item => item.Id == branch.Id)?.Lines.FirstOrDefault(item => item.Id == line.Id);
            if (nextLine is null) continue;
            foreach (var id in line.RubyAnnotations.Select(ruby => ruby.Id).Concat(nextLine.RubyAnnotations.Select(ruby => ruby.Id)).Distinct(StringComparer.Ordinal))
            {
                var oldRuby = line.RubyAnnotations.FirstOrDefault(ruby => ruby.Id == id);
                var newRuby = nextLine.RubyAnnotations.FirstOrDefault(ruby => ruby.Id == id);
                if (oldRuby != newRuby) groups.Add((new(branch.Id, line.Id, (newRuby ?? oldRuby)!.BaseTokenId, id), oldRuby, newRuby));
            }
        }
        int count = 0;
        foreach (var group in groups)
        {
            var oldSegments = group.Old?.Segments ?? [];
            var newSegments = group.New?.Segments ?? [];
            var oldMap = new Dictionary<string, (RubyTextSegment Segment, int Index)>(StringComparer.Ordinal);
            var newMap = new Dictionary<string, (RubyTextSegment Segment, int Index)>(StringComparer.Ordinal);
            for (int i = 0; i < oldSegments.Length; i++) oldMap.TryAdd(oldSegments[i].Id, (oldSegments[i], i));
            for (int i = 0; i < newSegments.Length; i++) newMap.TryAdd(newSegments[i].Id, (newSegments[i], i));
            foreach (var id in oldMap.Keys.Concat(newMap.Keys).Distinct(StringComparer.Ordinal))
                if (!oldMap.TryGetValue(id, out var oldValue) || !newMap.TryGetValue(id, out var newValue) || oldValue != newValue) count++;
        }
        if (groups.Count != 1) return new(count);
        var single = groups[0];
        var oldIds = single.Old?.Segments.Select(segment => segment.Id).ToArray() ?? [];
        var newIds = single.New?.Segments.Select(segment => segment.Id).ToArray() ?? [];
        var oldSet = oldIds.ToHashSet(StringComparer.Ordinal);
        var newSet = newIds.ToHashSet(StringComparer.Ordinal);
        var restored = newIds.FirstOrDefault(id => !oldSet.Contains(id));
        int removedIndex = Array.FindIndex(oldIds, id => !newSet.Contains(id));
        bool selectionChanged = restored is not null || removedIndex >= 0;
        string? selected = restored ?? (removedIndex >= 0 && newIds.Length > 0 ? newIds[Math.Min(removedIndex, newIds.Length - 1)] : null);
        return new(count, single.Target, selected, SelectionChanged: selectionChanged);
    }

    private static string RubyErrorField(ProjectCommandDto command, RubySegmentEditErrorCode? code) => command switch
    {
        SetRubySegmentTimesCommandDto => "command.changes",
        AddRubySegmentCommandDto when code == RubySegmentEditErrorCode.TargetStale => "command.afterSegmentId",
        _ when code is RubySegmentEditErrorCode.TargetStale or RubySegmentEditErrorCode.WorkspaceStale => "command.target",
        SetRubySegmentTimeCommandDto when code == RubySegmentEditErrorCode.InvalidStart => "command.startUs",
        SetRubySegmentTimeCommandDto => "command.endUs",
        SetRubySegmentTextCommandDto or AddRubySegmentCommandDto => "command.text",
        ReorderRubySegmentsCommandDto => "command.orderedSegmentIds",
        _ => "command.target",
    };
    private static BackendErrorResponse RubyRejected(string code, string field, string? detail = null) => new(BackendErrorCode.CommandRejected,
        detail ?? "注音片段编辑被领域规则拒绝。", field, FailureCode: code);
    private static BackendErrorResponse RubyBudget() => new(BackendErrorCode.ResourceTooLarge,
        "注音编辑超过64KiB意图、1MiB/20000项完整行、16MiB累计行或完整回执预算；工程未改变。", "command");
    private sealed class RubyIntentStream : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        { if (buffer.Length > RubySegmentEditingLimits.MaxIntentBytes - _length) throw new ReadModelBudgetExceededException(); _length += buffer.Length; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
