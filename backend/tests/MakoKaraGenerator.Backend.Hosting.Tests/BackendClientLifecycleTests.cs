using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendClientLifecycleTests
{
    private const string Secret = "0123456789012345678901234567890123456789";

    [Fact]
    public void 令牌真实认证与心跳续期且过期租约只休眠可凭恢复身份重连()
    {
        Clock clock = new();
        ClientRegistry registry = new("host", Secret, new() { MaxClients = 1, TokenLifetimeSeconds = 2, LeaseLifetimeSeconds = 3 }, clock);
        Assert.Equal(BackendErrorCode.UnauthorizedClient, registry.Register("wrong", new()).Error!.Code);
        ClientRegistrationResponse original = registry.Register(Secret, new()).Response!;
        Assert.Null(registry.Authenticate(original.ClientId, original.AccessToken));
        Assert.Equal(BackendErrorCode.UnauthorizedClient, registry.Authenticate(original.ClientId, original.ResumeToken)!.Code);
        Assert.Equal(BackendErrorCode.UnauthorizedClient, registry.Authenticate("other", original.AccessToken)!.Code);
        clock.Advance(TimeSpan.FromSeconds(1));
        ClientHeartbeatResponse heartbeat = registry.Heartbeat(original.ClientId);
        Assert.Equal(clock.GetUtcNow().AddSeconds(2), heartbeat.TokenExpiresAtUtc);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(BackendErrorCode.ClientTokenExpired, registry.Authenticate(original.ClientId, original.AccessToken)!.Code);
        Assert.True(registry.TryReadLease(original.ClientId, out _, out bool dormant));
        Assert.True(dormant);
        Assert.Equal(BackendErrorCode.ClientQuotaExceeded, registry.Register(Secret, new()).Error!.Code);
        Assert.Equal(BackendErrorCode.UnauthorizedClient, registry.Register(Secret, new(ResumeClientId: original.ClientId, ResumeToken: "wrong")).Error!.Code);
        ClientRegistrationResponse resumed = registry.Register(Secret, new(ResumeClientId: original.ClientId, ResumeToken: original.ResumeToken)).Response!;
        Assert.Equal(original.ClientId, resumed.ClientId);
        Assert.Equal(original.ResumeToken, resumed.ResumeToken);
        Assert.NotEqual(original.AccessToken, resumed.AccessToken);
        Assert.Null(registry.Authenticate(resumed.ClientId, resumed.AccessToken));
        Assert.Equal(BackendErrorCode.UnauthorizedClient, registry.Authenticate(original.ClientId, original.AccessToken)!.Code);
        registry.TryReadLease(original.ClientId, out _, out dormant);
        Assert.False(dormant);
    }

    [Fact]
    public async Task 同client跨路径回执先于修订且并发占位一次到期不再执行()
    {
        Clock clock = new();
        ClientRequestReceiptStore receipts = new("host", new() { MaxReceiptsPerSession = 1 }, clock);
        RequestMetadataDto metadata = new("same", "host", clock.GetUtcNow().AddSeconds(3));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        async Task<BackendOperationResult<string[]>> Execute()
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task;
            return BackendOperationResult<string[]>.Success(["one"]);
        }
        Task<BackendOperationResult<string[]>> first = receipts.ExecuteAsync("client", metadata, "POST /one", new { revision = "0" }, Execute);
        await entered.Task;
        Task<BackendOperationResult<string[]>> retry = receipts.ExecuteAsync("client", metadata, "POST /one", new { revision = "0" }, Execute);
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await receipts.ExecuteAsync("client", metadata, "POST /two", new { revision = "0" }, Execute)).Error!.Code);
        Assert.Equal(BackendErrorCode.RequestQuotaExceeded, (await receipts.ExecuteAsync("client", metadata with { RequestId = "new" }, "POST /one", null, Execute)).Error!.Code);
        release.TrySetResult();
        BackendOperationResult<string[]> a = await first;
        BackendOperationResult<string[]> b = await retry;
        Assert.Equal(1, calls);
        a.Response![0] = "changed by consumer";
        Assert.Equal("one", b.Response![0]);
        Assert.Equal("one", (await receipts.ExecuteAsync("client", metadata, "POST /one", new { revision = "0" }, Execute)).Response![0]);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(BackendErrorCode.RequestExpired, (await receipts.ExecuteAsync("client", metadata, "POST /one", new { revision = "0" }, Execute)).Error!.Code);
        Assert.Equal(1, calls);
        Assert.True((await receipts.ExecuteAsync("other-client", metadata with { ValidUntilUtc = clock.GetUtcNow().AddSeconds(3) }, "POST /one", new { revision = "0" }, Execute)).Accepted);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task 接受后的错误或大结果仍归档终态且取消等待不重新执行()
    {
        Clock clock = new();
        ClientRequestReceiptStore receipts = new("host", new() { MaxReceiptBytes = 1024 }, clock);
        RequestMetadataDto metadata = new("large", "host", clock.GetUtcNow().AddSeconds(10));
        int calls = 0;
        Task<BackendOperationResult<string>> Large()
        { calls++; return Task.FromResult(BackendOperationResult<string>.Success(new string('x', 2048))); }
        Assert.Equal(BackendErrorCode.InternalError, (await receipts.ExecuteAsync("c", metadata, "post", null, Large)).Error!.Code);
        Assert.Equal(BackendErrorCode.InternalError, (await receipts.ExecuteAsync("c", metadata, "post", null, Large)).Error!.Code);
        Assert.Equal(1, calls);
        Task<BackendOperationResult<BadResult>> Bad() => Task.FromResult(BackendOperationResult<BadResult>.Success(new()));
        Task<BackendOperationResult<BadResult>> bad = receipts.ExecuteAsync("c", metadata with { RequestId = "bad" }, "post", null, Bad);
        Assert.Equal(BackendErrorCode.InternalError, (await bad.WaitAsync(TimeSpan.FromSeconds(5))).Error!.Code);
        receipts.StopAccepting();
        await receipts.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackendErrorCode.SessionClosing, (await receipts.ExecuteAsync("c", metadata with { RequestId = "stopped" }, "post", null, Large)).Error!.Code);
    }

    [Fact]
    public async Task 事件跨工程单调且有界缺口主机和客户端不匹配必须重同步()
    {
        ClientEventJournal events = new("host", 2);
        string zero = events.Cursor("client");
        Task<ClientEventsResponse> wait = events.WaitForEventsAsync("client", zero);
        string first = events.Append("client", "project.changed");
        Assert.Equal("1", Assert.Single((await wait.WaitAsync(TimeSpan.FromSeconds(5))).Events).EventSeq);
        events.Append("other", "project.changed");
        events.Append("client", "project.saved");
        string third = events.Append("client", "project.closed");
        Assert.True(events.ReadAfter("client", zero).ResyncRequired);
        Assert.Collection(events.ReadAfter("client", first).Events,
            item => Assert.Equal("2", item.EventSeq), item => Assert.Equal("3", item.EventSeq));
        Assert.Equal(third, events.ReadAfter("client", first).EventCursor);
        Assert.True(events.ReadAfter("client", events.Cursor("other")).ResyncRequired);
        Assert.True(events.ReadAfter("client", third.Replace("host:", "old-host:", StringComparison.Ordinal)).ResyncRequired);
        using CancellationTokenSource cancel = new();
        Task<ClientEventsResponse> pending = events.WaitForEventsAsync("client", third, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(events.ReadAfter("client", third).Events);
    }

    private sealed class BadResult
    {
        private readonly string _message = "injected serialization failure";
        public string Value => throw new InvalidOperationException(_message);
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
