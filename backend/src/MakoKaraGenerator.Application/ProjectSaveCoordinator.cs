using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using System.Diagnostics;

namespace MakoKaraGenerator.Application;

/// <summary>
/// 每工作区一个写入者、一份待办的最新备份和一个手工保存边界。
/// 失效先于等待；已有完整资源事务到安全点后才清除本协调器的恢复标记。
/// </summary>
public sealed class ProjectSaveCoordinator
{
    private readonly object _gate = new();
    private readonly IProjectSaveStorage _storage;
    private ProjectSaveSnapshot? _pendingAuto;
    private IDisposable? _pendingAutoLease;
    private Job? _pendingManual;
    private Project? _lastBackedUp;
    private long _lastGeneration;
    private long _epoch;
    private bool _accepting = true, _manualOutstanding, _running;
    private string? _writtenDirectory;
    private Project? _writtenProject;
    private long _writtenGeneration;
    private ProjectWriteLeaseReservation? _writtenSaveLease;
    private Task _worker = Task.CompletedTask;
    private AutoSaveSession? _ownedMarker;
    private Task? _stopTask;

    /// <summary>使用正式完整闭包存储。</summary>
    public ProjectSaveCoordinator(string stateRoot) : this(new ProjectSaveStorage(Path.GetFullPath(stateRoot)))
    { ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot); }
    /// <summary>注入存储边界；不把工作区交给后台存储。</summary>
    public ProjectSaveCoordinator(IProjectSaveStorage storage) => _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    /// <summary>后台完成通知。界面须投递到自己的 dispatcher，并检查代次。</summary>
    public event Action<ProjectSaveResult>? Completed;
    /// <summary>结果是否仍属于当前接受写入的会话。</summary>
    public bool IsCurrent(ProjectSaveResult result)
    { lock (_gate) return _accepting && result.Epoch == _epoch; }

    /// <summary>无 IO 地替换待办备份；未改动或已备份的同一实例不再写入。</summary>
    public bool RequestAutoSave(ProjectSaveSnapshot snapshot, IDisposable? writeLease = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (!_accepting || !snapshot.IsDirty || _lastGeneration == snapshot.Generation && ReferenceEquals(_lastBackedUp, snapshot.Project)
                || _writtenGeneration == snapshot.Generation && ReferenceEquals(_writtenProject, snapshot.Project))
            { writeLease?.Dispose(); return false; }
            _pendingAutoLease?.Dispose();
            _pendingAuto = Rebase(snapshot);
            if (_pendingAuto.DirectoryPath != snapshot.DirectoryPath && _writtenSaveLease is { } actual)
            { writeLease?.Dispose(); writeLease = actual.RetainReference(); }
            _pendingAutoLease = writeLease;
            StartWorker();
            return true;
        }
    }

    /// <summary>手工保存优先于尚未开始的旧备份。重复手工请求由调用方命令门禁拦截。</summary>
    public Task<ProjectSaveResult> SaveAsync(ProjectSaveSnapshot snapshot, string directoryPath,
        ProjectWriteLeaseReservation? saveLease = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        string target = Path.GetFullPath(directoryPath);
        lock (_gate)
        {
            if (!_accepting) throw new InvalidOperationException("保存会话正在离开。");
            if (_manualOutstanding) throw new InvalidOperationException("已有手工保存请求。");
            var completion = new TaskCompletionSource<ProjectSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _epoch++; // A queued old backup notification cannot overwrite the manual save result.
            _pendingAuto = null;
            _pendingAutoLease?.Dispose(); _pendingAutoLease = null;
            _pendingManual = new(snapshot, _epoch, target, completion, SaveLease: saveLease);
            _manualOutstanding = true;
            StartWorker();
            return completion.Task;
        }
    }

    /// <summary>异步等待已接受的写入及最新待办，不占用调用线程。</summary>
    public Task DrainAsync() { lock (_gate) return _worker; }

    /// <summary>同步使旧结果失效，取消待办，再异步等执行中的完整事务结束并清理自有标记。</summary>
    public Task StopAsync(bool clearRecovery)
    {
        lock (_gate)
        {
            if (_stopTask is { IsCompleted: false }) return _stopTask;
            _accepting = false; _epoch++; _pendingAuto = null;
            _pendingAutoLease?.Dispose(); _pendingAutoLease = null;
            if (_pendingManual is { } pending)
            {
                _pendingManual = null; _manualOutstanding = false;
                pending.Completion!.TrySetResult(new(pending.Snapshot, pending.Epoch, true, pending.Target, [], new OperationCanceledException("保存会话已失效。")));
            }
            _stopTask = StopCoreAsync(_worker, clearRecovery);
            return _stopTask;
        }
    }

    private async Task StopCoreAsync(Task worker, bool clearRecovery)
    {
        // Always yield: the caller holds the short state lock, which must never enclose storage IO.
        await Task.Yield();
        await worker.ConfigureAwait(false);
        if (clearRecovery && _ownedMarker is { } marker)
        {
            await Task.Run(() => _storage.ClearRecovery(marker)).ConfigureAwait(false);
            _ownedMarker = null;
        }
    }

    /// <summary>离开操作取消或已进入下一工程后，重新接受当前工作区捕获的输入。</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_stopTask is { IsCompleted: false }) throw new InvalidOperationException("旧保存尚未到达安全点。");
            if (_accepting) return;
            _accepting = true; _lastBackedUp = null; _lastGeneration = 0;
        }
    }

    private void StartWorker()
    {
        if (!_running) { _running = true; _worker = Task.Run(Pump); }
    }

    private ProjectSaveSnapshot Rebase(ProjectSaveSnapshot snapshot) => snapshot.Generation == _writtenGeneration
        ? snapshot with { DirectoryPath = _writtenDirectory } : snapshot;

    private void Pump()
    {
        while (true)
        {
            Job job;
            lock (_gate)
            {
                if (_pendingManual is { } manual) { job = manual; _pendingManual = null; }
                else if (_pendingAuto is { } automatic)
                {
                    _pendingAuto = null;
                    IDisposable? lease = _pendingAutoLease; _pendingAutoLease = null;
                    if (_lastGeneration == automatic.Generation && ReferenceEquals(_lastBackedUp, automatic.Project)) { lease?.Dispose(); continue; }
                    job = new(Rebase(automatic), _epoch, null, null, lease);
                }
                else { _running = false; return; }
            }
            string? output = job.Target; IReadOnlyList<ProjectIssue> issues = []; Exception? failure = null, recoveryFailure = null;
            try
            {
                if (job.Target is { } target)
                {
                    issues = _storage.WriteProject(job.Snapshot, target);
                    lock (_gate)
                    {
                        _writtenDirectory = target; _writtenGeneration = job.Snapshot.Generation; _writtenProject = job.Snapshot.Project;
                        _writtenSaveLease = job.SaveLease;
                        if (_pendingAuto is { } relocating && relocating.Generation == job.Snapshot.Generation
                            && relocating.DirectoryPath != target && job.SaveLease is { } candidate)
                        { _pendingAutoLease?.Dispose(); _pendingAutoLease = candidate.RetainReference(); }
                        _lastBackedUp = null; _lastGeneration = 0;
                        if (_pendingAuto is { } pending && pending.Generation == job.Snapshot.Generation && ReferenceEquals(pending.Project, job.Snapshot.Project))
                        { _pendingAuto = null; _pendingAutoLease?.Dispose(); _pendingAutoLease = null; }
                    }
                    if (_ownedMarker is { } marker)
                    {
                        try { _storage.ClearRecovery(marker); _ownedMarker = null; }
                        catch (Exception error) { recoveryFailure = error; } // Project root is already committed.
                    }
                }
                else
                {
                    output = _storage.WriteBackup(job.Snapshot);
                    bool publish; lock (_gate) publish = _accepting && job.Epoch == _epoch;
                    if (publish)
                    {
                        var marker = new AutoSaveSession(output, job.Snapshot.DirectoryPath, job.Snapshot.Project.Name, job.Snapshot.CapturedAtUtc);
                        _ownedMarker = marker; _storage.PublishRecovery(marker);
                        lock (_gate)
                        {
                            if (_accepting && job.Epoch == _epoch) { _lastBackedUp = job.Snapshot.Project; _lastGeneration = job.Snapshot.Generation; }
                        }
                    }
                    else failure = new OperationCanceledException("旧备份已完成，恢复标记因会话失效未发布。");
                }
            }
            // Every storage failure completes its request. A faulted worker must not strand a manual Task forever.
            catch (Exception exception)
            { failure = exception; }
            finally { job.WriteLease?.Dispose(); }
            var result = new ProjectSaveResult(job.Snapshot, job.Epoch, job.Target is not null, output, issues, failure, recoveryFailure);
            lock (_gate) { if (job.Target is not null) _manualOutstanding = false; }
            job.Completion?.TrySetResult(result);
            if (Completed is { } listeners)
                foreach (Action<ProjectSaveResult> listener in listeners.GetInvocationList().Cast<Action<ProjectSaveResult>>())
                {
                    try { listener(result); }
                    catch (Exception error) { Trace.TraceError("Project save notification failed: {0}", error); }
                }
        }
    }

    private sealed record Job(ProjectSaveSnapshot Snapshot, long Epoch, string? Target,
        TaskCompletionSource<ProjectSaveResult>? Completion, IDisposable? WriteLease = null, ProjectWriteLeaseReservation? SaveLease = null);
}
