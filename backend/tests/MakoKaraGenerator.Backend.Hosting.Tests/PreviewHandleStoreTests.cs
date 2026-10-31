using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class PreviewHandleStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Png = PreviewPngFixture.Create();

    [Fact]
    public void 发布只接受真实PNG并按会话代次来源序号和TTL绑定读取()
    {
        PreviewSessionResponse session = Session();
        PreviewFrameRequest request = new("generation-1", "1", "1000", "seek");
        PreviewFrameMetadataDto frame = Frame("frame-1", Png, Now.AddSeconds(30));
        PreviewFrameHandleStore store = new(maxFrames: 2, maxBytes: Png.Length * 2);
        Assert.Null(store.Activate("client-a", session));

        Assert.Null(store.Publish("client-a", session, request, frame, Png, 0, Now));
        Assert.True(store.TryRead("client-a", "frame-1", Now, out PreviewFrameReadResult? read, out _));
        Assert.Equal(Png, read!.Content);
        Assert.False(store.TryRead("client-b", "frame-1", Now, out _, out BackendErrorResponse? ownerError));
        Assert.Equal(BackendErrorCode.ResourceUnauthorized, ownerError!.Code);
        Assert.False(store.TryRead("client-a", "frame-1", Now.AddSeconds(30), out _, out BackendErrorResponse? expiry));
        Assert.Equal(BackendErrorCode.PreviewFrameExpired, expiry!.Code);

        PreviewFrameMetadataDto fake = Frame("frame-fake", [1, 2, 3], Now.AddSeconds(30)) with { RequestSeq = "2" };
        Assert.Equal(BackendErrorCode.InvalidRequest,
            store.Publish("client-a", session, request with { RequestSeq = "2" }, fake, [1, 2, 3], 1, Now)?.Code);
    }

    [Fact]
    public void 发布拒绝旧代次倒退序号错误哈希和保留配额超限()
    {
        PreviewSessionResponse session = Session() with
        {
            Limits = new PreviewLimitsDto(960, 540, 2, 2, Png.Length * 2, 30, 2),
        };
        PreviewFrameHandleStore store = new(maxFrames: 1, maxBytes: Png.Length * 2);
        PreviewFrameRequest request = new("generation-1", "1", "1000", "seek");
        PreviewFrameMetadataDto frame = Frame("frame-1", Png, Now.AddSeconds(30));
        Assert.Null(store.Activate("client-a", session));

        Assert.Equal(BackendErrorCode.PreviewGenerationConflict,
            store.Publish("client-a", session, request with { PreviewGeneration = "old" }, frame, Png, 0, Now)?.Code);
        Assert.Equal(BackendErrorCode.PreviewRequestOutOfOrder,
            store.Publish("client-a", session, request, frame with { RequestSeq = "0" }, Png, 0, Now)?.Code);
        Assert.Equal(BackendErrorCode.PreviewFrameNotFound,
            store.Publish("client-a", session, request, frame with
            {
                Content = frame.Content with { Sha256 = new string('0', 64) },
            }, Png, 0, Now)?.Code);
        Assert.Null(store.Publish("client-a", session, request, frame, Png, 0, Now));
        Assert.Equal(BackendErrorCode.RequestQuotaExceeded,
            store.Publish("client-a", session, request with { RequestSeq = "2" }, Frame("frame-2", Png, Now.AddSeconds(30)) with { RequestSeq = "2" }, Png, 1, Now)?.Code);
    }

    [Fact]
    public void 诊断页按客户端代次和游标读取并拒绝失效游标及过大页()
    {
        PreviewSessionResponse session = Session();
        PreviewDiagnosticsStore store = new();
        PreviewDiagnosticsPageDto page = new(session.PreviewId, session.PreviewGeneration,
            session.Source.SourceRevision, [new("font", "warning", "missing", "line-1")], "next", true, null);
        Assert.Null(store.Activate("client-a", session));

        Assert.Null(store.Publish("client-a", session, page));
        Assert.True(store.TryRead("client-a", session.PreviewId, null, 1, out PreviewDiagnosticsPageDto? read, out _));
        Assert.Equal("next", read!.NextCursor);
        Assert.False(store.TryRead("client-a", session.PreviewId, "expired", null, out _, out BackendErrorResponse? cursor));
        Assert.Equal(BackendErrorCode.DiagnosticCursorExpired, cursor!.Code);
        Assert.False(store.TryRead("client-b", session.PreviewId, null, null, out _, out BackendErrorResponse? owner));
        Assert.Equal(BackendErrorCode.ResourceUnauthorized, owner!.Code);
        Assert.False(store.TryRead("client-a", session.PreviewId, null, 3, out _, out BackendErrorResponse? limit));
        Assert.Equal(BackendErrorCode.DiagnosticLimitExceeded, limit!.Code);
    }

    [Fact]
    public void 同会话连续播放按公布配额替换旧帧而全局配额保持拒绝()
    {
        PreviewSessionResponse session = Session() with { Limits = Session().Limits with { MaxRetainedFrames = 1 } };
        PreviewFrameHandleStore store = new(maxFrames: 1, maxBytes: Png.Length);
        Assert.Null(store.Activate("client-a", session));
        Assert.Null(Publish(store, session, "frame-1", 1));
        Assert.Null(Publish(store, session, "frame-2", 2));
        Assert.Equal(1, store.Count);
        Assert.False(store.TryRead("client-a", "frame-1", Now, out _, out BackendErrorResponse? missing));
        Assert.Equal(BackendErrorCode.PreviewFrameNotFound, missing!.Code);
        Assert.True(store.TryRead("client-a", "frame-2", Now, out _, out _));

        PreviewSessionResponse other = session with { PreviewId = "preview-2" };
        Assert.Null(store.Activate("client-a", other));
        Assert.Equal(BackendErrorCode.RequestQuotaExceeded, Publish(store, other, "frame-3", 1)?.Code);
        Assert.True(store.TryRead("client-a", "frame-2", Now, out _, out _));
    }

    [Fact]
    public void 读取租约禁止写入并限制并发与字节且拒绝发布保留旧帧()
    {
        PreviewSessionResponse session = Session() with { Limits = Session().Limits with { MaxRetainedFrames = 1 } };
        PreviewFrameHandleStore store = new(maxBytes: Png.Length, maxReaders: 1, maxReadBytes: Png.Length);
        Assert.Null(store.Activate("client-a", session));
        Assert.Null(Publish(store, session, "frame-1", 1));
        Assert.True(store.TryAcquireRead("client-a", "frame-1", Now, out PreviewFrameReadLease? lease, out _));
        Assert.False(lease!.Stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => lease.Stream.WriteByte(0));
        Assert.Equal(1, store.ActiveReaders);
        Assert.Equal(Png.Length, store.ActiveReadBytes);
        Assert.False(store.TryAcquireRead("client-a", "frame-1", Now, out _, out BackendErrorResponse? count));
        Assert.Equal(BackendErrorCode.RequestQuotaExceeded, count!.Code);
        Assert.Equal(BackendErrorCode.ResourceQuotaExceeded, Publish(store, session, "frame-2", 2)?.Code);
        Assert.Equal(1, store.Count); // Rejected replacement did not discard the readable previous frame.
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0, store.ActiveReaders);
        Assert.Equal(0, store.ActiveReadBytes);
        Assert.Null(Publish(store, session, "frame-2", 2));

        PreviewFrameHandleStore bytesStore = new(maxReaders: 2, maxReadBytes: Png.Length);
        Assert.Null(bytesStore.Activate("client-a", Session()));
        Assert.Null(Publish(bytesStore, Session(), "frame-1", 1));
        Assert.True(bytesStore.TryAcquireRead("client-a", "frame-1", Now, out PreviewFrameReadLease? first, out _));
        using (first)
        {
            Assert.False(bytesStore.TryAcquireRead("client-a", "frame-1", Now, out _, out BackendErrorResponse? bytes));
            Assert.Equal(BackendErrorCode.ResourceQuotaExceeded, bytes!.Code);
        }
        Assert.Equal(0, bytesStore.ActiveReaders);
    }

    [Fact]
    public void 过期或换代次关闭后已读租约保留预算而迟到发布不能复活()
    {
        PreviewSessionResponse original = Session();
        PreviewFrameHandleStore store = new(maxBytes: Png.Length);
        Assert.Null(store.Activate("client-a", original));
        Assert.Null(Publish(store, original, "frame-1", 1));
        Assert.True(store.TryAcquireRead("client-a", "frame-1", Now, out PreviewFrameReadLease? lease, out _));
        Assert.False(store.TryRead("client-a", "frame-1", Now.AddSeconds(30), out _, out BackendErrorResponse? expiry));
        Assert.Equal(BackendErrorCode.PreviewFrameExpired, expiry!.Code);
        Assert.Equal(0, store.Count);
        Assert.Equal(Png.Length, store.RetainedBytes);
        PreviewSessionResponse current = original with { PreviewGeneration = "opaque-current" };
        Assert.Null(store.Activate("client-a", current));
        Assert.Equal(BackendErrorCode.PreviewGenerationConflict, Publish(store, original, "frame-old", 2)?.Code);
        Assert.Equal(BackendErrorCode.ResourceQuotaExceeded, Publish(store, current, "frame-new", 1)?.Code);
        byte[] observed = new byte[Png.Length];
        lease!.Stream.ReadExactly(observed);
        Assert.Equal(Png, observed);
        lease.Dispose();
        Assert.Equal(0, store.RetainedBytes);
        Assert.Null(Publish(store, current, "frame-new", 1));
        Assert.Null(store.Close("client-a", current.PreviewId));
        Assert.Equal(BackendErrorCode.PreviewSessionNotFound, Publish(store, current, "frame-late", 2)?.Code);
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.RetainedBytes);
    }

    [Fact]
    public async Task 并发租约竞争只接受公布数量且换代次清旧索引后释放真实预算()
    {
        PreviewSessionResponse session = Session();
        PreviewFrameHandleStore store = new(maxBytes: Png.Length, maxReaders: 2, maxReadBytes: Png.Length * 2);
        Assert.Null(store.Activate("client-a", session));
        Assert.Null(Publish(store, session, "frame-1", 1));
        TaskCompletionSource attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        Task<bool>[] readers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            bool acquired = store.TryAcquireRead("client-a", "frame-1", Now, out PreviewFrameReadLease? lease, out BackendErrorResponse? error);
            try
            {
                if (!acquired) Assert.Equal(BackendErrorCode.RequestQuotaExceeded, error!.Code);
                if (Interlocked.Increment(ref attempts) == 8) attempted.TrySetResult();
                if (acquired) await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return acquired;
            }
            finally { lease?.Dispose(); }
        })).ToArray();
        try
        {
            await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, store.ActiveReaders);
            Assert.Equal(Png.Length * 2, store.ActiveReadBytes);
            PreviewSessionResponse current = session with { PreviewGeneration = "next-opaque" };
            Assert.Null(store.Activate("client-a", current));
            Assert.Equal(0, store.Count);
            Assert.Equal(Png.Length, store.RetainedBytes); // Shared content is counted once, even with two readers.
            Assert.False(store.TryAcquireRead("client-a", "frame-1", Now, out _, out BackendErrorResponse? stale));
            Assert.Equal(BackendErrorCode.PreviewFrameNotFound, stale!.Code);
        }
        finally { release.TrySetResult(); }
        bool[] accepted = await Task.WhenAll(readers);
        Assert.Equal(2, accepted.Count(success => success));
        Assert.Equal(0, store.ActiveReaders);
        Assert.Equal(0, store.ActiveReadBytes);
        Assert.Equal(0, store.RetainedBytes);
    }

    [Fact]
    public void 诊断会话全局有界且拒绝数组别名小limit过期代次和关闭复活()
    {
        PreviewDiagnosticsStore store = new(maxSessions: 1);
        PreviewSessionResponse original = Session();
        PreviewDiagnosticsPageDto page = Page(original, null, null, "one", "two");
        Assert.Equal(BackendErrorCode.PreviewSessionNotFound, store.Publish("client-a", original, page)?.Code);
        Assert.Null(store.Activate("client-a", original));
        Assert.Equal(BackendErrorCode.SessionQuotaExceeded,
            store.Activate("client-a", original with { PreviewId = "other" })?.Code);
        Assert.Equal(1, store.Count);
        Assert.Null(store.Publish("client-a", original, page));
        page.Diagnostics[0] = new("modified", "error", "alias", null);
        Assert.False(store.TryRead("client-a", original.PreviewId, null, 1, out _, out BackendErrorResponse? small));
        Assert.Equal(BackendErrorCode.DiagnosticLimitExceeded, small!.Code);
        Assert.True(store.TryRead("client-a", original.PreviewId, null, 2, out PreviewDiagnosticsPageDto? read, out _));
        Assert.Equal("one", read!.Diagnostics[0].Code);
        read.Diagnostics[0] = page.Diagnostics[0];
        Assert.True(store.TryRead("client-a", original.PreviewId, null, 2, out read, out _));
        Assert.Equal("one", read!.Diagnostics[0].Code);

        PreviewSessionResponse current = original with { PreviewGeneration = "opaque-new" };
        Assert.Null(store.Activate("client-a", current));
        Assert.Equal(BackendErrorCode.PreviewGenerationConflict, store.Publish("client-a", original, page)?.Code);
        Assert.Null(store.Publish("client-a", current, Page(current, null, null, "current")));
        Assert.True(store.TryRead("client-a", current.PreviewId, null, 2, out read, out _));
        Assert.Equal("current", read!.Diagnostics[0].Code);
        Assert.Null(store.Close("client-a", current.PreviewId));
        Assert.Equal(BackendErrorCode.PreviewSessionNotFound,
            store.Publish("client-a", current, Page(current, null, null, "late"))?.Code);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void 原子诊断快照替换清旧游标且拒绝坏链时保留完整旧快照()
    {
        PreviewSessionResponse session = Session();
        PreviewDiagnosticsStore store = new();
        Assert.Null(store.Activate("client-a", session));
        Assert.Null(store.PublishBatch("client-a", session,
            [Page(session, null, "old-next", "first"), Page(session, "old-next", null, "last")]));
        Assert.Equal(BackendErrorCode.InvalidRequest,
            store.PublishBatch("client-a", session, [Page(session, null, "missing", "invalid")])?.Code);
        Assert.True(store.TryRead("client-a", session.PreviewId, "old-next", null, out PreviewDiagnosticsPageDto? old, out _));
        Assert.Equal("last", old!.Diagnostics[0].Code);
        Assert.Null(store.PublishBatch("client-a", session, [Page(session, null, null, "replacement")]));
        Assert.False(store.TryRead("client-a", session.PreviewId, "old-next", null, out _, out BackendErrorResponse? stale));
        Assert.Equal(BackendErrorCode.DiagnosticCursorExpired, stale!.Code);
        Assert.True(store.TryRead("client-a", session.PreviewId, null, null, out PreviewDiagnosticsPageDto? fresh, out _));
        Assert.Equal("replacement", fresh!.Diagnostics[0].Code);
    }

    private static PreviewDiagnosticsPageDto Page(PreviewSessionResponse session, string? cursor,
        string? nextCursor, params string[] codes) => new(session.PreviewId, session.PreviewGeneration,
        session.Source.SourceRevision, codes.Select(code => new BackendDiagnosticDto(code, "warning", code, null)).ToArray(),
        nextCursor, nextCursor is not null, cursor);

    private static BackendErrorResponse? Publish(PreviewFrameHandleStore store, PreviewSessionResponse session, string id, long seq)
    {
        string sequence = seq.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return store.Publish("client-a", session, new(session.PreviewGeneration, sequence, "1000", "seek"),
            Frame(id, Png, Now.AddSeconds(30)) with { PreviewId = session.PreviewId,
                PreviewGeneration = session.PreviewGeneration, SourceRevision = session.Source.SourceRevision, RequestSeq = sequence },
            Png, seq - 1, Now);
    }

    private static PreviewSessionResponse Session() => new(
        "preview-1", new("project", "project-1", "source-1"), "generation-1",
        new(1, 1), PreviewAlphaMode.Straight, "ready", new(960, 540, 2, 2, Png.Length * 2, 30, 2));

    private static PreviewFrameMetadataDto Frame(string frameId, byte[] content, DateTimeOffset expiresAt) => new(
        frameId, "preview-1", "generation-1", "source-1", "1", "1000", 1, 1,
        PreviewAlphaMode.Straight,
        new("handle-" + frameId, "image/png", content.Length,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), expiresAt,
            "/api/v1/preview-frames/" + frameId + "/content"));
}
