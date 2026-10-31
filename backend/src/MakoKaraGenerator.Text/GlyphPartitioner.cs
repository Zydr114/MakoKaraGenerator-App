using System.Collections.Immutable;

namespace MakoKaraGenerator.Text;

/// <summary>源元素在整行中的完整文本区间，不用于裁切字形。</summary>
/// <param name="Start">UTF-16 关联起点。</param><param name="Length">源文本长度。</param>
public readonly record struct TextSpan(int Start, int Length)
{
    /// <summary>终点，不含。</summary>
    public int End => checked(Start + Length);
}

/// <summary>绘制单元与一个源文本元素的关系；不可拆单元不会被静默切开。</summary>
/// <param name="SourceIndex">源元素索引，不是字形索引。</param>
/// <param name="DrawUnitIndices">与该源元素相交的真实绘制单元。</param>
/// <param name="CanTransformIndependently">相交单元全部完全包含在该源元素内。</param>
/// <param name="DiagnosticCode">不支持时的稳定代码；支持为空。</param>
public sealed record DrawUnitPartition(int SourceIndex, ImmutableArray<int> DrawUnitIndices,
    bool CanTransformIndependently, string? DiagnosticCode);

/// <summary>把整行唯一字形结果关联到源元素；只检查关联，不重新整形或修改源切分。</summary>
public static class GlyphPartitioner
{
    /// <summary>区间须连续、非空且完整覆盖源行；跨源元素字簇返回明确不可独立状态。</summary>
    /// <param name="line">真实整行结果。</param><param name="sourceSpans">按源顺序的区间。</param>
    /// <returns>每个源元素的真实字形单元及能力；不会用 UTF-16 裁开字形。</returns>
    public static ImmutableArray<DrawUnitPartition> Partition(ShapedLine line, IReadOnlyList<TextSpan> sourceSpans)
    {
        ArgumentNullException.ThrowIfNull(line); ArgumentNullException.ThrowIfNull(sourceSpans);
        int end = 0;
        foreach (TextSpan span in sourceSpans)
        {
            if (span.Start != end || span.Length <= 0) throw new ArgumentException("源区间须按顺序连续且非空。", nameof(sourceSpans));
            end = span.End;
        }
        if (end != line.Text.Length) throw new ArgumentException("源区间不能完整关联行文本。", nameof(sourceSpans));
        ImmutableArray<DrawUnitPartition>.Builder partitions = ImmutableArray.CreateBuilder<DrawUnitPartition>(sourceSpans.Count);
        for (int i = 0; i < sourceSpans.Count; i++)
        {
            TextSpan span = sourceSpans[i];
            ImmutableArray<int> units = [.. line.DrawUnits.Select((unit, index) => (unit, index))
                .Where(item => item.unit.Utf16Start < span.End && span.Start < item.unit.Utf16End)
                .Select(static item => item.index)];
            bool independent = line.MappingIsExact && !units.IsEmpty
                && units.All(index => line.DrawUnits[index].Utf16Start >= span.Start && line.DrawUnits[index].Utf16End <= span.End);
            partitions.Add(new(i, units, independent, independent ? null : "UnsupportedGlyphPartition"));
        }
        return partitions.ToImmutable();
    }
}
