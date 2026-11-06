using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace M10.NativeEnvelope.Probe;

internal sealed record GuardianReply(object Data, bool Resolved = false);

// The socket owns no replacement envelope authority. Its callback closes over
// the SAME operation objects and exposes no caller-supplied identity or target.
internal sealed class ProbeGuardian : IAsyncDisposable
{
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, Task<GuardianReply>> _handle;
    private readonly Func<int, bool> _authorizePeer;
    private readonly Task _server;
    private readonly string _path;
    private readonly TaskCompletionSource _resolved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _retained;
    private Task? _operation;
    private object? _lastResult;
    internal bool Retained => _retained;

    internal ProbeGuardian(string directory, Func<string, Task<GuardianReply>> handle,
        Func<int, bool>? authorizePeer = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        _path = Path.Combine(directory, "guardian.sock");
        ObserverSupport.Require(Encoding.UTF8.GetByteCount(_path) <= 100, "observer-probe-guardian-path-too-long");
        _handle = handle;
        _authorizePeer = authorizePeer ?? (static fd => ProbeDiagnostics.SameUid(fd));
        _listener.Bind(new UnixDomainSocketEndPoint(_path));
        File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _listener.Listen(1);
        _server = ServeAsync();
    }

    internal async Task WaitRetainedAsync()
    {
        _retained = true;
        await _resolved.Task.ConfigureAwait(false);
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener.AcceptAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (SocketException) when (!_stop.IsCancellationRequested)
            {
                try { await Task.Delay(100, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                continue;
            }
            using (client)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                TaskCompletionSource? acknowledged = null;
                try
                {
                    ObserverSupport.Require(_authorizePeer(checked((int)client.Handle)),
                        "observer-probe-guardian-peer-rejected");
                    using var stream = new NetworkStream(client, ownsSocket: false);
                    byte[] request = new byte[128], one = new byte[1];
                    int used = 0;
                    while (true)
                    {
                        int read = await stream.ReadAsync(one, deadline.Token).ConfigureAwait(false);
                        ObserverSupport.Require(read == 1, "observer-probe-guardian-request-truncated");
                        if (one[0] == 10) break;
                        ObserverSupport.Require(used < request.Length && one[0] is >= 32 and <= 126,
                            "observer-probe-guardian-request-invalid");
                        request[used++] = one[0];
                    }
                    string command = Encoding.ASCII.GetString(request, 0, used);
                    ObserverSupport.Require(command is "status" or "observe" or "kill-drain" or "finish-known",
                        "observer-probe-guardian-command-invalid");
                    // The request transport is bounded; callbacks join real native
                    // operations and keep their Tasks/owners alive if that takes longer.
                    object responseValue;
                    if (command == "status")
                    {
                        GuardianReply current = await _handle(command).ConfigureAwait(false);
                        responseValue = new { retained = _retained, operationInFlight = _operation is { IsCompleted: false },
                            result = _operation is { IsCompleted: true } ? _lastResult : current.Data };
                    }
                    else if (!_retained || _operation is { IsCompleted: false })
                        responseValue = new { accepted = false, code = "observer-probe-guardian-active-or-busy" };
                    else
                    {
                        // Keep the actual task in this live guardian and return a
                        // bounded ACK. Status remains available during native joins.
                        acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _operation = RunOperationAsync(command, acknowledged.Task);
                        responseValue = new { accepted = true, command };
                    }
                    byte[] response = JsonSerializer.SerializeToUtf8Bytes(responseValue, ObserverSupport.Json);
                    ObserverSupport.Require(response.Length <= 16384, "observer-probe-guardian-response-overflow");
                    await stream.WriteAsync(response, deadline.Token).ConfigureAwait(false);
                    await stream.WriteAsync(new byte[] { 10 }, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception) when (!_stop.IsCancellationRequested)
                {
                    // A malformed/local client, disconnect, or timed-out response
                    // cannot release owners or stop the retained server.
                }
                finally
                {
                    // Completion may close the live guardian only after the ACK
                    // write was joined (or this local transport definitively failed).
                    acknowledged?.TrySetResult();
                }
            }
        }
    }

    private async Task RunOperationAsync(string command, Task acknowledged)
    {
        await Task.Yield(); // Do not run filesystem/native work on the bounded socket ACK path.
        try
        {
            GuardianReply reply = await _handle(command).ConfigureAwait(false);
            _lastResult = reply.Data;
            if (reply.Resolved)
            {
                await acknowledged.ConfigureAwait(false);
                _resolved.TrySetResult();
            }
        }
        catch (Exception error) { _lastResult = new { error = ObserverSupport.Safe(error) }; }
    }

    internal async Task CloseAsync()
    {
        _stop.Cancel();
        await _server.ConfigureAwait(false);
        if (_operation is not null) await _operation.ConfigureAwait(false);
        _listener.Dispose();
        File.Delete(_path);
        _stop.Dispose();
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
