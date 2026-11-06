namespace M10.NativeEnvelope.Probe;

// One ordered exchange on the parent's actual child stdin/stdout. Breaking this
// channel closes only our owned stdin writer, causing REAL EOF in the child's
// original Console reader. No command is resent and no Process is killed here.
internal sealed class ProbeOwnerChannel(ProbeWire wire, Action closeInput, EvidenceJournal journal, string scenario)
{
    private readonly object _gate = new();
    private Task? _commandCall, _sendCall, _closeInputCall;
    private Task<ProbeMessage?>? _readCall;
    private long _sequence;
    private bool _broken;
    internal bool Broken { get { lock (_gate) return _broken; } }
    internal bool InputClosed { get { lock (_gate) return _closeInputCall is { IsCompletedSuccessfully: true }; } }
    internal bool ReadInFlight => _readCall is { IsCompleted: false };
    internal Task InputCloseCompletion
    { get { lock (_gate) return _closeInputCall ?? throw new ProbeFailure("observer-probe-owner-input-close-not-started"); } }
    internal object State => new { broken = Broken, inputClosed = InputClosed,
        commandInFlight = _commandCall is { IsCompleted: false },
        sendInFlight = _sendCall is { IsCompleted: false }, readInFlight = ReadInFlight,
        inputCloseInFlight = _closeInputCall is { IsCompleted: false },
        inputCloseFailed = _closeInputCall is { IsFaulted: true } };

    internal Task<T> CommandAsync<T>(string kind, CancellationToken token)
    {
        lock (_gate)
        {
            ObserverSupport.Require(!_broken, "observer-probe-owner-channel-unusable");
            ObserverSupport.Require(_commandCall is null or { IsCompleted: true },
                "observer-probe-owner-command-already-in-flight");
            _sendCall = null;
            _readCall = null;
            // An explicit publication gate (not a scheduling yield) ensures the
            // SAME main task is retained before transport or cancellation runs.
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<T> call = ExchangeAsync<T>(++_sequence, kind, published.Task, token);
            _commandCall = call;
            published.SetResult();
            return call;
        }
    }

    private async Task<T> ExchangeAsync<T>(long sequence, string kind, Task published, CancellationToken token)
    {
        await published.ConfigureAwait(false);
        // Console/pipe streams may ignore cancellation after entering ReadAsync.
        // EOF must be driven from the parent writer, BEFORE joining that read.
        using CancellationTokenRegistration canceled = token.Register(static state =>
            ((ProbeOwnerChannel)state!).Break(), this);
        try
        {
            token.ThrowIfCancellationRequested();
            journal.Write(scenario, "owner-command", new { sequence, kind });
            _sendCall = wire.SendAsync(sequence, kind, new EmptyPayload(), token);
            await _sendCall.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _readCall = wire.ReadAsync(token);
            ProbeMessage reply = await _readCall.ConfigureAwait(false)
                ?? throw new ProbeFailure("observer-probe-owner-control-eof");
            // A late ACK does not turn an already canceled command into success.
            token.ThrowIfCancellationRequested();
            ObserverSupport.Require(reply.Sequence == sequence, "observer-probe-owner-reply-sequence");
            journal.Write(scenario, "owner-reply", reply);
            ObserverSupport.Require(reply.Kind == kind + "Ack", "observer-probe-owner-command-failed");
            T payload = reply.Payload<T>();
            token.ThrowIfCancellationRequested();
            return payload;
        }
        catch
        {
            Break();
            // Never WaitAsync(token) these actual Tasks, and never let JoinAsync
            // begin a competing stdout read while this original read is alive.
            await JoinTransportAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void Break()
    {
        lock (_gate)
        {
            _broken = true;
            // Dispose may itself wait for a pending pipe write. Keep its real Task
            // and run it off the cancellation callback to avoid blocking Cancel().
            _closeInputCall ??= Task.Run(closeInput);
        }
    }

    private async Task JoinTransportAsync()
    {
        if (_sendCall is { } send) { try { await send.ConfigureAwait(false); } catch (Exception) { } }
        if (_readCall is { } read) { try { await read.ConfigureAwait(false); } catch (Exception) { } }
        Task? close;
        lock (_gate) close = _closeInputCall;
        if (close is not null) { try { await close.ConfigureAwait(false); } catch (Exception) { } }
    }

    internal async Task JoinAsync()
    {
        Task? command;
        lock (_gate) command = _commandCall;
        if (command is not null) { try { await command.ConfigureAwait(false); } catch (Exception) { } }
        await JoinTransportAsync().ConfigureAwait(false);
    }

    internal async Task BreakAndJoinAsync()
    {
        Break();
        await JoinAsync().ConfigureAwait(false);
        Task close;
        lock (_gate) close = _closeInputCall!;
        // A failed stdin close is still unknown; it must not be logged as EOF.
        await close.ConfigureAwait(false);
    }
}
