using System.Collections.Concurrent;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class ProjectSaveCoordinatorTests
{
    private static ProjectSaveSnapshot Snapshot(string name, long generation = 1, string? directory = null)
        => new(ProjectWorkspace.CreateDefaultProject(name), directory, generation, true, DateTimeOffset.UtcNow);

    [Fact]
    public async Task 执行中只保留最新备份且完成引用来自实际输入()
    {
        using var storage = new ControlledStorage { HoldBackup = true };
        var service = new ProjectSaveCoordinator(storage); var first = Snapshot("A"); var latest = Snapshot("C");
        service.RequestAutoSave(first); await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        service.RequestAutoSave(Snapshot("B")); service.RequestAutoSave(latest);
        Assert.Single(storage.Calls); storage.Release.Set(); await service.DrainAsync();
        Assert.Equal(["A", "C"], storage.Calls.Select(c => c.Snapshot.Project.Name));
        Assert.Equal(1, storage.MaximumWriters); Assert.False(service.RequestAutoSave(latest));
        Assert.Equal("C", storage.Marker!.ProjectName);
    }

    [Fact]
    public async Task 手工保存期间编辑保持脏且换工程拒绝旧保存引用()
    {
        using var storage = new ControlledStorage { HoldManual = true };
        var service = new ProjectSaveCoordinator(storage); var workspace = ProjectWorkspace.CreateNew("A");
        workspace.Replace(workspace.Project with { Name = "要写出的 A" }); var snapshot = workspace.CaptureSave(DateTimeOffset.UtcNow);
        Task<ProjectSaveResult> writing = service.SaveAsync(snapshot, Path.GetFullPath("saved-A"));
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        workspace.Replace(workspace.Project with { Name = "写入期间编辑的 B" });
        storage.Release.Set(); var result = await writing; await service.DrainAsync();
        Assert.True(result.Succeeded); Assert.Same(snapshot.Project, Assert.Single(storage.Calls).Snapshot.Project);
        Assert.True(workspace.AcceptSave(result.Snapshot, result.DirectoryPath!, result.Issues)); Assert.True(workspace.IsDirty);
        Assert.Equal("写入期间编辑的 B", workspace.Project.Name);
        workspace.StartNew(ProjectWorkspace.CreateDefaultProject("另一个工程"));
        Assert.False(workspace.AcceptSave(snapshot, result.DirectoryPath!, result.Issues)); Assert.Null(workspace.DirectoryPath);
    }

    [Fact]
    public async Task 手工请求在当前写入后优先执行且后续备份使用实际另存为路径()
    {
        using var storage = new ControlledStorage { HoldBackup = true };
        var service = new ProjectSaveCoordinator(storage); service.RequestAutoSave(Snapshot("A"));
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); service.RequestAutoSave(Snapshot("被合并的 B"));
        string target = Path.GetFullPath("saved-C"); Task<ProjectSaveResult> manual = service.SaveAsync(Snapshot("C"), target);
        service.RequestAutoSave(Snapshot("D", directory: Path.GetFullPath("old")));
        storage.Release.Set(); await manual; await service.DrainAsync(); var calls = storage.Calls.ToArray();
        Assert.Equal(["A", "C", "D"], calls.Select(c => c.Snapshot.Project.Name));
        Assert.True(calls[1].Manual); Assert.Equal(target, calls[2].Snapshot.DirectoryPath); Assert.Equal(1, storage.MaximumWriters);
    }

    [Fact]
    public async Task 放弃先失效并取消待办等待旧事务后不发布旧标记()
    {
        using var storage = new ControlledStorage { HoldBackup = true }; var service = new ProjectSaveCoordinator(storage);
        ProjectSaveResult? finished = null; service.Completed += result => finished = result;
        service.RequestAutoSave(Snapshot("旧工程")); await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        service.RequestAutoSave(Snapshot("不应写出")); Task stopping = service.StopAsync(clearRecovery: true);
        Assert.False(stopping.IsCompleted); Assert.False(service.RequestAutoSave(Snapshot("已失效")));
        storage.Release.Set(); await stopping; Assert.Single(storage.Calls); Assert.Null(storage.Marker);
        Assert.False(service.IsCurrent(Assert.IsType<ProjectSaveResult>(finished)));
        var oldResult = finished; service.Resume(); Assert.False(service.IsCurrent(oldResult!));
        service.RequestAutoSave(Snapshot("新工程", generation: 2)); await service.DrainAsync();
        Assert.Equal("新工程", storage.Marker!.ProjectName); Assert.True(service.IsCurrent(finished!));
    }

    [Fact]
    public async Task 恢复标记发布已经开始时放弃仍等待并清理它()
    {
        using var storage = new ControlledStorage { HoldMarker = true }; var service = new ProjectSaveCoordinator(storage);
        service.RequestAutoSave(Snapshot("旧现场")); await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(storage.Marker); var stopping = service.StopAsync(clearRecovery: true);
        await Task.Delay(30); Assert.False(stopping.IsCompleted); storage.Release.Set(); await stopping; Assert.Null(storage.Marker);
        Assert.Single(storage.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 数据或标记失败都报告且同一快照能够重试(bool markerFailure)
    {
        using var storage = new ControlledStorage { FailWrite = !markerFailure, FailMarker = markerFailure };
        var service = new ProjectSaveCoordinator(storage); var notices = new ConcurrentQueue<ProjectSaveResult>(); service.Completed += notices.Enqueue;
        var snapshot = Snapshot("重试资格"); service.RequestAutoSave(snapshot); await service.DrainAsync();
        Assert.False(Assert.Single(notices).Succeeded); Assert.Null(storage.Marker);
        Assert.True(service.RequestAutoSave(snapshot)); await service.DrainAsync();
        Assert.Equal(2, notices.Count); Assert.True(notices.Last().Succeeded); Assert.False(service.RequestAutoSave(snapshot));
        Assert.Equal("重试资格", storage.Marker!.ProjectName);
    }

    [Fact]
    public async Task 根入口已经发布后恢复标记清理失败不谎报工程写入失败()
    {
        using var storage = new ControlledStorage(); var service = new ProjectSaveCoordinator(storage);
        service.RequestAutoSave(Snapshot("旧现场")); await service.DrainAsync(); storage.FailClear = true;
        var result = await service.SaveAsync(Snapshot("已发布工程"), Path.GetFullPath("committed"));
        Assert.True(result.Succeeded); Assert.Null(result.Error); Assert.IsType<IOException>(result.RecoveryError);
        Assert.NotNull(storage.Marker); await service.StopAsync(clearRecovery: true); Assert.Null(storage.Marker);
        Assert.Equal(2, storage.Calls.Count);
    }

    [Fact]
    public async Task 手工根入口已经写出的同一实例不因界面完成尚在排队而再次备份()
    {
        using var storage = new ControlledStorage(); var service = new ProjectSaveCoordinator(storage);
        var snapshot = Snapshot("实际已保存的快照"); await service.SaveAsync(snapshot, Path.GetFullPath("written"));
        Assert.False(service.RequestAutoSave(snapshot)); await service.DrainAsync(); Assert.Single(storage.Calls);
        Assert.True(service.RequestAutoSave(snapshot with { Project = snapshot.Project with { Name = "真正的新编辑" } }));
        await service.DrainAsync(); Assert.Equal(2, storage.Calls.Count);
    }

    [Fact]
    public async Task 手工保存后撤销到此前备份实例仍须新建恢复现场()
    {
        using var storage = new ControlledStorage(); var service = new ProjectSaveCoordinator(storage);
        var previous = Snapshot("先前的编辑"); service.RequestAutoSave(previous); await service.DrainAsync();
        await service.SaveAsync(Snapshot("手工定稿"), Path.GetFullPath("saved"));
        Assert.True(service.RequestAutoSave(previous)); await service.DrainAsync();
        Assert.Equal(3, storage.Calls.Count); Assert.Equal("先前的编辑", storage.Marker!.ProjectName);
    }

    [Fact]
    public async Task 手工失败保留工作区保存引用且同一捕获可重试()
    {
        using var storage = new ControlledStorage { FailWrite = true }; var service = new ProjectSaveCoordinator(storage);
        var workspace = ProjectWorkspace.CreateNew("起点"); workspace.Replace(workspace.Project with { Name = "脏" });
        var snapshot = workspace.CaptureSave(DateTimeOffset.UtcNow);
        var failed = await service.SaveAsync(snapshot, Path.GetFullPath("retry")); Assert.False(failed.Succeeded);
        Assert.True(workspace.IsDirty); Assert.Null(workspace.DirectoryPath);
        var retried = await service.SaveAsync(snapshot, Path.GetFullPath("retry")); Assert.True(retried.Succeeded);
        workspace.AcceptSave(snapshot, retried.DirectoryPath!, retried.Issues); Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task 放弃不清理被其他现场替换的标记且待办手工任务明确取消()
    {
        using var storage = new ControlledStorage(); var service = new ProjectSaveCoordinator(storage);
        service.RequestAutoSave(Snapshot("自己的")); await service.DrainAsync();
        var foreign = new AutoSaveSession("foreign", null, "另一现场", DateTimeOffset.UtcNow); storage.Marker = foreign;
        await service.StopAsync(clearRecovery: true); Assert.Same(foreign, storage.Marker);
        service.Resume(); storage.HoldBackup = true; service.RequestAutoSave(Snapshot("当前事务"));
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task<ProjectSaveResult> pending = service.SaveAsync(Snapshot("待办手工"), Path.GetFullPath("pending"));
        var stopping = service.StopAsync(clearRecovery: true); Assert.IsType<OperationCanceledException>((await pending).Error);
        storage.Release.Set(); await stopping; Assert.DoesNotContain(storage.Calls, c => c.Manual);
    }

    [Fact]
    public async Task 观察者失败不改变已提交结果且空闲交接不丢最后请求()
    {
        using var storage = new ControlledStorage(); var service = new ProjectSaveCoordinator(storage);
        service.Completed += _ => throw new InvalidOperationException("观察者故障");
        for (int index = 0; index < 100; index++)
        {
            var snapshot = Snapshot("请求" + index); Assert.True(service.RequestAutoSave(snapshot));
            await service.DrainAsync(); Assert.Equal(snapshot.Project.Name, storage.Marker!.ProjectName);
        }
        Assert.Equal(100, storage.Calls.Count); Assert.Equal(1, storage.MaximumWriters);
    }

    private sealed class ControlledStorage : IProjectSaveStorage, IDisposable
    {
        internal bool HoldBackup, HoldManual, HoldMarker, FailWrite, FailMarker, FailClear;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        internal readonly ConcurrentQueue<(ProjectSaveSnapshot Snapshot, bool Manual)> Calls = new();
        internal AutoSaveSession? Marker;
        internal int MaximumWriters;
        private int _writers;
        private void Write(ProjectSaveSnapshot snapshot, bool manual)
        {
            int active = Interlocked.Increment(ref _writers); MaximumWriters = Math.Max(MaximumWriters, active);
            try
            {
                Calls.Enqueue((snapshot, manual));
                if (manual ? HoldManual : HoldBackup)
                { Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("受控写入未释放。"); }
                if (FailWrite) { FailWrite = false; throw new IOException("写入失败"); }
            }
            finally { Interlocked.Decrement(ref _writers); }
        }
        public string WriteBackup(ProjectSaveSnapshot snapshot) { Write(snapshot, false); return "backup-" + Calls.Count; }
        public IReadOnlyList<ProjectIssue> WriteProject(ProjectSaveSnapshot snapshot, string directoryPath) { Write(snapshot, true); return []; }
        public void PublishRecovery(AutoSaveSession session)
        {
            if (FailMarker) { FailMarker = false; throw new IOException("标记失败"); }
            Marker = session;
            if (HoldMarker) { Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("受控标记未释放。"); }
        }
        public void ClearRecovery(AutoSaveSession expected)
        { if (FailClear) { FailClear = false; throw new IOException("标记清理失败"); } if (Marker == expected) Marker = null; }
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }
}
