using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>规范静态样式的原子准备与唯一历史提交，不处理预设、动画或实际帧上下文。</summary>
/// <remarks>由工作区所有者同步调用；预算观察器可抛出宿主异常，整个准备不修改工作区。</remarks>
public static partial class ProjectStyleEditUseCase
{
    /// <summary>准备同层稳定目标的规范成员更新；只有真实变化目标才触发预算观察器。</summary>
    /// <param name="workspace">拥有唯一历史的实际工作区。</param>
    /// <param name="targets">同层、无重复的完整稳定目标。</param>
    /// <param name="changes">无重复或父子冲突的有类型成员值。</param>
    /// <param name="onChangedTarget">新目标完整投影进入累计候选之前的纯预算检查。</param>
    /// <returns>绑定源工程与工作区的固定候选。</returns>
    public static PreparedProjectStyleEdit PreparePatch(ProjectWorkspace workspace, IReadOnlyList<StyleEditTarget> targets,
        IReadOnlyList<StyleEditChange> changes, Action<Project, StyleEditTarget>? onChangedTarget = null)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(changes);
        Project source = workspace.Project; long generation = workspace.Generation;
        if (targets.Count is < 1 or > 1000)
            return new(workspace, generation, source, Reject(source, new(ProjectStyleEditErrorCode.InvalidTarget)), "styles.patch");
        if (changes.Count is < 1 or > 29)
            return new(workspace, generation, source, Reject(source, new(ProjectStyleEditErrorCode.InvalidProperty)), "styles.patch");
        StyleEditTarget[] fixedTargets = [.. targets]; StyleEditChange[] fixedChanges = [.. changes];
        var properties = fixedChanges.Select(change => change?.Property ?? (StyleEditProperty)(-1)).ToArray();
        ProjectStyleEditResult result = Prepare(source, fixedTargets, properties, fixedChanges, onChangedTarget);
        return new(workspace, generation, source, result, "styles.patch");
    }

    /// <summary>只移除本层选定成员；不从解析后的样式制造整份覆盖。</summary>
    /// <param name="workspace">拥有唯一历史的实际工作区。</param>
    /// <param name="targets">同层、无重复的完整稳定目标。</param>
    /// <param name="properties">需恢复继承的规范成员。</param>
    /// <param name="onChangedTarget">新目标完整投影进入累计候选之前的纯预算检查。</param>
    /// <returns>绑定源工程与工作区的固定候选。</returns>
    public static PreparedProjectStyleEdit PrepareRestore(ProjectWorkspace workspace, IReadOnlyList<StyleEditTarget> targets,
        IReadOnlyList<StyleEditProperty> properties, Action<Project, StyleEditTarget>? onChangedTarget = null)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(properties);
        Project source = workspace.Project; long generation = workspace.Generation;
        if (targets.Count is < 1 or > 1000)
            return new(workspace, generation, source, Reject(source, new(ProjectStyleEditErrorCode.InvalidTarget)), "styles.restore");
        if (properties.Count is < 1 or > 29)
            return new(workspace, generation, source, Reject(source, new(ProjectStyleEditErrorCode.InvalidProperty)), "styles.restore");
        ProjectStyleEditResult result = Prepare(source, [.. targets], [.. properties], null, onChangedTarget);
        return new(workspace, generation, source, result, "styles.restore");
    }

    /// <summary>提交已通过宿主预算的固定候选；失败或无变化不触碰历史与重做。</summary>
    /// <param name="workspace">实际工作区。</param>
    /// <param name="prepared">纯准备返回的完整候选。</param>
    /// <returns>实际提交结果。</returns>
    public static ProjectStyleEditResult CommitPrepared(ProjectWorkspace workspace, PreparedProjectStyleEdit prepared)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(workspace, prepared.Workspace) || workspace.Generation != prepared.Generation)
            return Reject(workspace.Project, new(ProjectStyleEditErrorCode.WorkspaceStale));
        if (!ReferenceEquals(workspace.Project, prepared.Source))
            return Reject(workspace.Project, new(ProjectStyleEditErrorCode.TargetStale));
        if (!prepared.Result.Accepted || !prepared.Result.Changed) return prepared.Result;
        workspace.History.BreakMergeGroup();
        workspace.Execute(ProjectCommand.Create(prepared.CommandName, _ => prepared.Result.Project));
        workspace.History.BreakMergeGroup();
        return prepared.Result;
    }

    private static ProjectStyleEditResult Prepare(Project source, StyleEditTarget[] targets, StyleEditProperty[] properties,
        StyleEditChange[]? changes, Action<Project, StyleEditTarget>? observer)
    {
        if (ValidateTargets(source, targets) is { } targetError) return Reject(source, targetError);
        if (ValidateProperties(targets[0].Kind, properties, changes is null) is { } propertyError)
            return Reject(source, propertyError);
        if (changes is not null)
            foreach (var change in changes)
                if (ValidateChange(change) is { } valueError) return Reject(source, valueError);
        Project candidate = source; int count = 0;
        foreach (StyleEditTarget target in targets)
        {
            Project next;
            try
            {
                next = UpdateTarget(candidate, target, properties, changes);
                if (ReferenceEquals(next, candidate)) continue;
                ValidateTargetStyle(next, target);
            }
            catch (ArgumentException error)
            { return Reject(source, new(ProjectStyleEditErrorCode.InvalidValue, Array.IndexOf(targets, target), Detail: error.Message)); }
            // The observer runs outside the domain exception mapping. It must be able to reject
            // a complete target with its original budget error, before any history or adoption.
            observer?.Invoke(next, target);
            candidate = next; count++;
        }
        if (count == 0) return new(true, source, 0);
        // Style edits never delete lyrics; presentation cleanup belongs to explicit structural edits.
        var report = PresentationValidator.Validate(candidate);
        if (report.HasErrors)
            return Reject(source, new(ProjectStyleEditErrorCode.DomainValidationFailed,
                Detail: report.Issues.First(issue => issue.Severity == IssueSeverity.Error).Message));
        return new(true, candidate, count);
    }

    private static ProjectStyleEditError? ValidateTargets(Project source, StyleEditTarget[] targets)
    {
        if (targets.Length is < 1 or > 1000) return new(ProjectStyleEditErrorCode.InvalidTarget);
        HashSet<StyleEditTarget> seen = [];
        for (int index = 0; index < targets.Length; index++)
        {
            StyleEditTarget? target = targets[index];
            if (target is null || !Enum.IsDefined(target.Kind) || target.Kind != targets[0].Kind || !seen.Add(target)
                || !HasTargetShape(target)) return new(ProjectStyleEditErrorCode.InvalidTarget, index);
            if (target.Kind == StyleEditTargetKind.Project)
            { if (targets.Length != 1) return new(ProjectStyleEditErrorCode.InvalidTarget, index); continue; }
            var branch = source.Lyrics.Branches.FirstOrDefault(branch => branch.Id == target.BranchId);
            if (branch is null) return new(ProjectStyleEditErrorCode.InvalidTarget, index);
            if (target.Kind == StyleEditTargetKind.Branch) continue;
            var line = branch.Lines.FirstOrDefault(line => line.Id == target.LineId);
            if (line is null || target.Kind == StyleEditTargetKind.Token && !line.Tokens.Any(token => token.Id == target.TokenId))
                return new(ProjectStyleEditErrorCode.InvalidTarget, index);
        }
        return null;
    }

    private static bool HasTargetShape(StyleEditTarget target) => target.Kind switch
    {
        StyleEditTargetKind.Project => target.BranchId is null && target.LineId is null && target.TokenId is null,
        StyleEditTargetKind.Branch => !string.IsNullOrWhiteSpace(target.BranchId) && target.LineId is null && target.TokenId is null,
        StyleEditTargetKind.Line => !string.IsNullOrWhiteSpace(target.BranchId) && !string.IsNullOrWhiteSpace(target.LineId) && target.TokenId is null,
        StyleEditTargetKind.Token => !string.IsNullOrWhiteSpace(target.BranchId) && !string.IsNullOrWhiteSpace(target.LineId) && !string.IsNullOrWhiteSpace(target.TokenId),
        _ => false,
    };

    private static ProjectStyleEditError? ValidateProperties(StyleEditTargetKind kind, StyleEditProperty[] properties, bool restore)
    {
        if (properties.Length == 0) return new(ProjectStyleEditErrorCode.InvalidProperty);
        if (restore && kind == StyleEditTargetKind.Project) return new(ProjectStyleEditErrorCode.UnsupportedProperty);
        HashSet<StyleEditProperty> seen = [];
        foreach (var property in properties)
        {
            if (!Enum.IsDefined(property) || !seen.Add(property)) return new(ProjectStyleEditErrorCode.InvalidProperty, Property: property);
            if (kind == StyleEditTargetKind.Token && !TokenStyleSupport.IsSupported(ToCoreProperty(property)))
                return new(ProjectStyleEditErrorCode.UnsupportedProperty, Property: property);
        }
        if (seen.Contains(StyleEditProperty.Stroke) && properties.Any(IsStrokeLeaf)
            || seen.Contains(StyleEditProperty.Shadow) && properties.Any(IsShadowLeaf)
            || seen.Contains(StyleEditProperty.Glow) && properties.Any(IsGlowLeaf))
            return new(ProjectStyleEditErrorCode.InvalidProperty);
        return null;
    }

    private static bool IsStrokeLeaf(StyleEditProperty property) => property is >= StyleEditProperty.StrokeMaterial and <= StyleEditProperty.StrokePosition;
    private static bool IsShadowLeaf(StyleEditProperty property) => property is >= StyleEditProperty.ShadowColor and <= StyleEditProperty.ShadowBlurRadiusPx;
    private static bool IsGlowLeaf(StyleEditProperty property) => property is >= StyleEditProperty.GlowColor and <= StyleEditProperty.GlowSpreadPx;

    private static Project UpdateTarget(Project project, StyleEditTarget target, StyleEditProperty[] properties, StyleEditChange[]? changes)
    {
        if (target.Kind == StyleEditTargetKind.Project)
        {
            LyricStyleOverride patch = ApplyChanges(LyricStyleOverride.Empty, properties, changes, project.LyricStyle);
            LyricStyle updated = LyricStyleResolver.Resolve(project.LyricStyle, lineOverride: patch).Style;
            return updated == project.LyricStyle ? project : LyricStyleEditor.SetBaseline(project, updated);
        }
        int branchIndex = IndexOf(project.Lyrics.Branches, branch => branch.Id == target.BranchId);
        var branch = project.Lyrics.Branches[branchIndex];
        if (target.Kind == StyleEditTargetKind.Branch)
        {
            var overlay = UpdateOverlay(branch.StyleOverride, properties, changes, project.LyricStyle);
            if (overlay == branch.StyleOverride) return project;
            branch = branch with { StyleOverride = overlay };
        }
        else
        {
            int lineIndex = IndexOf(branch.Lines, line => line.Id == target.LineId);
            var line = branch.Lines[lineIndex];
            if (target.Kind == StyleEditTargetKind.Line)
            {
                var overlay = UpdateOverlay(line.StyleOverride, properties, changes,
                    LyricStyleResolver.Resolve(project.LyricStyle, branchOverride: branch.StyleOverride).Style);
                if (overlay == line.StyleOverride) return project;
                line = line with { StyleOverride = overlay };
            }
            else
            {
                int tokenIndex = IndexOf(line.Tokens, token => token.Id == target.TokenId);
                var token = line.Tokens[tokenIndex];
                var overlay = UpdateOverlay(token.StyleOverride, properties, changes, LyricStyleResolver.Resolve(project.LyricStyle,
                    branchOverride: branch.StyleOverride, lineOverride: line.StyleOverride).Style);
                if (overlay == token.StyleOverride) return project;
                line = line with { Tokens = line.Tokens.SetItem(tokenIndex, token with { StyleOverride = overlay }) };
            }
            branch = branch with { Lines = branch.Lines.SetItem(lineIndex, line) };
        }
        return project with { Lyrics = project.Lyrics with { Branches = project.Lyrics.Branches.SetItem(branchIndex, branch) } };
    }

    private static void ValidateTargetStyle(Project project, StyleEditTarget target)
    {
        if (target.Kind == StyleEditTargetKind.Project) { if (!project.LyricStyle.IsValid) throw new ArgumentException("工程基线无效。"); return; }
        var branch = project.Lyrics.Branches.Single(branch => branch.Id == target.BranchId);
        var line = target.LineId is null ? null : branch.Lines.Single(line => line.Id == target.LineId);
        var token = target.TokenId is null ? null : line!.Tokens.Single(token => token.Id == target.TokenId);
        _ = LyricStyleResolver.Resolve(project.LyricStyle, branchOverride: branch.StyleOverride,
            lineOverride: line?.StyleOverride, tokenOverride: token?.StyleOverride);
    }

    private static ProjectStyleEditResult Reject(Project source, ProjectStyleEditError error) => new(false, source, 0, error);
}
