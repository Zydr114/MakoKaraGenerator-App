using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Application.Tests;

public sealed class ProjectWriteLeaseTests
{
    [Fact]
    public void 同一物理目录拒绝重复写者且失败打开保留旧路径租约()
    {
        using TemporaryDirectory directory = new();
        string first = directory.FilePath("first.mkgproj");
        ProjectWorkspace workspace = ProjectWorkspace.CreateNew("A"); workspace.EnableWriteLeaseOwnership();
        try
        {
            workspace.SaveAs(first);
            Assert.Throws<ProjectWriteLeaseConflictException>(() => ProjectWriteLease.Acquire(first));
            Assert.Throws<ProjectWriteLeaseConflictException>(() => ProjectWriteLease.Acquire(Path.Combine(first, "..", "first.mkgproj")));
            Assert.False(workspace.Open(directory.FilePath("missing.mkgproj")).Loaded);
            Assert.Equal(first, workspace.DirectoryPath);
            Assert.Throws<ProjectWriteLeaseConflictException>(() => ProjectWriteLease.Acquire(first));
            workspace.Recover(workspace.Project, first);
            Assert.True(workspace.IsDirty); Assert.Equal(first, workspace.DirectoryPath);
        }
        finally { workspace.ReleaseWriteLease(); }
        using ProjectWriteLease lease = ProjectWriteLease.Acquire(first);
    }

    [Fact]
    public async Task 换工程后已接受自动备份继续持有旧目录直到真实IO完成()
    {
        using TemporaryDirectory directory = new();
        string path = directory.FilePath("old.mkgproj");
        ProjectWorkspace workspace = ProjectWorkspace.CreateNew("旧工程"); workspace.EnableWriteLeaseOwnership(); workspace.SaveAs(path);
        using BlockingBackup storage = new(directory.FilePath("state"));
        ProjectSaveCoordinator saves = new(storage);
        workspace.Replace(workspace.Project with { Name = "脏旧工程" });
        Assert.True(saves.RequestAutoSave(workspace.CaptureSave(DateTimeOffset.UtcNow), workspace.PrepareSaveLease(path)));
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        workspace.StartNew(ProjectWorkspace.CreateDefaultProject("新工程"));
        Assert.Throws<ProjectWriteLeaseConflictException>(() => ProjectWriteLease.Acquire(path));
        storage.Release.Set(); await saves.DrainAsync();
        using ProjectWriteLease lease = ProjectWriteLease.Acquire(path);
        Assert.Equal("脏旧工程", ProjectStore.Load(storage.Backup!).Project!.Name);
        await saves.StopAsync(clearRecovery: true);
    }

    [Fact]
    public void 链接别名共享物理写租约()
    {
        if (OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new();
        string target = directory.FilePath("target"); Directory.CreateDirectory(target);
        string alias = directory.FilePath("alias"); Directory.CreateSymbolicLink(alias, target);
        using ProjectWriteLease lease = ProjectWriteLease.Acquire(Path.Combine(target, "song.mkgproj"));
        Assert.Throws<ProjectWriteLeaseConflictException>(() => ProjectWriteLease.Acquire(Path.Combine(alias, "song.mkgproj")));
    }

    [Fact]
    public async Task Unix原生异进程锁冲突及崩溃释放不依赖不同TMPDIR()
    {
        if (OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new(); string path = directory.FilePath("song.mkgproj");
        string lockFile;
        using (ProjectWriteLease lease = ProjectWriteLease.Acquire(path))
        {
            lockFile = Path.Combine("/tmp", "MakoKaraGenerator-write-leases-" + Environment.UserName,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lease.DirectoryPath))) + ".lock");
            using Process competing = StartPython("import fcntl,sys\nf=open(sys.argv[1],'a+b')\ntry:\n fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)\n print('unexpected')\nexcept BlockingIOError:\n print('conflict')", lockFile, directory.Path);
            Assert.Equal("conflict", await competing.StandardOutput.ReadLineAsync()); await competing.WaitForExitAsync(); Assert.Equal(0, competing.ExitCode);
        }
        using Process owner = StartPython("import fcntl,sys,time\nf=open(sys.argv[1],'a+b')\nfcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)\nprint('locked',flush=True)\ntime.sleep(60)", lockFile, directory.Path);
        Assert.Equal("locked", await owner.StandardOutput.ReadLineAsync());
        Assert.Throws<ProjectWriteLeaseConflictException>(() => ProjectWriteLease.Acquire(path));
        owner.Kill(); await owner.WaitForExitAsync();
        using ProjectWriteLease afterCrash = ProjectWriteLease.Acquire(path);
    }

    private static Process StartPython(string script, string file, string temporary)
    {
        ProcessStartInfo start = new("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add(script); start.ArgumentList.Add(file);
        start.Environment["TMPDIR"] = temporary;
        return Process.Start(start)!;
    }

    private sealed class BlockingBackup(string root) : IProjectSaveStorage, IDisposable
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        internal string? Backup;
        public string WriteBackup(ProjectSaveSnapshot snapshot)
        { Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("备份未释放。"); return Backup = AutoSaveStore.Save(snapshot.Project, AutoSaveStore.ResolveRoot(snapshot.DirectoryPath, root), snapshot.CapturedAtUtc); }
        public IReadOnlyList<ProjectIssue> WriteProject(ProjectSaveSnapshot snapshot, string directoryPath) => throw new NotSupportedException();
        public void PublishRecovery(AutoSaveSession session) => AutoSaveSession.Write(root, session);
        public void ClearRecovery(AutoSaveSession expected) { if (AutoSaveSession.Read(root) == expected) AutoSaveSession.Clear(root); }
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }
}
