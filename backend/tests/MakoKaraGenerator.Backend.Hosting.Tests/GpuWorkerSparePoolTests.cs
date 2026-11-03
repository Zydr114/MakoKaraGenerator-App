using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class GpuWorkerSparePoolTests
{
    [Fact]
    public async Task TwoActiveAndReplenishAsync()
    {
        var f = new Fixture(); await using var pool = f.Create();
        await Until(() => pool.Read().Spare == 1);
        var first = await pool.AcquireAsync(); await Until(() => pool.Read().Spare == 1);
        Check(first.Admission == "prepared-spare-hit", "ready spare hit is classified inside admission lock");
        var second = await pool.AcquireAsync();
        Check(first.Worker.Id != second.Worker.Id && pool.Read() is { Bound: 2, Total: 2, Spare: 0 }, "two active workers");
        await Throws(() => pool.AcquireAsync(), "third worker denied");
        await first.DisposeAsync(); await Until(() => pool.Read().Spare == 1);
        var replacement = await pool.AcquireAsync();
        Check(replacement.Worker.Id != first.Worker.Id && replacement.Worker.Id != second.Worker.Id, "used worker never recycled");
        await replacement.DisposeAsync(); await second.DisposeAsync();
        Check(f.MaximumLive <= 2, "hard two-worker process budget");
    }

    [Fact]
    public async Task ExpiryAndInvalidationAsync()
    {
        var f = new Fixture(); await using var pool = f.Create();
        await Until(() => pool.Read().Spare == 1);
        var initial = f.Workers.Single(); f.Time.Advance(TimeSpan.FromSeconds(31));
        var afterTtl = await pool.AcquireAsync();
        Check(!initial.Alive && afterTtl.Worker.Id != initial.Id, "TTL drains before binding");
        await Until(() => pool.Read().Spare == 1);
        var obsolete = f.Workers.Last();
        await pool.InvalidateAsync("deployment-B"); await Until(() => pool.Read().Spare == 1);
        var next = await pool.AcquireAsync();
        Check(!obsolete.Alive && next.Worker.Id != obsolete.Id && pool.Read().ConfigurationIdentity == "deployment-B", "configuration invalidates spare");
        Check(afterTtl.Worker.Alive, "existing active worker not silently recycled by invalidation");
        await next.DisposeAsync(); await afterTtl.DisposeAsync();
    }

    [Fact]
    public async Task BackoffAsync()
    {
        var f = new Fixture { WarmFailures = 2 }; await using var pool = f.Create();
        await Until(() => pool.Read() is { BackingOff: true, Total: 0 });
        Check(pool.Read().ConsecutiveWarmupFailures == 1, "first warmup failure recorded");
        await Throws(() => pool.AcquireAsync(), "initial retry delayed");
        f.Time.Advance(TimeSpan.FromMilliseconds(251));
        await Throws(() => pool.AcquireAsync(), "second warmup attempt fails");
        await Until(() => pool.Read() is { BackingOff: true, Total: 0 });
        Check(pool.Read().ConsecutiveWarmupFailures == 2, "consecutive warmup failures accumulated");
        f.Time.Advance(TimeSpan.FromMilliseconds(251));
        await Throws(() => pool.AcquireAsync(), "second backoff doubles to 500ms");
        f.Time.Advance(TimeSpan.FromMilliseconds(251));
        var good = await pool.AcquireAsync();
        Check(pool.Read().ConsecutiveWarmupFailures == 0 && good.Worker.Alive, "success resets retry state");
        await good.DisposeAsync();
    }

    [Fact]
    public async Task QuarantineAndDrainAsync()
    {
        var f = new Fixture(); var pool = f.Create();
        var first = await pool.AcquireAsync(); await Until(() => pool.Read().Spare == 1);
        first.Worker.StopFails = true;
        await Throws(() => first.DisposeAsync().AsTask(), "unconfirmed exit quarantined");
        Check(pool.Read() is { Total: 2, Quarantined: 1 }, "unknown exit retains quota");
        var second = await pool.AcquireAsync();
        await Throws(() => pool.AcquireAsync(), "quarantine plus second active exhausts quota");
        await Throws(() => pool.DisposeAsync().AsTask(), "shutdown reports unknown drain");
        Check(!second.Worker.Alive && first.Worker.Alive && pool.Read().Total == 1, "shutdown drains known workers and retains failed ownership");
        first.Worker.StopFails = false;
        // Production callers only know IAsyncDisposable; the second dispose must
        // reach the quarantined ownership rather than replay the cached failure.
        await pool.DisposeAsync();
        Check(pool.Read().Total == 0 && f.Workers.All(w => !w.Alive), "retry confirms all owned objects terminated");
        Check(f.MaximumLive <= 2, "quarantine cannot free process capacity prematurely");
    }

    [Fact]
    public async Task OpeningCancellationAsync()
    {
        var f = new Fixture { HoldWarmup = true }; await using var pool = f.Create();
        using var cancellation = new CancellationTokenSource();
        var pending = pool.AcquireAsync(cancellation.Token);
        await Until(() => f.Workers.Count >= 1); cancellation.Cancel();
        await Throws(() => pending, "cancelled opening is reclaimed");
        await Until(() => pool.Read().Bound == 0);
        Check(f.MaximumLive <= 2 && f.Workers[0].StopCount == 1, "opening cancellation retains budget until owned stop");
    }

    [Fact]
    public async Task ExistingDeviceInspectionAsync()
    {
        var f = new Fixture(); await using var pool = f.Create();
        var first = await pool.AcquireAsync(); await Until(() => pool.Read().Spare == 1);
        var second = await pool.AcquireAsync();
        int workers = f.Workers.Count;
        int inspected = await pool.InspectReadyAsync((worker, _) => Task.FromResult(worker.Id));
        Check((inspected == first.Worker.Id || inspected == second.Worker.Id) &&
            workers == f.Workers.Count && pool.Read() is { Bound: 2, Spare: 0, Total: 2 },
            "availability inspects existing bound device without a third worker or binding");
        await first.DisposeAsync(); await second.DisposeAsync();
    }

    [Fact]
    public async Task StaleDeviceInspectionAsync()
    {
        var f = new Fixture(); await using var pool = f.Create();
        var first = await pool.AcquireAsync(); await Until(() => pool.Read().Spare == 1);
        var second = await pool.AcquireAsync();
        first.Worker.Alive = false;
        int inspected = await pool.InspectReadyAsync((worker, _) => Task.FromResult(worker.Id));
        Check(inspected == second.Worker.Id, "dead process cannot provide availability proof");
        await pool.InvalidateAsync("deployment-B");
        int calls = 0;
        await Throws(() => pool.InspectReadyAsync((worker, _) => { calls++; return Task.FromResult(worker.Id); }),
            "old configuration bound workers cannot prove current availability");
        Check(calls == 0 && f.Workers.Count == 2, "configuration mismatch neither queries nor starts helper");
        await first.DisposeAsync(); await second.DisposeAsync();
    }

    [Fact]
    public async Task OpeningInspectionCancellationAsync()
    {
        var f = new Fixture { HoldWarmup = true }; await using var pool = f.Create();
        using var cancellation = new CancellationTokenSource();
        int workers = f.Workers.Count, calls = 0;
        var pending = pool.InspectReadyAsync((worker, _) => { calls++; return Task.FromResult(worker.Id); }, cancellation.Token);
        cancellation.Cancel();
        await Throws(() => pending, "availability waiting for existing opening is cancellable");
        Check(calls == 0 && f.Workers.Count == workers && pool.Read().Total == 1,
            "opening device is not reported as ready and no helper starts");
    }

    [Fact]
    public async Task ConcurrentInvalidationRejectsInFlightProofAsync()
    {
        var f = new Fixture(); await using var pool = f.Create();
        var first = await pool.AcquireAsync(); await Until(() => pool.Read().Spare == 1);
        var second = await pool.AcquireAsync();
        var queried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var pending = pool.InspectReadyAsync(async (worker, _) =>
        { calls++; queried.TrySetResult(); return await answer.Task; });
        await queried.Task;
        await pool.InvalidateAsync("deployment-B");
        answer.SetResult(first.Worker.Id);
        await Throws(() => pending, "in-flight device proof invalidated before return");
        Check(calls == 1 && f.Workers.Count == 2, "remaining stale candidate skipped with no helper process");
        await first.DisposeAsync(); await second.DisposeAsync();
    }

    [Fact]
    public async Task QuarantineCannotProvideDeviceProofAsync()
    {
        var f = new Fixture(); var pool = f.Create();
        var first = await pool.AcquireAsync(); await Until(() => pool.Read().Spare == 1);
        var second = await pool.AcquireAsync();
        first.Worker.StopFails = true;
        await Throws(() => first.DisposeAsync().AsTask(), "failed exact stop quarantined");
        int inspected = await pool.InspectReadyAsync((worker, _) => Task.FromResult(worker.Id));
        Check(inspected == second.Worker.Id && pool.Read() is { Total: 2, Quarantined: 1 },
            "live but quarantined process cannot provide device proof");
        first.Worker.StopFails = false;
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task OpeningWaitIsNeverClassifiedAsPreparedHitAsync()
    {
        var f = new Fixture { WarmupRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var pool = f.Create();
        var pending = pool.AcquireAsync();
        f.WarmupRelease.SetResult();
        var lease = await pending;
        Check(lease.Admission == "opening-spare-wait", "unfinished preparation records exact miss before awaiting readiness");
        await lease.DisposeAsync();
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Throws(Func<Task> action, string message)
    { try { await action(); } catch { return; } throw new InvalidOperationException("Expected failure: " + message); }
    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(5); }
    }

    private sealed class FakeTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
    }
    private sealed class Worker(int id)
    { internal readonly int Id = id; internal bool Alive = true, StopFails; internal int StopCount; }
    private sealed class Fixture
    {
        internal readonly FakeTime Time = new();
        internal readonly List<Worker> Workers = [];
        internal int MaximumLive, WarmFailures;
        internal bool HoldWarmup;
        internal TaskCompletionSource? WarmupRelease;
        internal GpuWorkerSparePool<Worker> Create() => new("deployment-A", () =>
        {
            lock (Workers)
            {
                var worker = new Worker(Workers.Count + 1); Workers.Add(worker);
                MaximumLive = Math.Max(MaximumLive, Workers.Count(w => w.Alive)); return worker;
            }
        }, async (_, token) =>
        {
            if (HoldWarmup) await Task.Delay(Timeout.Infinite, token);
            if (WarmupRelease is not null) await WarmupRelease.Task.WaitAsync(token);
            if (Interlocked.Decrement(ref WarmFailures) >= 0) throw new IOException("warmup failed");
        }, worker =>
        {
            worker.StopCount++;
            if (worker.StopFails) throw new IOException("exact owned exit unconfirmed");
            worker.Alive = false; return Task.CompletedTask;
        }, worker => worker.Alive, timeProvider: Time);
    }
}
