using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private ProjectWriteLease? _profileLease;
    private readonly SemaphoreSlim _pinGate = new(1, 1);
    private readonly Dictionary<string, long> _pinnedReservations = new(StringComparer.Ordinal);

    private void RemoveStalePinnedInputs()
    {
        string sessions = Path.Combine(_persistence!.StateRoot, "sessions");
        if (!Directory.Exists(sessions)) return;
        foreach (string session in Directory.EnumerateDirectories(sessions))
        {
            string name = Path.GetFileName(session);
            if (!name.StartsWith("project-", StringComparison.Ordinal) || !Guid.TryParseExact(name.AsSpan(8), "N", out _)) continue;
            string input = Path.Combine(session, "loaded-input");
            if (!Directory.Exists(input)) continue;
            ProjectDirectoryHandles.EnsureNoLinks(session);
            Directory.Delete(input, recursive: true);
        }
    }

    private async Task<Project> PinInputAsync(Project project, string sourceDirectory, string target)
    {
        string stateRelative = Path.GetRelativePath(sourceDirectory, _persistence!.StateRoot);
        if (stateRelative != ".." && !stateRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(stateRelative))
            throw new ResourceStoreException(BackendErrorCode.PathNotAllowed, "工程目录不能包含后端profile。");
        // Read inputs are a closed package. No persisted field may turn a directory grant into arbitrary file access.
        foreach (string path in new[] { project.AudioPath, project.Background.Path }.OfType<string>())
        {
            string relative = Path.GetRelativePath(sourceDirectory, path);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new ResourceStoreException(BackendErrorCode.PathNotAllowed, "工程媒体不属于已授权闭包。");
        }
        long sourceBytes = DirectoryBytes(sourceDirectory);
        long reserve = checked(sourceBytes + 64 * 1024);
        await _pinGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pinnedReservations.Count >= _persistence.MaxPinnedClosures
                || reserve > _persistence.MaxPinnedClosureBytes - _pinnedReservations.Values.Sum())
                throw new ResourceStoreException(BackendErrorCode.ResourceQuotaExceeded, "固定工程闭包达到数量或字节预算。");
            _pinnedReservations.Add(target, reserve);
            try
            {
                await Task.Run(() => ProjectStore.Save(project, target, limits: new()
                { MaximumMediaBytes = _persistence.MaxPinnedClosureBytes,
                    MaximumAnalysisBytes = (int)Math.Min(_persistence.MaxPinnedClosureBytes, 256 * 1024 * 1024L) })).ConfigureAwait(false);
                long actual = DirectoryBytes(target);
                if (actual > reserve) throw new ResourceStoreException(BackendErrorCode.ResourceQuotaExceeded, "固定闭包超过预留预算。");
                _pinnedReservations[target] = actual;
                return ProjectStore.Load(target).Project ?? throw new IOException("固定工程闭包无法重新读取。");
            }
            catch { _pinnedReservations.Remove(target); throw; }
        }
        finally { _pinGate.Release(); }
    }

    private async Task ReleasePinnedInputAsync(string? directory)
    {
        if (directory is null) return;
        await _pinGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            _pinnedReservations.Remove(directory);
        }
        finally { _pinGate.Release(); }
    }

    private static long DirectoryBytes(string directory)
    {
        long bytes = 0; int count = 0;
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (++count > 100_000) throw new IOException("工程闭包文件数量超过预算检查上限。");
            bytes = checked(bytes + new FileInfo(path).Length);
        }
        return bytes;
    }
}
