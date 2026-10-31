using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>源时间状态；不将导入或估算假装为人工确认。</summary>
public enum SourceTimingStatus
{
    /// <summary>无时间。</summary>
    Missing,
    /// <summary>外部导入。</summary>
    Imported,
    /// <summary>自动估算。</summary>
    Estimated,
    /// <summary>人工修改。</summary>
    Edited,
    /// <summary>人工确认。</summary>
    Confirmed,
    /// <summary>非法，不能用于效果执行。</summary>
    Invalid,
}

/// <summary>只读源分支，不返回 Core 对象或展示容器。</summary>
/// <param name="Id">真实源身份。</param><param name="Name">源名称。</param>
/// <param name="Role">稳定角色名称，不是场景节点类型。</param><param name="SourceOrder">分支源序号。</param>
/// <param name="LineIds">原始源顺序。</param>
public sealed record SourceBranch(BranchId Id, string Name, string Role, int SourceOrder, ImmutableArray<LineId> LineIds);

/// <summary>只读源行；TimeRange 管可见区间，不代替元素演唱时间。</summary>
/// <param name="Id">源行。</param><param name="BranchId">源分支。</param><param name="Text">源文字。</param>
/// <param name="TimeRange">原行区间；缺失为空。</param><param name="TimingStatus">源状态。</param>
/// <param name="SourceOrder">所在分支内序号。</param><param name="TokenIds">源元素順序。</param>
/// <param name="RubyIds">真实注音顺序。</param>
public sealed record SourceLine(LineId Id, BranchId BranchId, string Text, TimeRange? TimeRange,
    SourceTimingStatus TimingStatus, int SourceOrder, ImmutableArray<TokenId> TokenIds, ImmutableArray<RubyId> RubyIds);

/// <summary>只读源元素；不包含帧状态，不制造前奏或纯组的 TokenId。</summary>
/// <param name="Id">源元素。</param><param name="BranchId">所属分支。</param><param name="LineId">所属源行。</param>
/// <param name="Text">原文本。</param><param name="TimeRange">源区间；缺失为空。</param>
/// <param name="TimingStatus">源状态。</param><param name="SourceOrder">所在行内序号。</param>
public sealed record SourceToken(TokenId Id, BranchId BranchId, LineId LineId, string Text,
    TimeRange? TimeRange, SourceTimingStatus TimingStatus, int SourceOrder);

/// <summary>只读注音组；片段保留独立源时间，组不新增演唱事件。</summary>
/// <param name="Id">真实注音身份。</param><param name="LineId">所属源行。</param>
/// <param name="BaseTokenId">基础元素。</param><param name="Text">注音原文。</param>
/// <param name="Language">可选语言标签。</param>
public sealed record SourceRuby(RubyId Id, LineId LineId, TokenId BaseTokenId, string Text, string? Language)
{
    /// <summary>注音片段的只读时间投影，顺序与源注音一致。</summary>
    public ImmutableArray<SourceRubySegment> Segments { get; init; } = [];
}

/// <summary>注音片段的只读脚本投影；不暴露 Core 对象。</summary>
public sealed record SourceRubySegment(string Id, string Text, TimeRange? TimeRange, SourceTimingStatus TimingStatus);

/// <summary>只读完整源目录；不随实例输出绑定裁剪或以当前可见文字代替历史来源。</summary>
public sealed class ContentCatalog
{
    private readonly ImmutableDictionary<TokenId, SourceToken> _tokens;
    private readonly ImmutableDictionary<LineId, SourceLine> _lines;
    private readonly ImmutableDictionary<BranchId, SourceBranch> _branches;
    private readonly ImmutableDictionary<RubyId, SourceRuby> _ruby;

