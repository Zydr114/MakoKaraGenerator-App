using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    private bool TryExecuteStyleCommand(Project before, ProjectCommandDto command, out Project after,
        out ProjectChangeSetDto? changeSet, out StyleCommandResultDto? styleResult, out BackendErrorResponse? error)
    {
        after = before; changeSet = null; styleResult = null; error = null;
        if (command.Type is not ("styles.patch" or "styles.restore")) return false;
        if (command.Target is not null || command.StartUs is not null || command.EndUs is not null || command.AnalysisJobId is not null)
        { error = InvalidStyle("command"); return true; }
        try
        {
            // Revalidate in-process admission too, without relying on a prior HTTP converter pass.
            byte[] intent = JsonSerializer.SerializeToUtf8Bytes(command, ReadModelJson);
            if (intent.Length > StyleEditingLimits.MaxIntentBytes) throw new StyleCommandBudgetExceededException();
            _ = JsonSerializer.Deserialize<ProjectCommandDto>(intent, ReadModelJson);
            StyleTargetDto[] targets = command switch
            {
                PatchStylesCommandDto patch => patch.Targets,
                RestoreStylesCommandDto restore => restore.Targets,
                _ => throw new JsonException("样式 type 必须绑定派生 DTO。"),
            };
            var stable = new List<StyleEditTarget>(targets.Length);
            foreach (StyleTargetDto target in targets)
            {
                if (!TryStyleTarget(target, out StyleEditTarget? item)) { error = InvalidStyle("command.targets"); return true; }
                stable.Add(item!);
            }
            string nextRevision = checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture);
            long total = 0;
            var changedTargets = new List<StyleEditTarget>();
            void Observe(Project candidate, StyleEditTarget target)
            {
                ProjectStyleResponse response = ProjectStyle(candidate, target, nextRevision);
                using ReadModelCountingStream counter = new(); JsonSerializer.Serialize(counter, response, ReadModelJson);
                if (counter.Length > StyleEditingLimits.MaxPreparedBytes - total) throw new ReadModelBudgetExceededException();
                total += counter.Length; changedTargets.Add(target);
            }
            PreparedProjectStyleEdit prepared;
            if (command is PatchStylesCommandDto patchCommand)
            {
                long expanded = JsonSerializer.SerializeToUtf8Bytes(patchCommand.Changes, ReadModelJson).LongLength * targets.Length;
                if (expanded > StyleEditingLimits.MaxPreparedBytes) throw new StyleCommandBudgetExceededException();
                var changes = patchCommand.Changes.Select(CoreStyleChange).ToArray();
                prepared = ProjectStyleEditUseCase.PreparePatch(Workspace, stable, changes, Observe);
            }
            else
            {
                var restore = (RestoreStylesCommandDto)command;
                prepared = ProjectStyleEditUseCase.PrepareRestore(Workspace, stable, restore.Properties.Select(CoreStyleProperty).ToArray(), Observe);
            }
            ProjectStyleEditResult candidate = prepared.Result;
            if (!candidate.Accepted)
            {
                var failure = candidate.Error!;
                error = StyleRejected(failure.Code.ToString(), failure.TargetIndex.HasValue ? "command.targets"
                    : command is RestoreStylesCommandDto ? "command.properties" : "command.changes", failure.Detail);
                return true;
            }
            changeSet = StyleChanges(changedTargets, candidate.Changed);
            styleResult = new(candidate.ChangedTargetCount);
            ValidateStyleReceipt(prepared, changeSet, styleResult);
            // Closing can be requested without waiting for this writer; no mutation follows a late close flag.
            if (MutationBlocked) { error = new(BackendErrorCode.SessionClosing, "工程会话正在关闭。", "command"); return true; }
            if (IsReadOnly) { error = new(BackendErrorCode.ReadOnlySession, "只读工程不能修改。", "command"); return true; }
            ProjectStyleEditResult committed = ProjectStyleEditUseCase.CommitPrepared(Workspace, prepared);
            if (!committed.Accepted) { error = StyleRejected(committed.Error!.Code.ToString(), "command"); return true; }
            after = committed.Project;
        }
        catch (StyleCommandBudgetExceededException)
        { error = StyleBudgetError(); }
        catch (ReadModelBudgetExceededException)
        { error = StyleBudgetError(); }
        catch (JsonException)
        { error = InvalidStyle("command"); }
        return true;
    }

    private static BackendErrorResponse StyleBudgetError() => new(BackendErrorCode.ResourceTooLarge,
        "样式意图、新值展开、完整新读投影或完整回执超过预算；工程未改变。", "command");

    private static ProjectChangeSetDto StyleChanges(IReadOnlyList<StyleEditTarget> targets, bool changed)
    {
        if (!changed) return new([], [], [], [], false);
        ProjectChangeSetDto result = new(["styles", "renderInputs"],
            targets.Select(t => t.BranchId).Where(id => id is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray(),
            targets.Select(t => t.LineId).Where(id => id is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray(),
            targets.Select(t => t.TokenId).Where(id => id is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray(), true);
        return JsonSerializer.SerializeToUtf8Bytes(result).Length > ProjectCommandLimits.MaxChangeSetBytes
            ? result with { BranchIds = [], LineIds = [], TokenIds = [] } : result;
    }

    private void ValidateStyleReceipt(PreparedProjectStyleEdit prepared, ProjectChangeSetDto changes, StyleCommandResultDto feedback)
    {
        bool changed = prepared.Result.Changed;
        // false is the longest boolean spelling; the cursor reserves the complete actual identity plus Int64 digits.
        ProjectCommandResponse upper = new(ProjectSessionId, RevisionText,
            changed ? checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture) : RevisionText,
            changed, false, false, false, changed ? prepared.CommandName : Workspace.UndoName,
            changed ? null : Workspace.RedoName, changes,
            EventCursor: HostInstanceId + ":" + OwnerClientId + ":" + long.MaxValue.ToString(CultureInfo.InvariantCulture), StyleResult: feedback);
        if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upper)).Length > _branchReceiptMaxBytes)
            throw new ReadModelBudgetExceededException();
    }

    internal static BackendErrorResponse? CheckStyleIntentBudget(ProjectCommandDto command)
    {
        if (command is not (PatchStylesCommandDto or RestoreStylesCommandDto)) return null;
        try
        {
            // A small counting stream stops serialization before allocating a whole oversized intent.
            using var counter = new StyleIntentStream(); JsonSerializer.Serialize(counter, command, ReadModelJson);
            return null;
        }
        catch (StyleCommandBudgetExceededException) { return StyleBudgetError(); }
        catch (JsonException) { return InvalidStyle("command"); }
        // Only this serializer admission boundary maps CLR values that cannot be represented in JSON.
        catch (ArgumentException) { return InvalidStyle("command"); }
    }
    private sealed class StyleIntentStream : Stream
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
        {
            if (buffer.Length > StyleEditingLimits.MaxIntentBytes - _length) throw new StyleCommandBudgetExceededException();
            _length += buffer.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static ProjectCommandDto CloneStyleCommand(ProjectCommandDto command)
    {
        StyleMaterialDto? CloneMaterial(StyleMaterialDto? material) => material is GradientStyleMaterialDto { Gradient: { } g } gradient
            ? gradient with { Gradient = g with { Stops = g.Stops?.ToArray()! } } : material;
        StyleStrokeDto[]? CloneStrokes(StyleStrokeDto[]? strokes) => strokes?.Select(s => s is null ? s! : s with { Material = CloneMaterial(s.Material)! }).ToArray();
        StyleChangeDto? CloneChange(StyleChangeDto? change) => change switch
        {
            StyleFallbackFamiliesChangeDto f => f with { Value = f.Value?.ToArray()! },
            StyleUnsungFillChangeDto m => m with { Value = CloneMaterial(m.Value)! },
            StyleSungFillChangeDto m => m with { Value = CloneMaterial(m.Value)! },
            StyleUnsungStrokesChangeDto s => s with { Value = CloneStrokes(s.Value)! },
            StyleSungStrokesChangeDto { Value: { } strokes } s => s with { Value = strokes with { Value = CloneStrokes(strokes.Value) } },
            _ => change,
        };
        return command switch
        {
            PatchStylesCommandDto patch => patch with { Targets = patch.Targets?.ToArray()!, Changes = patch.Changes?.Select(c => CloneChange(c)!).ToArray()! },
            RestoreStylesCommandDto restore => restore with { Targets = restore.Targets?.ToArray()!, Properties = restore.Properties?.ToArray()! },
            _ => command,
        };
    }
}
