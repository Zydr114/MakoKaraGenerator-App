using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>一次实际报告的稳定身份；同文不同调用仍是不同发生。</summary>
/// <param name="Epoch">独立日志代次。</param><param name="Sequence">该代次内从一开始的顺序。</param><param name="Diagnostic">完整作者及宿主来源。</param>
public readonly record struct RuntimeDiagnosticOccurrence(Guid Epoch, long Sequence, RuntimeDiagnostic Diagnostic);

/// <summary>共享不可变分块的完整诊断视图；不持有作者实例或可变日志。</summary>
public sealed class RuntimeDiagnosticSnapshot
{
    internal sealed record Chunk(Chunk? Previous, ImmutableArray<RuntimeDiagnostic> Records, int Start);
    internal sealed record Run(Guid Epoch, Chunk Head, int Count);
    internal ImmutableArray<Run> Runs { get; }
    private ImmutableArray<RuntimeDiagnostic> _array;
    internal RuntimeDiagnosticSnapshot(ImmutableArray<Run> runs, int count, bool hasError)
    { Runs = runs; Count = count; HasError = hasError; }
    /// <summary>没有真实报告的共享快照。</summary>
    public static RuntimeDiagnosticSnapshot Empty { get; } = new([], 0, false);
    /// <summary>完整发生次数，不按消息文本去重。</summary>
    public int Count { get; }
    /// <summary>记录建立时已检查的失败标记；成功帧不重新扫描全历史。</summary>
    public bool HasError { get; }
    /// <summary>兼容旧明确数组；只校验一次，每次调用建立独立发生身份。</summary>
    public static RuntimeDiagnosticSnapshot FromArray(ImmutableArray<RuntimeDiagnostic> records)
    {
        if (records.IsDefault) throw new ArgumentException("诊断数组不能是default。", nameof(records));
        if (records.IsEmpty) return Empty;
        var buffer = new RuntimeDiagnosticBuffer();
        foreach (var record in records) buffer.Add(record);
        return buffer.Snapshot();
    }
    /// <summary>导入完整发生快照，保留真实日志代次和连续序号，并隔离调用方集合。</summary>
    /// <remarks>每个代次必须非空、只出现一次，序号从一开始连续递增；不接受增量页冒充完整快照。</remarks>
    public static RuntimeDiagnosticSnapshot FromOccurrences(IEnumerable<RuntimeDiagnosticOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        var runs = ImmutableArray.CreateBuilder<Run>();
        var records = ImmutableArray.CreateBuilder<RuntimeDiagnostic>();
        HashSet<Guid> epochs = [];
        Guid epoch = Guid.Empty;
        int count = 0;
        bool hasError = false;
        foreach (var occurrence in occurrences)
        {
            if (occurrence.Epoch == Guid.Empty || occurrence.Diagnostic?.Diagnostic is null)
                throw new ArgumentException("诊断发生必须有真实非空代次和有效报告。", nameof(occurrences));
            if (occurrence.Epoch != epoch)
            {
                CommitRun();
                epoch = occurrence.Epoch;
                if (!epochs.Add(epoch))
                    throw new ArgumentException("完整诊断快照不能重复日志代次。", nameof(occurrences));
            }
            if (occurrence.Sequence != (long)records.Count + 1)
                throw new ArgumentException("诊断代次内序号必须从一开始连续递增。", nameof(occurrences));
            records.Add(occurrence.Diagnostic);
            count = checked(count + 1);
            hasError |= occurrence.Diagnostic.Diagnostic.Severity == DiagnosticSeverity.Error;
        }
        CommitRun();
        return count == 0 ? Empty : new(runs.ToImmutable(), count, hasError);

        void CommitRun()
        {
            if (records.Count == 0) return;
            ImmutableArray<RuntimeDiagnostic> isolated = records.ToImmutable();
            runs.Add(new(epoch, new(null, isolated, 0), isolated.Length));
            records.Clear();
        }
    }
    /// <summary>追加本次绘制等独立报告，保持共享边界与本帧的发生顺序。</summary>
    public RuntimeDiagnosticSnapshot Append(ImmutableArray<RuntimeDiagnostic> records)
    {
        if (records.IsDefault) throw new ArgumentException("诊断数组不能是default。", nameof(records));
        if (records.IsEmpty) return this;
        var buffer = new RuntimeDiagnosticBuffer(this);
        foreach (var record in records) buffer.Add(record);
        return buffer.Snapshot();
    }
    /// <summary>显式取得完整兼容数组；该快照内缓存，生产增量消费者无需物化。</summary>
    public ImmutableArray<RuntimeDiagnostic> ToImmutableArray()
    {
        if (_array.IsDefault)
        {
            var builder = ImmutableArray.CreateBuilder<RuntimeDiagnostic>(Count);
            foreach (var item in ReadAll()) builder.Add(item.Diagnostic);
            ImmutableInterlocked.InterlockedInitialize(ref _array, builder.MoveToImmutable());
        }
        return _array;
    }
    /// <summary>按完整发生顺序枚举，不复制诊断记录数组。</summary>
    public IEnumerable<RuntimeDiagnosticOccurrence> ReadAll()
    {
        foreach (var run in Runs)
            foreach (var item in ReadRun(run, 0)) yield return item;
    }
    internal static IEnumerable<RuntimeDiagnosticOccurrence> ReadRun(Run run, long consumed)
    {
        if (consumed >= run.Count) yield break;
        Stack<Chunk> chunks = [];
        for (Chunk? chunk = run.Head; chunk is not null && chunk.Start + chunk.Records.Length > consumed; chunk = chunk.Previous)
            chunks.Push(chunk);
        while (chunks.TryPop(out var chunk))
            for (int index = (int)Math.Max(0, consumed - chunk.Start); index < chunk.Records.Length; index++)
                yield return new(run.Epoch, (long)chunk.Start + index + 1, chunk.Records[index]);
    }
}
