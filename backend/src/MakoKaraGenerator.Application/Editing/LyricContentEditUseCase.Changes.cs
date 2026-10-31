using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

public static partial class LyricContentEditUseCase
{
    /// <summary>保守描述正文、注音、元素、时间及行结构变化，供统一编辑和撤销／重做刷新出口使用。</summary>
    /// <param name="before">编辑或历史恢复前的工程。</param>
    /// <param name="after">编辑或历史恢复后的工程。</param>
    /// <returns>稳定行、结构来源及展示失效范围；未知字段要求完整刷新。</returns>
    /// <remarks>按不变子树引用跳过比较，基于当前完整相等合同分类；新增模型字段须同步更新相等合同与分类回归。</remarks>
    public static ProjectLyricsChangeSet DescribeChanges(Project before, Project after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (ReferenceEquals(before, after)) return ProjectLyricsChangeSet.None;
        if (after with { Lyrics = before.Lyrics, LyricPresentation = before.LyricPresentation } != before
            || after.Lyrics with { Branches = before.Lyrics.Branches } != before.Lyrics
            || before.Lyrics.Branches.Length != after.Lyrics.Branches.Length)
            return ProjectLyricsChangeSet.FullRefresh;
        var rows = ImmutableArray.CreateBuilder<LyricLineChange>();
        var structures = ImmutableArray.CreateBuilder<string>();
        for (int index = 0; index < before.Lyrics.Branches.Length; index++)
        {
            LyricBranch oldBranch = before.Lyrics.Branches[index];
            LyricBranch newBranch = after.Lyrics.Branches[index];
            if (ReferenceEquals(oldBranch, newBranch)) continue;
            if (newBranch with { Lines = oldBranch.Lines } != oldBranch) return ProjectLyricsChangeSet.FullRefresh;
            bool structural = !oldBranch.Lines.Select(line => line.Id).SequenceEqual(newBranch.Lines.Select(line => line.Id));
            if (structural) structures.Add(oldBranch.Id);
            Dictionary<string, LyricLine>? oldById = structural ? oldBranch.Lines.GroupBy(line => line.Id)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal) : null;
            for (int lineIndex = 0; lineIndex < newBranch.Lines.Length; lineIndex++)
            {
                LyricLine newLine = newBranch.Lines[lineIndex];
                LyricLine? oldLine = structural ? oldById!.GetValueOrDefault(newLine.Id) : oldBranch.Lines[lineIndex];
                if (oldLine is null || ReferenceEquals(oldLine, newLine)) continue;
                if (!TryDescribeLine(oldLine, newLine, out LyricLineChangeKind kind)) return ProjectLyricsChangeSet.FullRefresh;
                if (kind != LyricLineChangeKind.None) rows.Add(new(oldBranch.Id, oldLine.Id, kind));
            }
        }
        bool presentationChanged = before.LyricPresentation != after.LyricPresentation;
        return rows.Count == 0 && structures.Count == 0 && !presentationChanged ? ProjectLyricsChangeSet.None
            : new(rows.ToImmutable(), structures.ToImmutable(), presentationChanged);
    }

    private static bool TryDescribeLine(LyricLine oldLine, LyricLine newLine, out LyricLineChangeKind kind)
    {
        kind = LyricLineChangeKind.None;
        if (newLine with { Text = oldLine.Text, Tokens = oldLine.Tokens, RubyAnnotations = oldLine.RubyAnnotations,
                Time = oldLine.Time, TimingStatus = oldLine.TimingStatus } != oldLine) return false;
        if (newLine.Text != oldLine.Text) kind |= LyricLineChangeKind.Text;
        if (!newLine.RubyAnnotations.SequenceEqual(oldLine.RubyAnnotations)) kind |= LyricLineChangeKind.Ruby;
        if (newLine.Time != oldLine.Time || newLine.TimingStatus != oldLine.TimingStatus) kind |= LyricLineChangeKind.Timing;
        bool structural = !oldLine.Tokens.Select(token => token.Id).SequenceEqual(newLine.Tokens.Select(token => token.Id));
        if (structural) kind |= LyricLineChangeKind.Tokens;
        Dictionary<string, LyricToken>? oldById = structural ? oldLine.Tokens.GroupBy(token => token.Id)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal) : null;
        for (int index = 0; index < newLine.Tokens.Length; index++)
        {
            LyricToken newToken = newLine.Tokens[index];
            LyricToken? oldToken = structural ? oldById!.GetValueOrDefault(newToken.Id) : oldLine.Tokens[index];
            if (oldToken is null || ReferenceEquals(oldToken, newToken)) continue;
            if (newToken with { Text = oldToken.Text, Time = oldToken.Time, TimingStatus = oldToken.TimingStatus } != oldToken)
                return false;
            if (newToken.Text != oldToken.Text) kind |= LyricLineChangeKind.Tokens;
            if (newToken.Time != oldToken.Time || newToken.TimingStatus != oldToken.TimingStatus) kind |= LyricLineChangeKind.Timing;
        }
        return true;
    }
}
