using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    internal static BackendErrorResponse? CheckPresetApplicationIntent(ProjectCommandDto command)
    {
        if (command is not (ApplyPresetApplicationCommandDto or UpdatePresetApplicationCommandDto
            or SetPresetApplicationEnabledCommandDto or RemovePresetApplicationCommandDto) && !IsPresetApplicationCommand(command)) return null;
        try
        {
            if (command.Target is not null || command.StartUs is not null || command.EndUs is not null || command.AnalysisJobId is not null)
                throw new JsonException("模板分配不接受时间或分析字段。");
            using (PresetIntentStream counter = new()) JsonSerializer.Serialize(counter, command, ReadModelJson);
            _ = JsonSerializer.Deserialize<ProjectCommandDto>(JsonSerializer.SerializeToUtf8Bytes(command, ReadModelJson), ReadModelJson);
            return null;
        }
        catch (PresetIntentBudgetExceededException)
        { return new(BackendErrorCode.ResourceTooLarge, "模板分配意图超过64KiB预算。", "command"); }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { return new(BackendErrorCode.InvalidRequest, ex.Message, "command"); }
    }

    private static ProjectCommandDto ClonePresetApplicationCommand(ProjectCommandDto command) => command switch
    {
        ApplyPresetApplicationCommandDto apply => apply with { Targets = apply.Targets?.Select(target => target is null ? null! : target with { ClipIds = target.ClipIds?.ToArray() }).ToArray(),
            BranchInputs = apply.BranchInputs is null ? null : new(apply.BranchInputs, StringComparer.Ordinal) },
        UpdatePresetApplicationCommandDto update => update with {
            BranchInputs = update.BranchInputs is null ? null : new(update.BranchInputs, StringComparer.Ordinal) },
        RemovePresetApplicationCommandDto remove => remove with { ApplicationIds = remove.ApplicationIds?.ToArray() },
        _ => command,
    };

    private Project? ExecutePresetApplicationCommand(Project before, ProjectCommandDto command,
        Func<ProjectCommandDto, StyleTemplate?> resolver, out BackendErrorResponse? error)
    {
        error = CheckPresetApplicationIntent(command);
        if (error is not null) return null;
        try
        {
            PreparedPresetApplicationsEdit prepared;
            switch (command)
            {
                case ApplyPresetApplicationCommandDto apply when apply.Targets is { Length: > 0 and <= 1000 } targets:
                    StyleTemplate? snapshot = resolver(apply);
                    if (snapshot is null) { error = MissingPresetSnapshot(); return null; }
                    ProjectEffectBinding[] bindings = ToBindings(before, targets);
                    prepared = PresetApplicationsEditUseCase.PrepareApply(Workspace, snapshot, bindings, apply.BranchInputs);
                    break;
                case UpdatePresetApplicationCommandDto update when !string.IsNullOrWhiteSpace(update.ApplicationId):
                    StyleTemplate? updateSnapshot = resolver(update);
                    if (updateSnapshot is null) { error = MissingPresetSnapshot(); return null; }
                    prepared = PresetApplicationsEditUseCase.PrepareUpdate(Workspace, updateSnapshot, [update.ApplicationId], update.BranchInputs);
                    break;
                case SetPresetApplicationEnabledCommandDto enabled when enabled.ApplicationId is { Length: > 0 } && enabled.Enabled is { } value:
                    prepared = PresetApplicationsEditUseCase.PrepareSetEnabled(Workspace, [enabled.ApplicationId], value);
                    break;
                case RemovePresetApplicationCommandDto remove when remove.ApplicationIds is { Length: > 0 } ids:
                    prepared = PresetApplicationsEditUseCase.PrepareRemove(Workspace, ids);
                    break;
                default:
                    error = new(BackendErrorCode.InvalidRequest, "模板分配命令字段不完整。", "command"); return null;
            }
            if (!prepared.Accepted)
            { error = new(BackendErrorCode.CommandRejected, prepared.Error ?? "模板分配命令被拒绝。", "command"); return null; }
            Project candidate = prepared.Result.Project;
            ValidatePresetApplicationsProjection(candidate, prepared.Changed
                ? checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture) : RevisionText);
            ProjectChangeSetDto changes = prepared.Changed ? new(["presetApplications", "renderInputs"], [], [], [], false)
                : new([], [], [], [], false);
            ProjectCommandResponse upper = new(ProjectSessionId, RevisionText,
                prepared.Changed ? checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture) : RevisionText,
                prepared.Changed, false, false, false, prepared.Changed ? command.Type : Workspace.UndoName,
                prepared.Changed ? null : Workspace.RedoName, changes,
                EventCursor: HostInstanceId + ":" + OwnerClientId + ":" + long.MaxValue.ToString(CultureInfo.InvariantCulture),
                PresetApplicationResult: PresetApplicationResult(command, before, candidate));
            if (JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(upper)).Length > _branchReceiptMaxBytes)
                throw new ReadModelBudgetExceededException();
            if (MutationBlocked) { error = new(BackendErrorCode.SessionClosing, "工程会话正在关闭。", "command"); return null; }
            ProjectEditResult committed = PresetApplicationsEditUseCase.CommitPrepared(Workspace, prepared);
            if (!committed.Accepted)
            { error = new(BackendErrorCode.RevisionConflict, committed.Error ?? "模板分配提交时工程已变化。", "expectedRevision"); return null; }
            return committed.Project;
        }
        catch (ReadModelBudgetExceededException)
        { error = new(BackendErrorCode.ResourceTooLarge, "模板分配意图、完整新读投影或完整回执超过预算；工程未改变。", "command"); return null; }
        catch (JsonException ex)
        { error = new(BackendErrorCode.InvalidRequest, ex.Message, "command.targets"); return null; }
        catch (ArgumentException ex)
        { error = new(BackendErrorCode.CommandRejected, ex.Message, "command"); return null; }
    }

    private void ValidatePresetApplicationsProjection(Project candidate, string revision)
    {
        ReadModelBudget budget = new();
        budget.Text(ProjectSessionId, revision);
        ProjectPresetApplicationDto[] applications = [.. candidate.PresetApplications.Items.Select(item =>
            ProjectPresetApplication(item, candidate, budget))];
        _ = BoundedReadModel(new ProjectPresetApplicationsResponse(ProjectSessionId, revision, applications));
    }

    private static BackendErrorResponse MissingPresetSnapshot() =>
        new(BackendErrorCode.ResourceNotFound, "模板快照不存在或修订不匹配。", "command.templateRevision");

    // Core applications address one concrete object each; a multi-clip HTTP target is one atomic multi-select.
    private static ProjectEffectBinding[] ToBindings(Project project, IReadOnlyList<PresetApplicationTargetDto> targets)
    {
        List<ProjectEffectBinding> bindings = [];
        foreach (PresetApplicationTargetDto target in targets)
        {
            PresetApplicationTargetJsonConverter.Validate(target);
            int count = target.Kind == "clips" ? target.ClipIds!.Length : 1;
            if (count > 1000 - bindings.Count) throw new ArgumentException("应用目标数量必须在1至1000之间。", nameof(targets));
            if (target.Kind != "clips") { bindings.Add(ToBinding(target)); continue; }
            var program = project.LyricPresentation.FindProgram(target.BranchId!);
            if (program is null || target.ClipIds!.Any(id => !program.Clips.Any(clip => clip.Id == id)))
                throw new ArgumentException("展示片段不存在或不属于所指定的歌词分支。", nameof(targets));
            bindings.AddRange(target.ClipIds!.Select(id => new ProjectEffectBinding(ProjectEffectBindingKind.Clips, ids: [id])));
        }
        return [.. bindings];
    }

    private static ProjectEffectBinding ToBinding(PresetApplicationTargetDto target)
    {
        PresetApplicationTargetJsonConverter.Validate(target);
        return target.Kind switch
        {
            "project" => new(ProjectEffectBindingKind.Project),
            "branch" => new(ProjectEffectBindingKind.Branch, target.BranchId),
            "line" => new(ProjectEffectBindingKind.Lines, ids: [target.LineId!]),
            "token" => new(ProjectEffectBindingKind.Tokens, ids: [target.TokenId!]),
            "node" => new(ProjectEffectBindingKind.Node, target.NodeId, includeDescendants: target.IncludeDescendants!.Value),
            _ => throw new ArgumentException("不支持的预设目标。", nameof(target)),
        };
    }
    private sealed class PresetIntentBudgetExceededException : Exception;

    private sealed class PresetIntentStream : Stream
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
            if (buffer.Length > PresetApplicationLimits.MaxIntentBytes - _length) throw new PresetIntentBudgetExceededException();
            _length += buffer.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

}
