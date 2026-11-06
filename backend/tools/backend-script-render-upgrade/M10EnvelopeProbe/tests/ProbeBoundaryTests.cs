using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Processes.MediaEnvelope;
using Xunit;

namespace M10.NativeEnvelope.Probe.BoundaryTests;

/// <summary>Only controlled protocol and retained-owner state; no diagnostics DSO or real manager is opened.</summary>
public sealed class ProbeBoundaryTests
{
    private const string Run = "11111111111111111111111111111111";
    private const string Scenario = "owner-unref-first";
    private static readonly EnvelopeIdentity Identity = new(Run, "mako-media-" + Run + ".service",
        "/org/freedesktop/systemd1/unit/owned", new string('a', 32),
        "/user.slice/mako-media-" + Run + ".service", 424242, 991, 31, 47);

    [Theory]
    [InlineData("\"extra\":1,", "JsonException")]
    [InlineData("\"Version\":1,", "observer-probe-control-duplicate-field")]
    [InlineData("\"Data\":{\"nested\":1,\"nested\":2},", "observer-probe-control-duplicate-field")]
    public async Task WireRejectsAdditionalAndDuplicateFields(string prefix, string expected)
    {
        string json = "{" + prefix + EnvelopeTail() + "}\n";
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var wire = new ProbeWire(input, Stream.Null, Run, Scenario);
        Exception? error = await Record.ExceptionAsync(async () => { _ = await wire.ReadAsync(CancellationToken.None); });
        Assert.NotNull(error);
        if (expected == "JsonException") Assert.IsType<JsonException>(error);
        else Assert.Equal(expected, Assert.IsType<ProbeFailure>(error).Message);
    }

    [Fact]
    public async Task WireRejectsTruncatedAndOversizeFramesWithoutTreatingThemAsEof()
    {
        foreach ((string text, string code) in new[]
        {
            ("{" + EnvelopeTail() + "}", "observer-probe-control-truncated"),
            (new string('x', 16385) + "\n", "observer-probe-control-line-overflow"),
        })
        {
            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
            var wire = new ProbeWire(input, Stream.Null, Run, Scenario);
            ProbeFailure error = await Assert.ThrowsAsync<ProbeFailure>(async () => { _ = await wire.ReadAsync(CancellationToken.None); });
            Assert.Equal(code, error.Message);
        }
    }

