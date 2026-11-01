namespace MakoKaraGenerator.Backend.Server;

internal static class ServerShutdown
{
    internal static async Task<bool> DisposeAsync(Func<ValueTask> previews, Func<ValueTask> sessions,
        Action renderBackends, TextWriter errors)
    {
        bool succeeded = true;
        async Task RunAsync(string stage, Func<ValueTask> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception error)
            {
                succeeded = false;
                await errors.WriteLineAsync($"BACKEND_SHUTDOWN_FAILED {stage} {error.GetType().Name}").ConfigureAwait(false);
            }
        }

        await RunAsync("preview", previews).ConfigureAwait(false);
        await RunAsync("sessions", sessions).ConfigureAwait(false);
        await RunAsync("render-backends", () => { renderBackends(); return ValueTask.CompletedTask; }).ConfigureAwait(false);
        return succeeded;
    }
}
