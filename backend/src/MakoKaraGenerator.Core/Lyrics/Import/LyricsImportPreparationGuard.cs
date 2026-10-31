using System.Collections.ObjectModel;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>解析与候选构造的可选累计逻辑工作配额；不是 CLR 堆上限或操作系统沙箱。</summary>
public sealed class LyricsImportPreparationGuard
{
    private readonly long _maximum;
    private long _used;
    private readonly Action<LyricsImportIssue>? _beforeIssue;

    /// <summary>创建一次准备专属的配额；所有调用须由该次准备顺序执行。</summary>
    /// <param name="maximum">累计逻辑工作字节上限。</param>
    /// <param name="beforeIssue">问题保留前的可选完整报告预算检查。</param>
    public LyricsImportPreparationGuard(long maximum, Action<LyricsImportIssue>? beforeIssue = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        _maximum = maximum; _beforeIssue = beforeIssue;
    }

    /// <summary>已经消费的逻辑工作字节；释放临时对象不返还配额。</summary>
    public long UsedBytes => _used;

    /// <summary>在分配或扩容前消费配额。</summary>
    /// <param name="bytes">本次逻辑工作字节。</param>
    public void Reserve(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes > _maximum - _used) throw new LyricsImportPreparationLimitException();
        _used += bytes;
    }

    /// <summary>在保留一个模型、中间项或集合项前计费，含保守集合与映射开销。</summary>
    /// <param name="text">该项需要处理的文本；共享文本仍计入累计处理量。</param>
    public void Item(string? text = null) => Reserve(512L + 2L * (text?.Length ?? 0));

    /// <summary>在整批行字符串及引用数组分配前计量；不限制输入行数或改变空行语义。</summary>
    /// <param name="text">尚未分行的完整文本。</param>
    public void Lines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        long count = 1;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\r') { count++; if (i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (text[i] is '\n' or '\f' or '\u0085' or '\u2028' or '\u2029') count++;
        Reserve(4L * text.Length + 64L * count + 64);
    }

    internal void Fields(string text, ReadOnlySpan<char> delimiters)
    {
        long count = 1;
        foreach (char value in text) if (delimiters.Contains(value)) count++;
        Reserve(64L * count + 2L * text.Length + 64);
    }

    internal static LyricsImportPreparationGuard? Of(IList<LyricsImportIssue> issues) =>
        (issues as BudgetedImportList<LyricsImportIssue>)?.Guard;

    internal static LyricToken Token(LyricsImportPreparationGuard? guard, string id, string text,
        Time.TimeRange? time = null, TimingStatus timingStatus = TimingStatus.Missing)
    {
        guard?.Item(text);
        return LyricToken.Create(id, text, time, timingStatus);
    }

    internal void Issue(LyricsImportIssue issue)
    {
        _beforeIssue?.Invoke(issue);
        Item(issue.RawText); Reserve(2L * issue.Message.Length);
    }
}

/// <summary>准备的累计逻辑工作配额耗尽；调用方必须完整拒绝，不能提交部分结果。</summary>
public sealed class LyricsImportPreparationLimitException : Exception
{
    /// <summary>创建不包含用户原文的稳定拒绝。</summary>
    public LyricsImportPreparationLimitException() : base("歌词准备超过累计逻辑工作配额。") { }
}

internal sealed class BudgetedImportList<T>(LyricsImportPreparationGuard? guard) : Collection<T>, IReadOnlyList<T>
{
    internal LyricsImportPreparationGuard? Guard => guard;
    protected override void InsertItem(int index, T item) { BeforeRetaining(item); base.InsertItem(index, item); }
    protected override void SetItem(int index, T item) { BeforeRetaining(item); base.SetItem(index, item); }
    private void BeforeRetaining(T item)
    {
        if (item is LyricsImportIssue issue) guard?.Issue(issue);
        else guard?.Item(item switch
        {
            LyricToken token => token.Text,
            LyricLine line => line.Text,
            ParsedLyricsLine line => line.Text,
            ParsedLyricsToken token => token.Text,
            Lrc.LrcEntry entry => entry.Text,
            Lrc.LrcDiagnostic diagnostic => diagnostic.RawText,
            string text => text,
            _ => null,
        });
    }
    internal bool Exists(Predicate<T> match) => this.Any(item => match(item));
    internal void RemoveRange(int index, int count) { for (int i = count - 1; i >= 0; i--) RemoveAt(index + i); }
    internal void AddRange(IEnumerable<T> items) { foreach (T item in items) Add(item); }
}

internal sealed class BudgetedImportText(LyricsImportPreparationGuard? guard)
{
    private readonly System.Text.StringBuilder _value = new();
    internal int Length => _value.Length;
    internal void Append(string? text) { guard?.Reserve(2L * (text?.Length ?? 0) + 8); _value.Append(text); }
    internal void Append(char value) { guard?.Reserve(4); _value.Append(value); }
    internal void Append(string text, int start, int count) { guard?.Reserve(2L * count + 8); _value.Append(text, start, count); }
    internal void Clear() => _value.Clear();
    public override string ToString() { guard?.Reserve(32L + 2L * Length); return _value.ToString(); }
}

internal sealed class BudgetedImportBuilder<T>(LyricsImportPreparationGuard? guard)
{
    private readonly System.Collections.Immutable.ImmutableArray<T>.Builder _items = System.Collections.Immutable.ImmutableArray.CreateBuilder<T>();
    internal int Count => _items.Count;
    internal T this[int index] { get => _items[index]; set { guard?.Item(); _items[index] = value; } }
    internal void Add(T item)
    {
        guard?.Item(item switch { ParsedLyricsToken token => token.Text, LyricToken token => token.Text, _ => null });
        _items.Add(item);
    }
    internal System.Collections.Immutable.ImmutableArray<T> ToImmutable()
    {
        guard?.Reserve(64L + 16L * Count);
        return _items.ToImmutable();
    }
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
}
