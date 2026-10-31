namespace MakoKaraGenerator.Rendering;

/// <summary>单消费者按代次及顺序读取尚未交付的报告；缓存和旧快照不重复消费。</summary>
public sealed class RuntimeDiagnosticCursor
{
    private readonly Dictionary<Guid, long> _consumed = [];
    /// <summary>实际交付给消费者的发生次数，不按消息去重。</summary>
    public long ConsumedCount { get; private set; }
    /// <summary>仅枚举每个日志代次的新后缀；首个远时刻快照仍交付全部已发生记录。</summary>
    public IEnumerable<RuntimeDiagnosticOccurrence> ReadNew(RuntimeDiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var run in snapshot.Runs)
        {
            _consumed.TryGetValue(run.Epoch, out long consumed);
            foreach (var occurrence in RuntimeDiagnosticSnapshot.ReadRun(run, consumed))
            {
                _consumed[run.Epoch] = occurrence.Sequence; ConsumedCount++;
                yield return occurrence;
            }
        }
    }
}
