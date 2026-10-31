using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>绑定一次指针拖动的即时提交身份；跨时间窗合为一次撤销，不打开持久历史组。</summary>
/// <remarks>
/// 由 <see cref="LyricTimingEditUseCase.TryBeginGesture"/> 创建，在工作区所有者线程同步使用。
/// 已接受变化立即成为工程状态；结束或捕获丢失保留这些变化。结束或历史连续性失效后不可重新绑定，
/// 即使撤销／重做返回原工程引用也不能复活。此对象只捕获输入基线，不持有第二个可编辑工程。
/// </remarks>
public sealed class LyricTimingEditGesture : IDisposable
{
    private readonly ProjectWorkspace _workspace;
    private readonly long _generation;
    private readonly ProjectEditMergeIdentity _identity;
    private Project _lastProject;
    private object _continuityToken;
    private bool _active = true;
    private bool _updating;

    internal LyricTimingEditGesture(ProjectWorkspace workspace, LyricTimingEditBaseline baseline)
    {
        _workspace = workspace;
        _generation = baseline.ExpectedWorkspaceGeneration;
        Target = baseline.Target;
        _lastProject = workspace.Project;
        _continuityToken = workspace.History.EditContinuityToken;
        _identity = new("gesture", [Target], new object());
    }

    /// <summary>此次拖动绑定的明确分支与稳定目标。</summary>
    public LyricTimingEditTarget Target { get; }

    /// <summary>是否仍可接受更新；发现失效基线后永久终止。</summary>
    public bool IsActive
    {
        get
        {
            // 自身提交的同步历史通知先于 Workspace.Project 赋值，观察性读取不能把这段过渡判为失效。
            if (_active && !_updating && (_workspace.Generation != _generation
                || !ReferenceEquals(_workspace.Project, _lastProject)
                || !ReferenceEquals(_workspace.History.EditContinuityToken, _continuityToken)))
                _active = false;
            return _active;
        }
    }

    /// <summary>即时提交本手势的最新端点；域错误保留手势，基线失效则永久拒绝。</summary>
    /// <param name="startUs">新起点；为空时保留最后已接受的精确起点。</param>
    /// <param name="endUs">新终点；为空时保留最后已接受的精确终点。</param>
    /// <param name="contentDurationUs">已准备的媒体时长；非正值不增加媒体上界。</param>
    /// <returns>拒绝或无变化保留原引用；接受变化立即提交并合并到本手势。</returns>
    /// <remarks>支持提交期间同步只读观察与显式结束；重入更新会拒绝，其他重入工程写入不受支持。</remarks>
    public LyricTimingEditResult Update(long? startUs, long? endUs, long contentDurationUs = 0)
    {
        if (_updating || !IsActive) return new(false, _workspace.Project, ProjectTimingChangeSet.None,
            new(LyricTimingEditErrorCode.GestureStale));
        if (!LyricTimingEditUseCase.TryReadTime(_workspace.Project, Target, out TimeRange? time))
        {
            _active = false;
            return new(false, _workspace.Project, ProjectTimingChangeSet.None,
                new(LyricTimingEditErrorCode.TargetStale));
        }
        _updating = true;
        try
        {
            LyricTimingEditResult result = LyricTimingEditUseCase.SetTimeCore(_workspace,
                new(_workspace, _generation, Target, time, startUs, endUs, contentDurationUs), mergeable: true, _identity);
            if (result.Accepted)
            {
                _lastProject = result.Project;
                _continuityToken = _workspace.History.EditContinuityToken;
            }
            return result;
        }
        finally { _updating = false; }
    }

    /// <summary>结束本手势并保留已接受变化；不会打断之后开始的其他手势或操作。</summary>
    public void End()
    {
        if (!_active) return;
        _active = false;
        _workspace.History.EndEditMerge(_identity,
            _updating ? _workspace.History.EditContinuityToken : _continuityToken);
    }

    /// <summary>释放时结束本手势；不回滚工程。</summary>
    public void Dispose() => End();
}
