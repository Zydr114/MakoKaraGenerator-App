using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using MakoKaraGenerator.Core.Internal;

namespace MakoKaraGenerator.Core.Diagnostics;

/// <summary>
/// 一次校验（或一次读取）产生的诊断集合。
/// </summary>
/// <remarks>
/// 不可变；<see cref="Issues"/> 按产生顺序排列，便于稳定断言与逐条展示。
/// </remarks>
public sealed record ValidationReport
{
    private ImmutableArray<ProjectIssue> _issues;

    /// <summary>没有诊断的报告。</summary>
    public static ValidationReport Empty { get; } = new() { Issues = [] };

    /// <summary>
    /// 诊断项，按产生顺序排列。
    /// </summary>
    /// <remarks>取值时把 <c>default</c> 规范化为空序列；原因见 <see cref="Lyrics.LyricLine.Tokens"/>。</remarks>
    public ImmutableArray<ProjectIssue> Issues
    {
        get => _issues.IsDefault ? ImmutableArray<ProjectIssue>.Empty : _issues;
        init => _issues = value;
    }

    /// <summary>是否存在阻断性错误。</summary>
    public bool HasErrors => CountOf(IssueSeverity.Error) > 0;

    /// <summary>是否存在警告。</summary>
    public bool HasWarnings => CountOf(IssueSeverity.Warning) > 0;

    /// <summary>错误条目数。</summary>
    public int ErrorCount => CountOf(IssueSeverity.Error);

    /// <summary>警告条目数。</summary>
    public int WarningCount => CountOf(IssueSeverity.Warning);

    /// <summary>提示条目数。</summary>
    public int InfoCount => CountOf(IssueSeverity.Info);

    /// <summary>诊断总条数。</summary>
    public int Count => Issues.Length;

    /// <summary>是否存在指定标识的诊断。</summary>
    /// <param name="code">诊断标识。</param>
    /// <returns>存在时为 <see langword="true"/>。</returns>
    public bool Contains(DiagnosticCode code)
    {
        foreach (ProjectIssue issue in Issues)
        {
            if (issue.Code == code)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>按严重程度筛选诊断项。</summary>
    /// <param name="severity">目标严重程度。</param>
    /// <returns>匹配的诊断项序列。</returns>
    public IEnumerable<ProjectIssue> OfSeverity(IssueSeverity severity)
    {
        foreach (ProjectIssue issue in Issues)
        {
            if (issue.Severity == severity)
            {
                yield return issue;
            }
        }
    }

    /// <summary>合并另一份报告，保持两侧原有顺序。</summary>
    /// <param name="other">另一份报告，可为 <see langword="null"/>。</param>
    /// <returns>合并后的新报告。</returns>
    public ValidationReport Merge(ValidationReport? other)
    {
        if (other is null || other.Issues.IsEmpty)
        {
            return this;
        }

        return Issues.IsEmpty ? other : this with { Issues = [.. Issues, .. other.Issues] };
    }

    /// <summary>创建一份报告。</summary>
    /// <param name="issues">诊断项。</param>
    /// <returns>新的报告实例。</returns>
    public static ValidationReport From(IEnumerable<ProjectIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        return new ValidationReport { Issues = [.. issues] };
    }

    /// <summary>生成适合日志与导出报告的多行摘要。</summary>
    /// <returns>摘要文本；没有诊断时返回说明性单行文本。</returns>
    public string ToSummary()
    {
        if (Count == 0)
        {
            return "未发现问题。";
        }

        StringBuilder builder = new();
        builder.Append(
            CultureInfo.InvariantCulture,
            $"共 {Count} 条诊断：错误 {ErrorCount}、警告 {WarningCount}、提示 {InfoCount}。");

        foreach (ProjectIssue issue in Issues)
        {
            builder.AppendLine().Append("  ").Append(issue.ToString());
        }

        return builder.ToString();
    }

    /// <summary>
    /// 手写的结构相等性；原因见 <see cref="Lyrics.LyricLine.Equals(Lyrics.LyricLine?)"/>。
    /// </summary>
    /// <param name="other">另一份报告。</param>
    /// <returns>诊断内容与顺序均相等时为 <see langword="true"/>。</returns>
    public bool Equals(ValidationReport? other)
    {
        if (other is null)
        {
            return false;
        }

        return ReferenceEquals(this, other) || SequenceEquality.ContentEquals(Issues, other.Issues);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        SequenceEquality.AddContentHash(ref hash, Issues);
        return hash.ToHashCode();
    }

    private int CountOf(IssueSeverity severity)
    {
        int count = 0;

        foreach (ProjectIssue issue in Issues)
        {
            if (issue.Severity == severity)
            {
                count++;
            }
        }

        return count;
    }
}
