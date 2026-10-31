using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MakoKaraGenerator.Media;

/// <summary>Owns one process and its pipes; never discovers or terminates other media processes.</summary>
internal sealed class OwnedMediaProcess : IDisposable
{
    private const int RetainedErrorCharacters = 32_768;
    private const int MetadataCharacters = 1_048_576;
    private readonly Process _process;
    private readonly Task<string> _errors;
    private readonly object _gate = new();
    private bool _disposed;
    private Exception? _terminationError;
    private OwnedMediaProcess(Process process)
    { _process = process; _errors = ReadTextAsync(process.StandardError, RetainedErrorCharacters, false, CancellationToken.None); }
    internal Task<string> Errors => _errors;
    internal Stream Output => _process.StandardOutput.BaseStream;
    internal static OwnedMediaProcess Start(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        var process = new Process { StartInfo = info };
        try { process.Start(); return new(process); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { process.Dispose(); throw new IOException($"无法启动 {executable}：{error.Message}", error); }
    }

    internal async Task<string> ReadMetadataAsync(CancellationToken token)
    {
        string json = await ReadTextAsync(_process.StandardOutput, MetadataCharacters, true, token).ConfigureAwait(false);
        await _process.WaitForExitAsync(token).ConfigureAwait(false);
        string errors = await _errors.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (_process.ExitCode != 0) throw new IOException($"媒体探测失败（退出码 {_process.ExitCode}）：{errors.Trim()}");
        return json;
    }

    /// <summary>Cancellation callbacks only stop this process; the owner subsequently awaits reclamation.</summary>
    internal void Terminate()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or AggregateException or NotSupportedException)
            { _terminationError = error; }
        }
    }
    internal async Task CloseAsync()
    {
        Terminate();
        try
        {
            // Kill is asynchronous. Also observe the drain before disposing its pipe.
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await _errors.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException error) { throw new IOException("媒体子进程或管道未能及时释放。", _terminationError ?? error); }
        finally
        {
            lock (_gate)
            {
                _disposed = true;
                // Process.Dispose releases process handles; explicitly close the redirected pipes we own.
                try { _process.StandardOutput.Dispose(); }
                finally
                {
                    try { _process.StandardError.Dispose(); }
                    finally { _process.Dispose(); }
                }
            }
        }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; }
        CloseAsync().GetAwaiter().GetResult();
    }

    private static async Task<string> ReadTextAsync(StreamReader reader, int retained, bool rejectOverflow, CancellationToken token)
    {
        // Do not synchronously consume a continuously ready pipe on the calling UI thread.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var text = new StringBuilder(Math.Min(retained, 4096));
        char[] buffer = new char[4096]; bool truncated = false;
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) break;
            int keep = Math.Min(read, retained - text.Length);
            text.Append(buffer, 0, keep);
            if (keep == read) continue;
            if (rejectOverflow) throw new IOException("媒体探测输出超过允许的大小。");
            truncated = true;
        }
        if (truncated) text.Append("\n（后续诊断已省略）");
        return text.ToString();
    }
}
