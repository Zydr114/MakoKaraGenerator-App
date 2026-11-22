using System.Diagnostics;
using System.Globalization;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Processes;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerExportLifetimeTests
{
    [Fact]
    public async Task 真实导出Worker先退出仍须排水其已停止的真实编码器()
    {
        if (!LinuxProcessGuards.IsSupported) return;
        string root = FindRoot();
        string workerPath = WorkerBinaryLocator.LocateBackendWorker();
        string output = Path.Combine(root, "artifacts/backend-b4b-processes/worker-child-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        WorkerBackendExportExecutor executor = new(workerPath, TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe, TimeSpan.FromSeconds(20),
            new BackendExportOptions { MaxMediaBytes = WorkerExportContract.MaximumMediaBytes });
        Project project = Project.Create("real owned Worker descendant",
            Composition.Create(1920, 1080, new FrameRate(60, 1), outputDurationUs: 10_000_000),
            LyricsDocument.Create([LyricLine.Create("line", "Real Worker lifetime", new(0, 10_000_000), TimingStatus.Edited,
                [LyricToken.Create("token", "Real Worker lifetime", new(0, 10_000_000), TimingStatus.Edited)])]));
        OwnedProcessGroup? group = null;
        ProcessIdentity? encoder = null;
        Task<ExportReportDto> execution = executor.ExecuteAsync(ProjectSnapshot.FromProject(project),
            new("opaqueMp4", false, Path.Combine(output, "candidate.mp4"), null));
        try
        {
            Stopwatch wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                foreach (Process child in Process.GetProcesses())
                {
                    using (child)
                    {
                        try
                        {
                            string stat = File.ReadAllText("/proc/" + child.Id.ToString(CultureInfo.InvariantCulture) + "/stat");
                            string[] parts = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (int.Parse(parts[1], CultureInfo.InvariantCulture) != Environment.ProcessId) continue;
                            string command = File.ReadAllText("/proc/" + child.Id.ToString(CultureInfo.InvariantCulture) + "/cmdline");
                            if (!command.Contains("MakoKaraGenerator.Backend.Worker", StringComparison.Ordinal)) continue;
                            group = OwnedProcessGroup.TryCapture(child);
                            if (group is null) continue;
                            foreach (ProcessIdentity member in group.ObserveMembers())
                            {
                                if (member.ProcessId == child.Id) continue;
                                string args = File.ReadAllText("/proc/" + member.ProcessId.ToString(CultureInfo.InvariantCulture) + "/cmdline");
                                if (!args.Contains("rawvideo", StringComparison.Ordinal)) continue;
                                encoder = member; break;
                            }
                        }
                        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException) { }
                    }
                    if (encoder is not null) break;
                }
                if (encoder is not null) break;
                if (execution.IsCompleted)
                {
                    try { await execution; }
                    catch (Exception earlyFailure) { throw new InvalidOperationException("Real export failed before any controlled lifetime action.", earlyFailure); }
                    Assert.Fail("A real bounded export completed before controlled root termination.");
                }
                await Task.Delay(1);
            }
            Assert.NotNull(group); Assert.NotNull(encoder);
            await SignalAsync(encoder.ProcessId, "-STOP");
            Assert.Contains(group.ObserveMembers(), value => value == encoder);
            await SignalAsync(group.Leader.ProcessId, "-KILL");
            Exception? failure = await Record.ExceptionAsync(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(failure is IOException or RenderSessionFailureException, "Actual Worker termination must surface a failed execution.");
            IReadOnlyList<ProcessIdentity> retained = group.ObserveMembers();
            await File.WriteAllTextAsync(Path.Combine(output, "owned-identities.txt"),
                $"leader={group.Leader}\nencoder={encoder}\nretained={string.Join(',', retained)}\n");
            Assert.DoesNotContain(retained, value => value == encoder);
        }
        finally
        {
            if (group is not null) await group.KillAndDrainAsync(TimeSpan.FromSeconds(3));
            try { await execution.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        }
    }

    private static async Task SignalAsync(int pid, string signal)
    {
        ProcessStartInfo start = new("/bin/kill") { UseShellExecute = false };
        start.ArgumentList.Add(signal); start.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        using Process process = Process.Start(start) ?? throw new IOException("Owned signal helper failed.");
        await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
    }
    private static string FindRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "MakoKaraGenerator.Backend.Worker"))) root = root.Parent;
        return root?.FullName ?? throw new IOException("Repository root not found.");
    }
}
