using System.Diagnostics;
using System.Globalization;
using MakoKaraGenerator.Processes;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerControlDrainTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 子收割启用后旧无私有组Worker与满输入管道仍须精确排水且不偷取无关退出状态(bool stopReadingAfterHello)
    {
        if (!LinuxProcessGuards.IsSupported) return;
        Assert.True(LinuxProcessGuards.TryEnableChildReaping());
        using TemporaryDirectory directory = new();
        string script = directory.FilePath("owned-worker.py");
        string childFile = directory.FilePath("owned-child");
        await File.WriteAllTextAsync(script, Source);
        using Process unrelated = Process.Start(new ProcessStartInfo("python3")
        { ArgumentList = { "-c", "import time,sys; time.sleep(.5); sys.exit(7)" } })!;
        WorkerProcessProxy proxy = new(new("python3", [script, childFile, stopReadingAfterHello ? "blocked" : "legacy"],
            TimeSpan.FromMilliseconds(80)));
        OwnedProcessTree? cleanupTree = null;
        Task? call = null;
        try
        {
            await proxy.StartAsync();
            int pid = Assert.IsType<int>(proxy.ProcessId);
            using (Process root = Process.GetProcessById(pid)) cleanupTree = OwnedProcessTree.TryCapture(root);
            Assert.NotNull(cleanupTree);
            // The fixture creates its child before hello, so an original broken
            // implementation can also be safely cleaned after the bounded assertion.
            cleanupTree.ObserveDescendants();
            int child = int.Parse(await File.ReadAllTextAsync(childFile), CultureInfo.InvariantCulture);
            using CancellationTokenSource deadline = new(TimeSpan.FromMilliseconds(300));
            call = proxy.InvokeAsync(new("probe", stopReadingAfterHello ? new byte[512 * 1024] : []), deadline.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Null(proxy.ProcessId);
            Assert.False(Directory.Exists("/proc/" + pid.ToString(CultureInfo.InvariantCulture)));
            Assert.False(Directory.Exists("/proc/" + child.ToString(CultureInfo.InvariantCulture)));
            await unrelated.WaitForExitAsync();
            Assert.Equal(7, unrelated.ExitCode);
        }
        finally
        {
            if (cleanupTree is not null) await cleanupTree.KillAndDrainAsync(TimeSpan.FromSeconds(3));
            if (call is not null) { try { await call.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { } }
            await proxy.DisposeAsync();
        }
    }

    private const string Source = """
        import json,struct,subprocess,sys,time
        child_file,mode=sys.argv[1:]
        prefix=sys.stdin.buffer.read(4)
        hello=json.loads(sys.stdin.buffer.read(struct.unpack('<i',prefix)[0]))
        child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        open(child_file,'w').write(str(child.pid))
        reply=dict(protocolVersion='worker-1',kind='hello',requestId=hello['requestId'],payload='',delayMilliseconds=0,cancelled=False)
        body=json.dumps(reply).encode()
        sys.stdout.buffer.write(struct.pack('<i',len(body))+body);sys.stdout.buffer.flush()
        if mode=='legacy':
            prefix=sys.stdin.buffer.read(4)
            sys.stdin.buffer.read(struct.unpack('<i',prefix)[0])
        time.sleep(30)
        """;
}