    [Fact]
    public async Task WireBoundsTotalFrameCount()
    {
        string line = "{" + EnvelopeTail() + "}\n";
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(line, 65))));
        var wire = new ProbeWire(input, Stream.Null, Run, Scenario);
        for (int index = 0; index < 64; index++) Assert.NotNull(await wire.ReadAsync(CancellationToken.None));
        ProbeFailure error = await Assert.ThrowsAsync<ProbeFailure>(async () => { _ = await wire.ReadAsync(CancellationToken.None); });
        Assert.Equal("observer-probe-control-count-overflow", error.Message);
    }

    [Fact]
    public async Task AmbiguousUnrefRetainsSameObjectAndFinishKnownNeverRetriesIt()
    {
        using var fixture = new JournalFixture();
        var observer = new FakeObserver { AmbiguousUnref = true };
        var lease = new ObserverLease(fixture.Journal, Scenario, Identity, new FakeBackend(observer));
        await lease.AttachAsync(CancellationToken.None);
        await Assert.ThrowsAsync<EnvelopeException>(() => lease.ReleaseAsync(CancellationToken.None));
        Assert.True(lease.UnrefAttempted);
        Assert.False(lease.Unreferenced);
        await Assert.ThrowsAsync<ProbeFailure>(() => lease.CleanupAsync(finishKnown: true, CancellationToken.None));
        Assert.Same(observer, lease.Observer);
        Assert.Equal(1, observer.SentUnrefs);
        Assert.Equal(0, observer.CloseCalls);
        Assert.False(lease.Closed);
    }

    [Fact]
    public async Task KnownLivePreflightRefusalKeepsSameObserverAndAllowsFirstTerminalUnref()
    {
        using var fixture = new JournalFixture();
        var observer = new FakeObserver { Live = true };
        var lease = new ObserverLease(fixture.Journal, Scenario, Identity, new FakeBackend(observer));
        await lease.AttachAsync(CancellationToken.None);
        await lease.VerifyLiveRefusalsAsync(CancellationToken.None);
        Assert.Same(observer, lease.Observer);
        Assert.False(lease.UnrefAttempted);
        Assert.False(lease.Closed);
        Assert.Equal(0, observer.SentUnrefs);
        Assert.True(observer.Observes > 0);
        observer.Live = false;
        await lease.CleanupAsync(finishKnown: true, CancellationToken.None);
        Assert.True(lease.Closed);
        Assert.Equal(1, observer.SentUnrefs);
    }

    [Fact]
    public async Task FailedAttachWithHistoricalVerifiedValueStillReturnsRealRetainedOwner()
    {
        using var fixture = new JournalFixture();
        var observer = new FakeObserver();
        var failure = new EnvelopeException(EnvelopeFailure.DrainFailed, "native-observer-attach-canceled");
        var lease = new ObserverLease(fixture.Journal, Scenario, Identity, new FakeBackend(observer, failure));
        await lease.AttachAsync(CancellationToken.None);
        Assert.Same(observer, lease.Observer);
        Assert.False(lease.Result!.Attached);
        Assert.True(observer.AttachVerified);
        Assert.False(lease.Closed);
        Assert.Equal(0, observer.CloseCalls);
    }

    [Fact]
    public async Task GuardianKeepsStatusAvailableWhileAnAcceptedOperationIsJoined()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Path.Combine(Path.GetTempPath(), "m10-guardian-" + Guid.NewGuid().ToString("N"));
        ObserverSupport.MakePrivateDirectory(directory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guardian = new ProbeGuardian(directory, async command =>
        {
            if (command == "observe")
            {
                entered.TrySetResult();
                await release.Task;
                completed.TrySetResult();
            }
            return new GuardianReply(new { state = "controlled-owner-retained" }, command == "finish-known");
        }, _ => true); // Peer credential behavior has its separate native boundary tests.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(directory, "guardian.sock")));
            Assert.Contains("\"accepted\":false", await RequestAsync(directory, "observe", deadline.Token));
            Task retained = guardian.WaitRetainedAsync();
            Assert.Contains("\"accepted\":true", await RequestAsync(directory, "observe", deadline.Token));
            await entered.Task.WaitAsync(deadline.Token);
            Assert.Contains("\"operationInFlight\":true", await RequestAsync(directory, "status", deadline.Token));
            Assert.Contains("\"accepted\":false", await RequestAsync(directory, "finish-known", deadline.Token));
            release.TrySetResult();
            await completed.Task.WaitAsync(deadline.Token);
            while ((await RequestAsync(directory, "status", deadline.Token)).Contains("\"operationInFlight\":true", StringComparison.Ordinal))
                await Task.Delay(1, deadline.Token);
            Assert.Contains("\"accepted\":true", await RequestAsync(directory, "finish-known", deadline.Token));
            await retained.WaitAsync(deadline.Token);
        }
        finally
        {
            release.TrySetResult();
            await guardian.CloseAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string> RequestAsync(string directory, string command, CancellationToken token)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(directory, "guardian.sock")), token);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), token);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadLineAsync(token) ?? throw new InvalidOperationException("controlled guardian EOF");
    }

    private static string EnvelopeTail() => "\"Version\":1,\"Run\":\"" + Run
        + "\",\"Scenario\":\"" + Scenario + "\",\"Sequence\":1,\"Kind\":\"ObserveOwner\",\"Data\":{}";

    private static EnvelopeSnapshot Snapshot(bool live) => new(Identity.UnitName, Identity.UnitPath,
        Identity.InvocationId, "Mako media envelope " + Run, Identity.ControlGroup, live ? Identity.MainPid : 0,
        live ? Identity.StartTicks : 0, Identity.CgroupDevice, Identity.CgroupInode, live, true, live, false,
        ObserverSupport.NativeBytes, ObserverSupport.NativeBytes, 0, ObserverSupport.NativeBytes, 0,
        true, true, false, "exec", "control-group", "no", "kill", live ? "active" : "inactive",
        "success", live ? 0 : 1, 0, 0, true, true);

    private sealed class FakeBackend(FakeObserver observer, EnvelopeException? failure = null) : IEnvelopeObserverBackend
    {
        public ValueTask<EnvelopeObserverAttachResult> AttachAsync(EnvelopeIdentity expectedIdentity,
            ulong expectedLimitBytes, CancellationToken cancellationToken) => ValueTask.FromResult(new EnvelopeObserverAttachResult(observer, failure));
    }

    private sealed class FakeObserver : IEnvelopeObserver
    {
        internal bool Live, AmbiguousUnref;
        internal int SentUnrefs, CloseCalls, Observes;
        private bool _released;
        public EnvelopeIdentity ExpectedIdentity => Identity;
        public bool AttachVerified => true;
        public ValueTask<EnvelopeSnapshot> ObserveAsync(CancellationToken cancellationToken)
        { Observes++; return ValueTask.FromResult(Snapshot(Live)); }
        public ValueTask KillVerifiedCgroupAsync(CancellationToken cancellationToken)
        { Live = false; return ValueTask.CompletedTask; }
        public ValueTask UnrefOwnReferenceAsync(CancellationToken cancellationToken)
        {
            if (Live) return ValueTask.FromException(new EnvelopeException(EnvelopeFailure.DrainFailed, "native-unit-not-terminal"));
            SentUnrefs++;
            if (AmbiguousUnref) return ValueTask.FromException(new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "controlled-unref-ambiguous"));
            _released = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            CloseCalls++;
            return _released ? ValueTask.CompletedTask : ValueTask.FromException(new EnvelopeException(
                EnvelopeFailure.DrainFailed, "native-observer-reference-unresolved"));
        }
    }

    private sealed class JournalFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "m10-boundary-" + Guid.NewGuid().ToString("N"));
        internal EvidenceJournal Journal { get; }
        internal JournalFixture() => Journal = new(_directory);
        public void Dispose() { Journal.Dispose(); Directory.Delete(_directory, recursive: true); }
    }
}
