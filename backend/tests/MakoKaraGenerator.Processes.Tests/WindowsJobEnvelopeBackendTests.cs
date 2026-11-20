using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MakoKaraGenerator.Processes.MediaEnvelope;
using Xunit.Abstractions;

namespace MakoKaraGenerator.Processes.Tests;

public sealed class WindowsJobEnvelopeBackendTests(ITestOutputHelper output)
{
    private const long NativeBytes = 128 * 1024 * 1024;
    private static readonly EnvelopeExecutionPolicy Policy = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(15));

    [Fact]
    public async Task DirectLaunchUsesFourBinaryPipesAndAnExplicitInheritanceList()
    {
        if (!WindowsJobObjectGuard.IsSupported) return;
        using var sentinel = new EventWaitHandle(false, EventResetMode.ManualReset);
        var attributes = new Program.MediaChildNative.SecurityAttributes
        { Length = Marshal.SizeOf<Program.MediaChildNative.SecurityAttributes>(), Inherit = 1 };
        sentinel.SafeWaitHandle = Program.MediaChildNative.CreateEvent(ref attributes, 1, 0, 0);
        Assert.False(sentinel.SafeWaitHandle.IsInvalid);
        string sentinelValue = unchecked((nuint)sentinel.SafeWaitHandle.DangerousGetHandle()).ToString(CultureInfo.InvariantCulture);
        string? previous = Environment.GetEnvironmentVariable(Program.PrivateEnvironment);
        Environment.SetEnvironmentVariable(Program.PrivateEnvironment, "not-for-the-helper");
        try
        {
            var fixture = new Fixture();
            byte[] pixels = new byte[24_000];
            for (int index = 0; index < pixels.Length; index++) pixels[index] = unchecked((byte)index);
            EnvelopeCompletion result = await fixture.RunAsync(Command("--closure", Program.BinaryArgument, sentinelValue), async (session, token) =>
            {
                Assert.Equal(16_384, session.PipeBackingBytes);
                await session.Input.WriteAsync(pixels, token);
                byte[] actual = new byte[pixels.Length];
                byte[] metadata = new byte[3], diagnostics = new byte[3];
                await Task.WhenAll(session.Output.ReadExactlyAsync(actual, token).AsTask(),
                    session.Metadata.ReadExactlyAsync(metadata, token).AsTask(), session.Diagnostics.ReadExactlyAsync(diagnostics, token).AsTask());
                Assert.Equal(pixels, actual);
                Assert.Equal(new byte[] { 0, 0x1a, 0xff }, metadata);
                Assert.Equal(new byte[] { 0xff, 0, 0x1a }, diagnostics);
                Assert.True(session.ProgressVersion > 4);
            });
            Assert.False(sentinel.WaitOne(0));
            Assert.Equal(WindowsJobMemoryNotifications.None, Assert.IsType<WindowsJobMemoryDiagnostics>(result.WindowsJobMemory).Notifications);
            await fixture.AssertDrainedAsync();
        }
        finally { Environment.SetEnvironmentVariable(Program.PrivateEnvironment, previous); }
    }

    [Fact]
    public async Task HeldLaunchCannotRunUntilAdmittedAndReleased()
    {
        if (!WindowsJobObjectGuard.IsSupported) return;
        await RunHeldProbeAsync("corrupt", async (session, owner, token) =>
        {
            EnvelopeException rejected = await Assert.ThrowsAsync<EnvelopeException>(() => session.ReleaseExecGateAsync(token).AsTask());
            Assert.Equal(EnvelopeFailure.CapacityUnsupported, rejected.Failure);
            await owner.ValidateControlsAsync(NativeBytes, token);
            using var heldRead = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Output.ReadAsync(new byte[1], heldRead.Token).AsTask());
            Assert.Equal(0, session.ProgressVersion);
            await session.ReleaseExecGateAsync(token);
            byte[] marker = new byte[1];
            await session.Output.ReadExactlyAsync(marker, token);
            Assert.Equal(0xfb, marker[0]);
        });
    }

    [Fact]
    public async Task NaturalExitDrainsAllThreeTailsEvenWithoutAConsumer()
    {
        if (!WindowsJobObjectGuard.IsSupported) return;
        var fixture = new Fixture();
        Assert.Equal(0, (await fixture.RunAsync(Command("tails"))).ExitCode);
        await fixture.AssertDrainedAsync();
    }

    [Fact]
    public async Task CanStartEnvelopeInsideTheWorkersOuterJob()
    {
        if (!WindowsJobObjectGuard.IsSupported) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var start = new ProcessStartInfo(Program.DotnetExecutable);
        foreach (string argument in Program.MediaChildArguments(["nested"])) start.ArgumentList.Add(argument);
        WindowsControlledProcess worker = WindowsJobObjectGuard.StartProcess(start);
        await using WindowsJobObjectGuard guard = worker.Guard;
        using Process process = worker.ManagedProcess;
        using Stream input = worker.StandardInput;
        using Stream stdout = worker.StandardOutput;
        using StreamReader stderr = worker.StandardError;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(0, guard.ObserveActiveProcessCount());
        }
        finally { await guard.KillAndDrainAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedProfileOrFailedCreateNeverRunsTheConsumer(bool invalidProfile)
    {
        if (!WindowsJobObjectGuard.IsSupported) return;
        var fixture = new Fixture(invalidProfile ? NativeBytes - 4096 : NativeBytes);
        string missing = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N"), "missing.exe");
        EnvelopeException failure = await Assert.ThrowsAsync<EnvelopeException>(() => fixture.RunAsync(
            invalidProfile ? Command("tails") : new EnvelopeCommand(missing, missing, []),
            (_, _) => throw new InvalidOperationException("Rejected launch ran its callback.")));
        Assert.Equal(EnvelopeFailure.CapacityUnsupported, failure.Failure);
        Assert.Equal(invalidProfile ? "windows-envelope-profile-unavailable" : "windows-envelope-held-start-failed", failure.SafeCode);
        await fixture.AssertDrainedAsync();
    }

    [Fact]
    public async Task CallerCancellationCancelsBlockedReadAndDrainsARealOrphan()
    {
        if (!WindowsJobObjectGuard.IsSupported) return;
        var fixture = new Fixture();
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Process? orphan = null;
        try
        {
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync(Command("orphan"),
                async (session, token) =>
                {
                    byte[] pid = new byte[sizeof(int)];
                    await session.Output.ReadExactlyAsync(pid, token);
                    orphan = Process.GetProcessById(BitConverter.ToInt32(pid));
                    _ = orphan.SafeHandle;
                    await session.Input.WriteAsync(new byte[] { 1 }, token);
                    Task<int> blockedRead = session.Metadata.ReadAsync(new byte[1], token).AsTask();
                    caller.Cancel();
                    await blockedRead;
                    token.ThrowIfCancellationRequested();
                }, caller.Token));
            Assert.Equal(caller.Token, failure.CancellationToken);
            Assert.NotNull(orphan);
            Assert.True(orphan.HasExited);
            await fixture.AssertDrainedAsync();
        }
        finally { orphan?.Dispose(); }
    }

    [Theory]
    [InlineData("process-limit", WindowsJobMemoryNotifications.ProcessMemoryLimit)]
    [InlineData("job-limit", WindowsJobMemoryNotifications.JobMemoryLimit)]
    public async Task RealCommitLimitPacketsOverrideTruncatedWire(string mode, WindowsJobMemoryNotifications expected)
    {
        if (!WindowsJobObjectGuard.IsSupported) return;
        var fixture = new Fixture();
        bool truncated = false;
        using var wireDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        EnvelopeException failure = await Assert.ThrowsAsync<EnvelopeException>(() => fixture.RunAsync(Command(mode), async (session, _) =>
        {
            try { await session.Output.ReadExactlyAsync(new byte[2], wireDeadline.Token); }
            catch (EndOfStreamException) { truncated = true; throw; }
        }));
        var memory = Assert.IsType<WindowsJobMemoryDiagnostics>(failure.WindowsJobMemory);
        output.WriteLine("{0}: {1}, peak process={2}, peak job={3}", mode, memory.Notifications, memory.PeakProcessCommitBytes, memory.PeakJobCommitBytes);
        Assert.Equal(EnvelopeFailure.CapacityExceeded, failure.Failure);
        Assert.True(memory.Notifications.HasFlag(expected));
        Assert.True(truncated);
        await fixture.AssertDrainedAsync();
    }

    [Theory]
    [InlineData("process-limit", WindowsJobMemoryNotifications.ProcessMemoryLimit)]
    [InlineData("job-limit", WindowsJobMemoryNotifications.JobMemoryLimit)]
    public async Task KernelDeniesTheCommitEvenWithoutSupervisoryTermination(string mode, WindowsJobMemoryNotifications expected)
    {
        if (!WindowsJobObjectGuard.IsSupported) return;
        await RunHeldProbeAsync(mode, async (session, owner, token) =>
        {
            await owner.ValidateControlsAsync(NativeBytes, token);
            await session.ReleaseExecGateAsync(token);
            EnvelopeRunState terminal = await AwaitTerminalAsync(owner, token);
            var memory = Assert.IsType<WindowsJobMemoryDiagnostics>(terminal.WindowsJobMemory);
            output.WriteLine("{0}: exit={1}, packets={2}, peak process={3}, peak job={4}", mode, terminal.ExitCode,
                memory.Notifications, memory.PeakProcessCommitBytes, memory.PeakJobCommitBytes);
            Assert.Equal(72, terminal.ExitCode);
            Assert.True(memory.Notifications.HasFlag(expected));
        });
    }

    [Fact]
    public async Task HighCommitAndNoMemoryExitStatusAreNotEvidenceOfCapacityExceeded()
    {
        if (!WindowsJobObjectGuard.IsSupported) return;
        var fixture = new Fixture();
        EnvelopeException failure = await Assert.ThrowsAsync<EnvelopeException>(() => fixture.RunAsync(Command("corrupt")));
        Assert.Equal(EnvelopeFailure.DecodeFailed, failure.Failure);
        var memory = Assert.IsType<WindowsJobMemoryDiagnostics>(failure.WindowsJobMemory);
        Assert.Equal(WindowsJobMemoryNotifications.None, memory.Notifications);
        Assert.InRange(memory.PeakJobCommitBytes, 88UL * 1024 * 1024, (ulong)NativeBytes);
        await fixture.AssertDrainedAsync();

        fixture = new Fixture();
        await Assert.ThrowsAsync<EndOfStreamException>(() => fixture.RunAsync(Command("corrupt"),
            async (session, token) => await session.Output.ReadExactlyAsync(new byte[2], token)));
        await fixture.AssertDrainedAsync();
    }

    private static EnvelopeCommand Command(params string[] args) => new(Program.DotnetExecutable, Program.DotnetExecutable, Program.MediaChildArguments(args));

    private static async Task RunHeldProbeAsync(string mode, Func<IEnvelopeSession, EnvelopeRunOwner, CancellationToken, Task> probe)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = deadline.Token;
        await using IEnvelopeSession session = await new WindowsJobEnvelopeBackend().OpenAsync(token);
        await session.StartHeldAsync(new("test", "unused", NativeBytes, Command(mode)), token);
        EnvelopeRunOwner owner = ((IHandleEnvelopeSession)session).CaptureOwner(token);
        try { await probe(session, owner, token); }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            CancellationToken drain = cleanup.Token;
            EnvelopeRunState state = await owner.ObserveOwnedAsync(drain);
            if (!state.Terminal || state.Populated) await owner.KillOwnedAsync(drain);
            await Task.WhenAll(session.Output.CopyToAsync(Stream.Null, 4096, drain), session.Metadata.CopyToAsync(Stream.Null, 4096, drain),
                session.Diagnostics.CopyToAsync(Stream.Null, 4096, drain));
            state = await AwaitTerminalAsync(owner, drain);
            await owner.ReleaseOwnedAsync(state, drain);
        }
    }

    private static async Task<EnvelopeRunState> AwaitTerminalAsync(EnvelopeRunOwner owner, CancellationToken token)
    {
        while (true)
        {
            EnvelopeRunState state = await owner.ObserveOwnedAsync(token);
            if (state.Terminal && !state.Populated) return state;
            await Task.Delay(10, token);
        }
    }

    private sealed class Fixture(long nativeBytes = NativeBytes)
    {
        private readonly EnvelopeQuarantineStore _quarantine = new();
        private readonly DecodeOperationBudget _budget = new(nativeBytes + DecodeOperationBudget.TransportReservationPerProducer,
            DecodeOperationBudget.TransportReservationPerProducer, [nativeBytes], 1, new Source());

        internal Task<EnvelopeCompletion> RunAsync(EnvelopeCommand command, Func<IEnvelopeSession, CancellationToken, Task>? consume = null,
            CancellationToken token = default) => new OwnedNativeEnvelope(new WindowsJobEnvelopeBackend(), _quarantine, Policy)
                .RunAsync(_budget.BeginNative(0), command, consume ?? ((_, _) => Task.CompletedTask), token);

        internal async Task AssertDrainedAsync()
        {
            Assert.Equal(0, _quarantine.Count);
            Assert.Equal(0, _budget.LiveManagedBytes);
            await _budget.ReleaseSourceAsync();
        }

        private sealed class Source : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