    /// <summary>建立固定目录；重复身份、丢失关联、乱序或非法区间拒绝，不修正源数据。</summary>
    public ContentCatalog(ImmutableArray<SourceBranch> branches, ImmutableArray<SourceLine> lines,
        ImmutableArray<SourceToken> tokens, ImmutableArray<SourceRuby> ruby)
    {
        if (branches.IsDefault || lines.IsDefault || tokens.IsDefault || ruby.IsDefault)
            throw new ArgumentException("源目录需要明确的不可变集合。");
        Branches = branches; Lines = lines; Tokens = tokens; Ruby = ruby;
        _branches = branches.ToImmutableDictionary(static item => item.Id);
        _lines = lines.ToImmutableDictionary(static item => item.Id);
        _tokens = tokens.ToImmutableDictionary(static item => item.Id);
        _ruby = ruby.ToImmutableDictionary(static item => item.Id);
        Validate();
    }
    /// <summary>源分支順序。</summary>
    public ImmutableArray<SourceBranch> Branches { get; }
    /// <summary>先分支再源行順序。</summary>
    public ImmutableArray<SourceLine> Lines { get; }
    /// <summary>先分支、源行，再行内元素顺序。</summary>
    public ImmutableArray<SourceToken> Tokens { get; }
    /// <summary>真实注音集合。</summary>
    public ImmutableArray<SourceRuby> Ruby { get; }
    /// <summary>读取源元素；目标丢失抛具名 KeyNotFoundException。</summary>
    public SourceToken GetToken(TokenId id) => _tokens.TryGetValue(id, out var item)
        ? item : throw new KeyNotFoundException("源元素不存在：" + id.Value);
    /// <summary>查询源元素，不伪造缺失目标。</summary>
    public bool TryGetToken(TokenId id, out SourceToken? token) => _tokens.TryGetValue(id, out token);
    /// <summary>读取源行。</summary>
    public SourceLine GetLine(LineId id) => _lines.TryGetValue(id, out var item)
        ? item : throw new KeyNotFoundException("源行不存在：" + id.Value);
    /// <summary>读取分支。</summary>
    public SourceBranch GetBranch(BranchId id) => _branches.TryGetValue(id, out var item)
        ? item : throw new KeyNotFoundException("源分支不存在：" + id.Value);
    /// <summary>读取注音。</summary>
    public SourceRuby GetRuby(RubyId id) => _ruby.TryGetValue(id, out var item)
        ? item : throw new KeyNotFoundException("注音不存在：" + id.Value);
    /// <summary>按明确的 ordinal 规则查询，不使用系统区域或模糊匹配。</summary>
    public ImmutableArray<SourceToken> FindTokens(string text, StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (comparison is not (StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(comparison), "只允许明确的 ordinal 文本比较。");
        return [.. Tokens.Where(token => token.Text.Equals(text, comparison))];
    }

    private void Validate()
    {
        int linesSeen = 0, tokensSeen = 0, rubySeen = 0;
        HashSet<string> segmentIds = new(StringComparer.Ordinal);
        foreach (var (branch, index) in Branches.Select((branch, index) => (branch, index)))
        {
            if (!branch.Id.IsValid || branch.SourceOrder != index || branch.LineIds.IsDefault)
                throw new ArgumentException("分支身份或源顺序无效。");
            foreach (var (lineId, lineIndex) in branch.LineIds.Select((id, i) => (id, i)))
            {
                SourceLine line = GetLine(lineId);
                if (!line.Id.IsValid || line.BranchId != branch.Id || line.SourceOrder != lineIndex
                    || line.TokenIds.IsDefault || line.RubyIds.IsDefault || linesSeen >= Lines.Length || Lines[linesSeen++] != line)
                    throw new ArgumentException("源行关联或顺序无效。");
                ValidateTime(line.TimeRange, line.TimingStatus);
                foreach (var (tokenId, tokenIndex) in line.TokenIds.Select((id, i) => (id, i)))
                {
                    SourceToken token = GetToken(tokenId);
                    if (!token.Id.IsValid || token.BranchId != branch.Id || token.LineId != line.Id
                        || token.SourceOrder != tokenIndex || tokensSeen >= Tokens.Length || Tokens[tokensSeen++] != token)
                        throw new ArgumentException("源元素关联或顺序无效。");
                    ValidateTime(token.TimeRange, token.TimingStatus);
                }
                foreach (RubyId id in line.RubyIds)
                {
                    SourceRuby item = GetRuby(id);
                    if (!id.IsValid || item.LineId != line.Id || !line.TokenIds.Contains(item.BaseTokenId)
                        || rubySeen >= Ruby.Length || Ruby[rubySeen++] != item) throw new ArgumentException("注音关联或顺序无效。");
                    foreach (SourceRubySegment segment in item.Segments)
                    {
                        if (string.IsNullOrWhiteSpace(segment.Id) || !segmentIds.Add(segment.Id)
                            || string.IsNullOrEmpty(segment.Text))
                            throw new ArgumentException("注音片段身份或文本无效。");
                        ValidateTime(segment.TimeRange, segment.TimingStatus);
                    }
                }
            }
        }
        if (linesSeen != Lines.Length || tokensSeen != Tokens.Length || rubySeen != Ruby.Length)
            throw new ArgumentException("源目录含未归属对象。");
    }
    private static void ValidateTime(TimeRange? range, SourceTimingStatus status)
    {
        if (!Enum.IsDefined(status) || status == SourceTimingStatus.Invalid
            || (status == SourceTimingStatus.Missing ? range is not null : range is not { IsValid: true }))
            throw new ArgumentException("源时间和状态不一致，不修正数据。");
    }
}
