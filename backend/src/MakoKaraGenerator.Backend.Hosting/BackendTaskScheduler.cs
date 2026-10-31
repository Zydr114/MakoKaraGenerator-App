namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>只调度已登记Job的执行许可，不创建另一份任务状态或账本。</summary>
internal sealed class BackendTaskScheduler(BackendTaskSchedulingOptions options)
{
    internal BackendTaskSchedulingOptions Options { get; } = options;
    private readonly object _gate = new();
    private readonly List<Reservation> _queued = [];
    private readonly Dictionary<string, int> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _projects = new(StringComparer.Ordinal);
    private int _running;
    internal int Running { get { lock (_gate) return _running; } }
    internal int Queued { get { lock (_gate) return _queued.Count; } }

    internal Reservation? TryReserve(string client, string project)
    {
        lock (_gate)
        {
            Reservation reservation = new(this, client, project);
            if (CanRun(reservation)) Grant(reservation);
            else
            {
                if (_queued.Count >= Options.MaxQueued || _queued.Count(value => value.Client == client) >= Options.MaxQueuedPerClient)
                    return null;
                _queued.Add(reservation);
            }
            return reservation;
        }
    }

    private bool CanRun(Reservation value) => _running < Options.MaxRunning
        && _clients.GetValueOrDefault(value.Client) < Options.MaxRunningPerClient
        && _projects.GetValueOrDefault(value.Project) < Options.MaxRunningPerProject;
    private void Grant(Reservation value)
    {
        value.Running = true;
        _running++;
        _clients[value.Client] = _clients.GetValueOrDefault(value.Client) + 1;
        _projects[value.Project] = _projects.GetValueOrDefault(value.Project) + 1;
        value.Permit.TrySetResult();
    }
    private void Release(Reservation value)
    {
        lock (_gate)
        {
            if (value.Released) return;
            value.Released = true;
            if (value.Running)
            {
                _running--;
                RemoveCount(_clients, value.Client); RemoveCount(_projects, value.Project);
            }
            else _queued.Remove(value);
            // Skip a blocked client rather than occupying a global slot while waiting for it.
            foreach (Reservation waiting in _queued.ToArray())
            {
                if (!CanRun(waiting)) continue;
                _queued.Remove(waiting); Grant(waiting);
            }
        }
    }
    private static void RemoveCount(Dictionary<string, int> counts, string key)
    {
        if (counts[key] == 1) counts.Remove(key); else counts[key]--;
    }

    internal sealed class Reservation(BackendTaskScheduler owner, string client, string project) : IDisposable
    {
        internal string Client { get; } = client;
        internal string Project { get; } = project;
        internal bool Running;
        internal bool Released;
        internal TaskCompletionSource Permit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task WaitAsync(CancellationToken cancellationToken) => Permit.Task.WaitAsync(cancellationToken);
        public void Dispose() => owner.Release(this);
    }
}
