using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>单串行所有者的追加日志；已发布快照只引用不可变分块和有限尾部副本。</summary>
public sealed class RuntimeDiagnosticBuffer
{
    private const int ChunkCapacity = 32;
    private readonly RuntimeDiagnosticSnapshot _prefix;
    private readonly Guid _epoch = Guid.NewGuid();
    private RuntimeDiagnosticSnapshot.Chunk? _sealed;
    private RuntimeDiagnostic[]? _pending;
    private int _pendingCount, _count;
    private bool _hasError;
    private RuntimeDiagnosticSnapshot? _snapshot;
    /// <summary>建立新日志或共享现有前缀的独立本帧日志，不复制历史。</summary>
    public RuntimeDiagnosticBuffer(RuntimeDiagnosticSnapshot? prefix = null) { _prefix = prefix ?? RuntimeDiagnosticSnapshot.Empty; }
    /// <summary>追加完整报告；非法记录立即拒绝，Error仍由调用阶段保留失败门禁。</summary>
    public void Add(RuntimeDiagnostic diagnostic)
    {
        if (diagnostic?.Diagnostic is null) throw new ArgumentException("诊断记录和作者报告必须有效。", nameof(diagnostic));
        int next = checked(_count + 1);
        _pending ??= new RuntimeDiagnostic[ChunkCapacity];
        _pending[_pendingCount++] = diagnostic; _count = next;
        _hasError |= diagnostic.Diagnostic.Severity == DiagnosticSeverity.Error; _snapshot = null;
        if (_pendingCount == ChunkCapacity)
        {
            _sealed = new(_sealed, ImmutableArray.Create(_pending), _count - ChunkCapacity);
            _pending = null; _pendingCount = 0;
        }
    }
    /// <summary>取得稳定快照；未追加时重用，最多复制当前32条尾部，不复制完整前缀。</summary>
    public RuntimeDiagnosticSnapshot Snapshot()
    {
        if (_snapshot is not null) return _snapshot;
        if (_count == 0) return _prefix;
        var head = _pendingCount == 0 ? _sealed! : new RuntimeDiagnosticSnapshot.Chunk(_sealed,
            ImmutableArray.Create(_pending!.AsSpan(0, _pendingCount)), _count - _pendingCount);
        return _snapshot = new(_prefix.Runs.Add(new(_epoch, head, _count)), checked(_prefix.Count + _count), _prefix.HasError || _hasError);
    }
}
