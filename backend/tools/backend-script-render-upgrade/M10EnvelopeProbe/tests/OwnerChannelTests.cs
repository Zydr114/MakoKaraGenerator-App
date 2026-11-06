using System.Threading.Channels;
using Xunit;

namespace M10.NativeEnvelope.Probe.BoundaryTests;

/// <summary>Behavioral regression source for the parent stdin/late ACK failure window; never run in this revision task.</summary>
public sealed class OwnerChannelTests
{
    private const string Run = "11111111111111111111111111111111";
    private const string Scenario = "owner-unref-first";

    [Fact]
    public async Task CanceledLateAckClosesOwnedStdinAndJoinsOriginalReaderBeforeCleanupCanStartAnotherRead()
    {
        string directory = Path.Combine(Path.GetTempPath(), "m10-owner-channel-" + Guid.NewGuid().ToString("N"));
        using var journal = new EvidenceJournal(directory);
        var stdin = new ControlledPipe();
        var stdout = new ControlledPipe();
        var delayedOutput = new DelayedReader(stdout.Reader);
        var childOwnsUnit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishChildCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childWire = new ProbeWire(stdin.Reader, stdout.Writer, Run, Scenario);
        var ownerChannel = new ProbeOwnerChannel(new ProbeWire(delayedOutput, stdin.Writer, Run, Scenario),
            stdin.Writer.Close, journal, Scenario);
        using var canceled = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        int receivedCommands = 0;
        Task child = Task.Run(async () =>
        {
            ProbeMessage start = await childWire.ReadAsync(CancellationToken.None)
                ?? throw new InvalidOperationException("controlled StartHeld missing");
            receivedCommands++;
            childOwnsUnit.TrySetResult();
            await childWire.SendAsync(start.Sequence, "StartHeldAck", new EmptyPayload(), CancellationToken.None);
            // This is the SAME child reader, deliberately ignoring token cancellation.
            // The original bug leaves this await blocked because the parent never sends EOF.
            Assert.Null(await childWire.ReadAsync(CancellationToken.None));
            childCleanup.TrySetResult();
            await finishChildCleanup.Task;
            stdout.Writer.Close();
        });
        Task<EmptyPayload>? command = null;
        try
        {
            command = ownerChannel.CommandAsync<EmptyPayload>("StartHeld", canceled.Token);
            await childOwnsUnit.Task.WaitAsync(deadline.Token);
            await delayedOutput.Entered.Task.WaitAsync(deadline.Token);
            canceled.Cancel();
            await childCleanup.Task.WaitAsync(deadline.Token);
            Assert.True(ownerChannel.Broken);
            await ownerChannel.InputCloseCompletion.WaitAsync(deadline.Token);
            Assert.True(ownerChannel.InputClosed);
            Assert.True(ownerChannel.ReadInFlight);
            Assert.False(command.IsCompleted);
            Task joined = ownerChannel.JoinAsync();
            Assert.False(joined.IsCompleted); // Do not abandon the late real read via WaitAsync.
            delayedOutput.AllowRead.TrySetResult();
            finishChildCleanup.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { _ = await command; });
            await joined.WaitAsync(deadline.Token);
            await child.WaitAsync(deadline.Token);
            Assert.False(ownerChannel.ReadInFlight);
            Assert.Equal(1, receivedCommands); // No AbortOwned or Unref is resent on the broken transport.
            Assert.Equal(1, delayedOutput.MaxConcurrentReads);
        }
        finally
        {
            // Test-fixture release only: prevent a failed regression assertion from
            // leaking the very fake blocked Tasks this test is intended to expose.
            stdin.Writer.Close();
            delayedOutput.AllowRead.TrySetResult();
            finishChildCleanup.TrySetResult();
            await child;
            if (command is not null) { try { await command; } catch (Exception) { } }
            await ownerChannel.JoinAsync();
            journal.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidReplyClosesInputBeforeFailureEscapesAndRejectsAnotherCommand()
    {
        string directory = Path.Combine(Path.GetTempPath(), "m10-owner-channel-" + Guid.NewGuid().ToString("N"));
        using var journal = new EvidenceJournal(directory);
        var stdin = new ControlledPipe();
        var stdout = new ControlledPipe();
        var childWire = new ProbeWire(stdin.Reader, stdout.Writer, Run, Scenario);
        var channel = new ProbeOwnerChannel(new ProbeWire(stdout.Reader, stdin.Writer, Run, Scenario),
            stdin.Writer.Close, journal, Scenario);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Task child = Task.Run(async () =>
        {
            ProbeMessage start = await childWire.ReadAsync(CancellationToken.None)
                ?? throw new InvalidOperationException("controlled command missing");
            await childWire.SendAsync(start.Sequence, "WrongAck", new EmptyPayload(), CancellationToken.None);
            Assert.Null(await childWire.ReadAsync(CancellationToken.None));
            stdout.Writer.Close();
        });
        try
        {
            await Assert.ThrowsAsync<ProbeFailure>(() => channel.CommandAsync<EmptyPayload>("StartHeld", CancellationToken.None));
            Assert.True(channel.InputClosed);
            await Assert.ThrowsAsync<ProbeFailure>(() => channel.CommandAsync<EmptyPayload>("AbortOwned", CancellationToken.None));
            await child.WaitAsync(deadline.Token);
            await channel.JoinAsync();
        }
        finally
        {
            stdin.Writer.Close();
            await child;
            journal.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ControlledPipe
    {
        private readonly Channel<byte> _bytes = Channel.CreateUnbounded<byte>();
        internal Stream Reader { get; }
        internal Stream Writer { get; }
        internal ControlledPipe() { Reader = new Endpoint(_bytes, false); Writer = new Endpoint(_bytes, true); }
        private sealed class Endpoint(Channel<byte> bytes, bool writing) : Stream
        {
            public override bool CanRead => !writing;
            public override bool CanWrite => writing;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (writing) throw new NotSupportedException();
                if (buffer.IsEmpty) return 0;
                byte value;
                try { value = await bytes.Reader.ReadAsync(CancellationToken.None); }
                catch (ChannelClosedException) { return 0; }
                buffer.Span[0] = value;
                return 1;
            }
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!writing) throw new NotSupportedException();
                foreach (byte value in buffer.Span)
                    if (!bytes.Writer.TryWrite(value)) throw new IOException("controlled input closed");
                return ValueTask.CompletedTask;
            }
            protected override void Dispose(bool disposing)
            { if (disposing && writing) bytes.Writer.TryComplete(); base.Dispose(disposing); }
        }
    }

    private sealed class DelayedReader(Stream actual) : Stream
    {
        private int _reads;
        internal int MaxConcurrentReads { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaxConcurrentReads = Math.Max(MaxConcurrentReads, Interlocked.Increment(ref _reads));
            Entered.TrySetResult();
            try { await AllowRead.Task; return await actual.ReadAsync(buffer, CancellationToken.None); }
            finally { Interlocked.Decrement(ref _reads); }
        }
    }
}
