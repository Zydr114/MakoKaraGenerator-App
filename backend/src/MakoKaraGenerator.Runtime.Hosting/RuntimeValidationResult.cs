using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Runtime.Hosting;

/// <summary>固定帧起始时刻计划，两种输出各自使用独立会话。</summary>
public sealed record RuntimeValidationPlan
{
    /// <summary>每种输出固定帧数，严格为正且总数可表示。</summary>
    public RuntimeValidationPlan(int framesPerOutput)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerOutput);
        FramesPerOutput = framesPerOutput; TotalFrames = checked(framesPerOutput * 2);
    }
    /// <summary>每种输出需要完成的帧数。</summary>
    public int FramesPerOutput { get; }
    /// <summary>普通和透明帧数之和。</summary>
    public int TotalFrames { get; }
}

/// <summary>真实已完成帧数、输出模式及整数时间，不包含显示标签。</summary>
public sealed record RuntimeValidationProgress(int CompletedFrames, int TotalFrames, bool Transparent, long TimeUs);
/// <summary>保留输出模式和每次真实诊断发生。</summary>
public sealed record RuntimeValidationDiagnostic(bool Transparent, RuntimeDiagnosticOccurrence Occurrence);

/// <summary>完整来源或后端失败消息，输出标签和拼接文案由UI负责。</summary>
public sealed record RuntimeValidationIssue(bool Transparent, long TimeUs, string Code, string? Message, bool IsError)
{
    /// <summary>实际材质来源和旁路上下文。</summary>
    public RenderMaterialDiagnostic? Material { get; init; }
    /// <summary>实际图绑定和旁路上下文。</summary>
    public RenderImageEffectDiagnostic? Graph { get; init; }
    /// <summary>固定包及启用实例的真实源码诊断。</summary>
    public ProjectEffectSourceDiagnostic? Compilation { get; init; }
    /// <summary>作者错误的原始逻辑源码位置。</summary>
    public SourceLocation? Location { get; init; }
    /// <summary>准备试样的选择失败标识，界面负责翻译。</summary>
    public string? SampleFailureCode { get; init; }
}

/// <summary>实际帧数和完整汇总诊断；没有成功的部分输出替代。</summary>
public sealed record RuntimeValidationResult(bool Succeeded, int OpaqueFrames, int TransparentFrames,
    ImmutableArray<RuntimeValidationIssue> Issues)
{
    /// <summary>全部真实发生，不受汇总去重影响。</summary>
    public ImmutableArray<RuntimeValidationDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>可选报告累计预算；检查在报告集合增长之前执行，默认调用保持原语义。</summary>
public sealed class RuntimeValidationBudget
{
    /// <summary>设置发生和汇总上限及宿主的有界序列化检查。</summary>
    public RuntimeValidationBudget(int maximumIssues, int maximumOccurrences,
        Action<RuntimeValidationIssue>? inspectIssue = null, Action<RuntimeValidationDiagnostic>? inspectDiagnostic = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumIssues);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOccurrences);
        MaximumIssues = maximumIssues; MaximumOccurrences = maximumOccurrences;
        InspectIssue = inspectIssue; InspectDiagnostic = inspectDiagnostic;
    }
    /// <summary>汇总问题上限。</summary>
    public int MaximumIssues { get; }
    /// <summary>两种输出的完整发生上限。</summary>
    public int MaximumOccurrences { get; }
    /// <summary>加入汇总之前的可选预算检查。</summary>
    public Action<RuntimeValidationIssue>? InspectIssue { get; }
    /// <summary>加入实际发生之前的可选预算检查。</summary>
    public Action<RuntimeValidationDiagnostic>? InspectDiagnostic { get; }
}

/// <summary>完整报告预算耗尽，不得作为截断的成功或失败报告继续发布。</summary>
public sealed class RuntimeValidationBudgetExceededException : Exception
{
    /// <summary>创建预算失败。</summary>
    public RuntimeValidationBudgetExceededException() : base("完整运行验证报告超过预算。") { }
}
