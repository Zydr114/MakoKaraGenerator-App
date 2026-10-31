using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>稳定注音组目标；每层身份必须真实匹配，不按下标或其它组回退。</summary>
public sealed record RubyEditTarget(string BranchId, string LineId, string TokenId, string RubyId);
/// <summary>注音编辑捕获的完整原组和工作区身份。</summary>
public sealed record RubySegmentEditBaseline(ProjectWorkspace Workspace, long Generation, RubyEditTarget Target, RubyAnnotation Ruby);
/// <summary>原子批次中的独立绝对时间。</summary>
public sealed record RubySegmentTimeEdit(RubySegmentEditBaseline Baseline, string SegmentId, long StartUs, long EndUs);
/// <summary>注音编辑的稳定拒绝原因。</summary>
public enum RubySegmentEditErrorCode
{
    /// <summary>工作区或代次改变。</summary>
    WorkspaceStale,
    /// <summary>目标不存在、父子身份不符或原组已变化。</summary>
    TargetStale,
    /// <summary>时间起点为负。</summary>
    InvalidStart,
    /// <summary>终点不晚于起点。</summary>
    InvalidRange,
    /// <summary>片段正文不能为空。</summary>
    EmptyText,
    /// <summary>顺序必须完整、唯一且只包含原组片段。</summary>
    InvalidOrder,
    /// <summary>批次目标重复。</summary>
    DuplicateTarget,
    /// <summary>时间批次必须非空。</summary>
    InvalidBatch,
    /// <summary>领域结构或展示验证失败。</summary>
    DomainValidationFailed,
    /// <summary>无法分配唯一标识。</summary>
    IdentifierExhausted,
}
/// <summary>注音编辑结果；拒绝和无变化保留原工程引用。</summary>
public sealed record RubySegmentEditResult(bool Accepted, Project Project, bool Changed,
    RubySegmentEditErrorCode? Error = null, string? Detail = null, string? SelectedSegmentId = null,
    string? CreatedSegmentId = null, int ChangedSegmentCount = 0);
/// <summary>尚未提交的固定注音候选；读取不产生历史。</summary>
public sealed class PreparedRubySegmentEdit
{
    internal PreparedRubySegmentEdit(ProjectWorkspace workspace, Project source, RubySegmentEditResult result, string name)
    { Workspace = workspace; Generation = workspace.Generation; Source = source; Result = result; Name = name; }
    /// <summary>固定候选与真实结果。</summary>
    public RubySegmentEditResult Result { get; }
    internal ProjectWorkspace Workspace { get; }
    internal long Generation { get; }
    internal Project Source { get; }
    internal string Name { get; }
}
