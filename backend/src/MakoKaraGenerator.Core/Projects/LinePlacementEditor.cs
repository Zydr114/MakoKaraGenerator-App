using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>独立行位置的纯编辑操作；稳定 ID 校验失败时整批拒绝，无变化返回原实例。</summary>
public static class LinePlacementEditor
{
    /// <summary>把现有排版默认解析为独立几何位置，不修改样式数据。</summary>
    /// <param name="style">当前行的生效样式。</param>
    /// <returns>水平对齐对应的边缘或中心锚点，纵向位置沿用样式比例。</returns>
    public static ScenePlacement ResolveDefault(LyricStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return new ScenePlacement
        {
            Position = new(style.HorizontalAlignment switch
            {
                LyricHorizontalAlignment.Left => 0, LyricHorizontalAlignment.Right => 1, _ => .5,
            }, style.VerticalCenterRatio),
            Anchor = style.HorizontalAlignment switch
            {
                LyricHorizontalAlignment.Left => ScenePlacementAnchor.LeftCenter,
                LyricHorizontalAlignment.Right => ScenePlacementAnchor.RightCenter,
                _ => ScenePlacementAnchor.Center,
            },
        };
    }

    /// <summary>预演一组位置写入，不改变输入工程。</summary>
    /// <param name="project">源工程。</param>
    /// <param name="updates">行 ID 与新位置；空位置表示恢复默认。</param>
    /// <param name="result">完整结果，失败时为源工程。</param>
    /// <returns>目标存在且数据有效时为真。</returns>
    public static bool TryApply(Project project, IReadOnlyDictionary<string, LyricLocalTransform?> updates, out Project result)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(updates);
        result = project;
        if (updates.Count == 0 || updates.Any(entry => string.IsNullOrWhiteSpace(entry.Key)
            || entry.Value is { IsValid: false })) return false;
        var allIds = project.Lyrics.AllLines.Select(line => line.Id).ToArray();
        if (allIds.Distinct(StringComparer.Ordinal).Count() != allIds.Length
            || updates.Keys.Any(id => !allIds.Contains(id, StringComparer.Ordinal))) return false;
        var presentation = project.LyricPresentation;
        foreach (LyricBranch branch in project.Lyrics.Branches)
        {
            var definition = presentation.Resolve(branch.Id);
            foreach (var line in branch.Lines)
                if (updates.TryGetValue(line.Id, out LyricLocalTransform? placement))
                    definition = PresentationEditor.SetLine(definition, line.Id,
                        (definition.FindLine(line.Id) ?? new() { LineId = line.Id }) with { LocalTransform = placement });
            presentation = presentation.SetBranch(definition);
        }
        if (!ReferenceEquals(presentation, project.LyricPresentation)) result = project with { LyricPresentation = presentation };
        var report = Validation.PresentationValidator.Validate(result);
        if (report.HasErrors) { result = project; return false; }
        return true;
    }

    /// <summary>按所给行序，在已有最小和最大锚点之间平均分布；其余位置属性不变。</summary>
    /// <param name="placements">至少三行的生效位置，顺序由调用方明确决定。</param>
    /// <param name="vertical">纵向分布，否则横向。</param>
    /// <returns>每行的新位置。</returns>
    public static IReadOnlyList<ScenePlacement> Distribute(IReadOnlyList<ScenePlacement> placements, bool vertical)
    {
        ArgumentNullException.ThrowIfNull(placements);
        if (placements.Count < 3 || placements.Any(placement => !placement.IsValid))
            throw new ArgumentException("平均分布需要至少三行有效位置。", nameof(placements));
        double low = placements.Min(placement => vertical ? placement.Position.Y : placement.Position.X);
        double high = placements.Max(placement => vertical ? placement.Position.Y : placement.Position.X);
        return placements.Select((placement, index) => placement with
        {
            Position = vertical
                ? placement.Position with { Y = low + (high - low) * index / (placements.Count - 1) }
                : placement.Position with { X = low + (high - low) * index / (placements.Count - 1) },
        }).ToArray();
    }
}
