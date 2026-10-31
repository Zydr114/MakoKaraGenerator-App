using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    private static readonly JsonSerializerOptions ReadModelJson = CreateReadModelJson();

    internal Task<BackendOperationResult<ProjectLyricBranchesResponse>> ReadLyricBranchesAsync(
        string? expectedRevision, int offset, int limit, CancellationToken cancellationToken) =>
        ReadModelAsync(expectedRevision, () =>
        {
            LyricsDocument document = Workspace.Project.Lyrics;
            if (PageError(offset, limit, document.Branches.Length) is { } error)
                return BackendOperationResult<ProjectLyricBranchesResponse>.Rejected(error);
            ReadModelBudget budget = new();
            budget.Text(ProjectSessionId, document.PrimaryBranchId);
            ProjectLyricBranchDto[] branches = [.. document.Branches.Skip(offset).Take(limit).Select(branch =>
            {
                budget.Item(); budget.Text(branch.Id, branch.Name, branch.Language);
                return new ProjectLyricBranchDto(branch.Id, branch.Name, (ProjectLyricBranchRole)branch.Role,
                    branch.Language, (ProjectLyricTimingMode)branch.TimingMode, branch.Lines.Length);
            })];
            return BoundedReadModel(new ProjectLyricBranchesResponse(ProjectSessionId, RevisionText,
                document.PrimaryBranchId, offset, document.Branches.Length, NextOffset(offset, branches.Length, document.Branches.Length), branches));
        }, cancellationToken);

    internal Task<BackendOperationResult<ProjectLyricsWindowResponse>> ReadLyricsWindowAsync(
        string? branchId, string? expectedRevision, int offset, int limit, CancellationToken cancellationToken) =>
        ReadModelAsync(expectedRevision, () =>
        {
            if (string.IsNullOrWhiteSpace(branchId))
                return BackendOperationResult<ProjectLyricsWindowResponse>.Rejected(new(BackendErrorCode.InvalidRequest,
                    "必须提供有效的 branchId。", "branchId"));
            LyricBranch? branch = Workspace.Project.Lyrics.Branches.FirstOrDefault(item => item.Id == branchId);
            if (branch is null)
                return BackendOperationResult<ProjectLyricsWindowResponse>.Rejected(new(BackendErrorCode.CommandRejected,
                    "歌词来源不存在。", "branchId"));
            if (PageError(offset, limit, branch.Lines.Length) is { } error)
                return BackendOperationResult<ProjectLyricsWindowResponse>.Rejected(error);
            ReadModelBudget budget = new(); budget.Text(ProjectSessionId, branch.Id);
            ProjectLyricLineDto[] lines = [.. branch.Lines.Skip(offset).Take(limit).Select(line => ProjectEditableLine(branch, line, budget))];
            return BoundedReadModel(new ProjectLyricsWindowResponse(ProjectSessionId, RevisionText, branch.Id,
                offset, branch.Lines.Length, NextOffset(offset, lines.Length, branch.Lines.Length), lines));
        }, cancellationToken);

    internal Task<BackendOperationResult<ProjectSettingsResponse>> ReadSettingsAsync(
        string? expectedRevision, CancellationToken cancellationToken) =>
        ReadModelAsync(expectedRevision, () =>
        {
            Project project = Workspace.Project;
            ReadModelBudget budget = new(); budget.Text(ProjectSessionId, project.Name);
            Composition composition = project.Composition;
            RgbaColor color = composition.BackgroundColor;
            return BoundedReadModel(new ProjectSettingsResponse(ProjectSessionId, RevisionText, project.Name,
                composition.CanvasWidth, composition.CanvasHeight, composition.FrameRate.Numerator, composition.FrameRate.Denominator,
                color.Red, color.Green, color.Blue, color.Alpha, composition.OutputDurationUs?.ToString(CultureInfo.InvariantCulture),
                AudioDurationUs.ToString(CultureInfo.InvariantCulture), Background: BackgroundSettings(project.Background)));
        }, cancellationToken);

    internal Task<BackendOperationResult<ProjectPresetApplicationsResponse>> ReadPresetApplicationsAsync(
        string? expectedRevision, CancellationToken cancellationToken) =>
        ReadModelAsync(expectedRevision, () =>
        {
            Project project = Workspace.Project;
            ReadModelBudget budget = new();
            budget.Text(ProjectSessionId, RevisionText);
            ProjectPresetApplicationDto[] applications = [.. project.PresetApplications.Items.Select(item =>
                ProjectPresetApplication(item, project, budget))];
            return BoundedReadModel(new ProjectPresetApplicationsResponse(ProjectSessionId, RevisionText, applications));
        }, cancellationToken, "presetApplications");

    private static ProjectPresetApplicationDto ProjectPresetApplication(PresetApplication item, Project project, ReadModelBudget budget)
    {
        budget.Item(); budget.Text(item.Id, item.Snapshot.Id, item.Snapshot.Name);
        ProjectPresetApplicationTargetDto target = ProjectPresetTarget(item.Target, project, out bool targetValid);
        budget.Text(target.Kind, target.BranchId, target.LineId, target.TokenId, target.NodeId);
        if (target.ClipIds is { } clips) { budget.Item(clips.Length); budget.Text(clips); }
        budget.Item(item.BranchInputs.Count); budget.Text(item.BranchInputs.Keys.ToArray()); budget.Text(item.BranchInputs.Values.ToArray());
        TemplateProperty[] properties = [.. item.Snapshot.Properties.Select(property => (TemplateProperty)property)];
        budget.Item(properties.Length); budget.Text(properties.Select(property => property.ToString()).ToArray());
        var snapshot = new ProjectPresetApplicationSnapshotDto(item.Snapshot.Id, item.Snapshot.Name,
            (TemplateKind)item.Snapshot.Kind, properties, !item.Snapshot.EffectRecipes.IsEmpty,
            !MakoKaraGenerator.Core.Projects.SourceMaterialCatalog.Collect(item.Snapshot.Style).IsEmpty,
            !item.Snapshot.Packages.IsEmpty);
        return new(item.Id, target, targetValid, item.Enabled, snapshot,
            new ProjectPresetApplicationSourceDto(null, null, false, null),
            item.BranchInputs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    private static ProjectPresetApplicationTargetDto ProjectPresetTarget(ProjectEffectBinding binding, Project project, out bool valid)
    {
        valid = binding.Kind switch
        {
            ProjectEffectBindingKind.Project => true,
            ProjectEffectBindingKind.Branch => binding.Id is not null && project.Lyrics.Branches.Any(branch => branch.Id == binding.Id),
            ProjectEffectBindingKind.Lines => binding.Ids.Length == 1 && FindLine(project, binding.Ids[0]) is not null,
            ProjectEffectBindingKind.Tokens => binding.Ids.Length == 1 && FindToken(project, binding.Ids[0]) is not null,
            ProjectEffectBindingKind.Node => binding.Id is not null && project.LyricPresentation.FindNode(binding.Id) is not null,
            ProjectEffectBindingKind.Clips => binding.Ids.Length == 1 && project.LyricPresentation.Programs.Any(program => program.Clips.Any(clip => clip.Id == binding.Ids[0])),
            _ => false,
        };
        return binding.Kind switch
        {
            ProjectEffectBindingKind.Project => new("project"),
            ProjectEffectBindingKind.Branch => new("branch", BranchId: binding.Id),
            ProjectEffectBindingKind.Lines => FindLine(project, binding.Ids[0]) is { } line
                ? new("line", BranchId: line.BranchId, LineId: line.Line.Id)
                : new("line", LineId: binding.Ids.FirstOrDefault()),
            ProjectEffectBindingKind.Tokens => FindToken(project, binding.Ids[0]) is { } token
                ? new("token", BranchId: token.BranchId, LineId: token.Line.Id, TokenId: token.Token.Id)
                : new("token", TokenId: binding.Ids.FirstOrDefault()),
            ProjectEffectBindingKind.Node => new("node", NodeId: binding.Id, IncludeDescendants: binding.IncludeDescendants),
            ProjectEffectBindingKind.Clips => new("clips", BranchId: project.LyricPresentation.Programs
                .FirstOrDefault(program => program.Clips.Any(clip => binding.Ids.Contains(clip.Id)))?.BranchId,
                ClipIds: [.. binding.Ids]),
            _ => new("unknown"),
        };
    }

    private static (string BranchId, LyricLine Line)? FindLine(Project project, string id)
    {
        foreach (LyricBranch branch in project.Lyrics.Branches)
            if (branch.Lines.FirstOrDefault(line => line.Id == id) is { } line) return (branch.Id, line);
        return null;
    }

    private static (string BranchId, LyricLine Line, LyricToken Token)? FindToken(Project project, string id)
    {
        foreach (LyricBranch branch in project.Lyrics.Branches)
            foreach (LyricLine line in branch.Lines)
                if (line.Tokens.FirstOrDefault(token => token.Id == id) is { } token) return (branch.Id, line, token);
        return null;
    }

    private static ProjectLyricLineDto ProjectEditableLine(LyricBranch branch, LyricLine line, ReadModelBudget? budget)
    {
        budget?.Item(); budget?.Text(branch.Id, line.Id, line.Text, line.PrimaryLineId);
        // The same projection and preallocation gates serve GET and content-command candidate admission.
        budget?.Item(line.Tokens.Length); budget?.Item(line.RubyAnnotations.Length);
        budget?.Item(line.RubyAnnotations.Sum(annotation => annotation.EffectiveSegments.Count));
        ProjectLyricTokenDto[] tokens = [.. line.Tokens.Select(token =>
        {
            budget?.Text(token.Id, token.Text);
            return new ProjectLyricTokenDto(token.Id, token.Text, ToTime(token.Time), (ProjectLyricTimingStatus)token.TimingStatus);
        })];
        ProjectRubyAnnotationDto[] ruby = [.. line.RubyAnnotations.Select(annotation =>
        {
            budget?.Text(annotation.Id, annotation.BaseTokenId, annotation.Text, annotation.Language);
            ProjectRubyTextSegmentDto[] segments = [.. annotation.EffectiveSegments.Select(segment =>
            {
                budget?.Text(segment.Id, segment.Text);
                return new ProjectRubyTextSegmentDto(segment.Id, segment.Text, ToTime(segment.Time),
                    (ProjectLyricTimingStatus)segment.TimingStatus);
            })];
            return new ProjectRubyAnnotationDto(annotation.Id, annotation.BaseTokenId, annotation.Text,
                annotation.Language, annotation.RelativeScale, annotation.GapPx, (ProjectRubyAlignment)annotation.Alignment, segments);
        })];
        return new(branch.Id, line.Id, line.Text, ToTime(line.Time), (ProjectLyricTimingStatus)line.TimingStatus,
            line.PrimaryLineId, tokens, ruby);
    }

    internal static ProjectLyricLineDto[] ProjectImportPreview(LyricBranch branch) =>
        [.. branch.Lines.Select(line => ProjectEditableLine(branch, line, null))];

    private void ValidateContentLineBudget(Project project, string branchId, string lineId)
    {
        LyricBranch branch = project.Lyrics.Branches.First(value => value.Id == branchId);
        int index = 0;
        while (index < branch.Lines.Length && branch.Lines[index].Id != lineId) index++;
        LyricLine line = branch.Lines[index];
        ReadModelBudget budget = new(); budget.Text(ProjectSessionId, branch.Id);
        ProjectLyricLineDto projected = ProjectEditableLine(branch, line, budget);
        _ = BoundedReadModel(new ProjectLyricsWindowResponse(ProjectSessionId,
            checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture), branch.Id, index, branch.Lines.Length,
            NextOffset(index, 1, branch.Lines.Length), [projected]));
    }

    private static void ValidateContentTextBudget(string text)
    {
        using ReadModelCountingStream counter = new();
        JsonSerializer.Serialize(counter, text, ReadModelJson);
    }

    private string RevisionText => ProjectRevision.ToString(CultureInfo.InvariantCulture);

    private async Task<BackendOperationResult<T>> ReadModelAsync<T>(string? expectedRevision,
        Func<BackendOperationResult<T>> project, CancellationToken cancellationToken, string budgetField = "window")
    {
        await Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsClosed) return BackendOperationResult<T>.Rejected(ProjectSessionRegistry.MissingSessionError(ProjectSessionId));
            if (!TryParseRevision(expectedRevision, out long revision))
                return BackendOperationResult<T>.Rejected(new(BackendErrorCode.InvalidRequest,
                    "expectedRevision 必须是非负十进制 Int64 字符串。", "expectedRevision"));
            if (revision != ProjectRevision)
                return BackendOperationResult<T>.Rejected(new(BackendErrorCode.RevisionConflict,
                    "工程修订已经改变，请重新读取当前状态。", "expectedRevision"));
            cancellationToken.ThrowIfCancellationRequested();
            try { return project(); }
            catch (ReadModelBudgetExceededException)
            {
                return BackendOperationResult<T>.Rejected(new(BackendErrorCode.ResourceTooLarge,
                    budgetField == "style" ? "完整样式读模型超过 1MiB JSON 或 20000 项预算；不会截断。"
                        : "读模型超过 1MiB JSON 或 20000 项预算；请缩小窗口，单行不会被截断。", budgetField));
            }
        }
        finally { Writer.Release(); }
    }

    private static BackendErrorResponse? PageError(int offset, int limit, int count) =>
        offset < 0 || offset > count || limit is < 1 or > ProjectReadModelLimits.MaxPageSize
            ? new(BackendErrorCode.InvalidRequest, "offset 必须在 0..totalCount，limit 必须在 1..100。", "window") : null;

    private static int? NextOffset(int offset, int length, int total) => offset + length < total ? offset + length : null;

    private static BackendOperationResult<T> BoundedReadModel<T>(T response)
    {
        using ReadModelCountingStream counter = new();
        JsonSerializer.Serialize(counter, response, ReadModelJson);
        return BackendOperationResult<T>.Success(response);
    }

    private static JsonSerializerOptions CreateReadModelJson()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed class ReadModelBudget
    {
        private int _items;
        private long _characters;
        internal void Item(int count = 1)
        {
            if (count > ProjectReadModelLimits.MaxItems - _items) throw new ReadModelBudgetExceededException();
            _items += count;
        }
        internal void Text(params string?[] values)
        {
            foreach (string? value in values)
            {
                _characters += value?.Length ?? 0;
                // Every UTF-16 code unit needs at least one JSON byte; reject before serializer buffers can grow.
                if (_characters > ProjectReadModelLimits.MaxResponseBytes) throw new ReadModelBudgetExceededException();
            }
        }
    }

    private sealed class ReadModelBudgetExceededException : Exception;

    private sealed class ReadModelCountingStream : Stream
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
            if (buffer.Length > ProjectReadModelLimits.MaxResponseBytes - _length) throw new ReadModelBudgetExceededException();
            _length += buffer.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
