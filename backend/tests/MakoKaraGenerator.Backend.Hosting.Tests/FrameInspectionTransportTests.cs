using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class FrameInspectionTransportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Png = PreviewPngFixture.Create();

    [Fact]
    public async Task 投影经Worker消息往返后字段不变且只能属于已请求检查的帧回执()
    {
        RenderFrameInspection sample = Sample();
        PreviewInspectionDto dto = FrameInspectionMapper.Encode(sample);
        Assert.Equal(Json(sample), Json(FrameInspectionMapper.Decode(dto)));

        StaticWorkerFrameRequest request = StaticWorkerContractTests.Request() with { Inspect = true };
        WorkerMessage result = Result(request) with { FrameInspection = dto };
        StaticRenderWorkerContract.ValidateFrameResult(request, result);
        using MemoryStream stream = new();
        await WorkerProtocol.WriteAsync(stream, result);
        stream.Position = 0;
        WorkerMessage read = (await WorkerProtocol.ReadAsync(stream))!;
        Assert.Equal(Json(sample), Json(FrameInspectionMapper.Decode(read.FrameInspection!)));

        StaticWorkerFrameRequest plain = request with { Inspect = false };
        Assert.Equal("WorkerInspectionInvalid", Assert.Throws<WorkerProtocolException>(() =>
            StaticRenderWorkerContract.ValidateFrameResult(plain, Result(plain) with { FrameInspection = dto })).Code);
        Assert.Equal("WorkerInspectionInvalid", Assert.Throws<WorkerProtocolException>(() =>
            WorkerProtocol.Validate(new WorkerMessage("result", "id", null, [], frameInspection: dto))).Code);
        Assert.Equal("WorkerInspectionInvalid", Assert.Throws<WorkerProtocolException>(() =>
            FrameInspectionMapper.Decode(dto with { Tokens = null! })).Code);
    }

    [Fact]
    public void 投影与帧同寿命并在未开启时返回不可用()
    {
        PreviewSessionResponse session = new("preview-1", new("project", "project-1", "source-1"), "generation-1",
            new(1, 1), PreviewAlphaMode.Straight, "ready", new(960, 540, 2, 2, Png.Length * 2, 30, 2));
        PreviewFrameHandleStore store = new(maxFrames: 4, maxBytes: Png.Length * 4);
        Assert.Null(store.Activate("client-a", session));
        PreviewFrameInspectionResponse inspection = new("frame-1", "preview-1", "generation-1", "source-1", "1000", 1, 1, "cpu",
            FrameInspectionMapper.ToDto(Sample()), new(4.5, 1.25, Png.Length));

        PreviewFrameMetadataDto inspected = Frame("frame-1", "1") with { InspectionUrl = "/api/v1/preview-frames/frame-1/inspection" };
        Assert.Equal(BackendErrorCode.InvalidRequest, store.Publish("client-a", session, Request("1"), inspected, Png, 0, Now)?.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest, store.Publish("client-a", session, Request("1"), inspected, Png, 0, Now,
            inspection with { TimeUs = "999" })?.Code);
        Assert.Null(store.Publish("client-a", session, Request("1"), inspected, Png, 0, Now, inspection));
        Assert.Null(store.Publish("client-a", session, Request("2"), Frame("frame-2", "2"), Png, 1, Now));

        Assert.True(store.TryReadInspection("client-a", "frame-1", Now, out PreviewFrameInspectionResponse? read, out _));
        Assert.Same(inspection, read);
        Assert.False(store.TryReadInspection("client-b", "frame-1", Now, out _, out BackendErrorResponse? owner));
        Assert.Equal(BackendErrorCode.ResourceUnauthorized, owner!.Code);
        Assert.False(store.TryReadInspection("client-a", "frame-2", Now, out _, out BackendErrorResponse? missing));
        Assert.Equal(BackendErrorCode.PreviewInspectionUnavailable, missing!.Code);
        Assert.False(store.TryReadInspection("client-a", "frame-1", Now.AddSeconds(30), out _, out BackendErrorResponse? expired));
        Assert.Equal(BackendErrorCode.PreviewFrameExpired, expired!.Code);
        Assert.False(store.TryReadInspection("client-a", "frame-1", Now.AddSeconds(30), out _, out BackendErrorResponse? gone));
        Assert.Equal(BackendErrorCode.PreviewFrameNotFound, gone!.Code);
    }

    internal static RenderFrameInspection Sample() => new(new(1920, 1080), true,
        [new("token-1", "binding-1", "line-1", false, "ClusterSpansElements", [0, 0, 10, 0, 10, 10, 0, 10],
            [new(0, ["token-1", "token-2"], false, "ClusterSpansElements", [0, 0, 5, 0, 5, 10, 0, 10])], [new("ruby-1", null)], 2)],
        [new("line-1", "binding-1", [0, 0, 100, 0, 100, 20, 0, 20])],
        [new("line-1", "binding-1", "token-1", 0.5, "fromToken", [5, 5], [0, 5], [10, 5])],
        [new("cursor", 2, 1, 3, [1, 2, 3, 4], 1)],
        [new("cursor", "pkg", "rev", "main", 2, "lines", ["line-1"], "ABCDEF0123456789", 0)],
        new(12.5, 3, 1.5, 0, 0, 0.25, 4, 0, 0.5, 3, 2048, 4096, 0, 0, 0,
            [new("ABCDEF0123456789", "Advance", 2, 1.5, 1)], 0, new(1, 2, 0, 3, 1), 40.125),
        new(0, 0, 0, 0, 1));

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, RenderFrameInspection.JsonOptions);

    private static WorkerMessage Result(StaticWorkerFrameRequest request) => new(StaticRenderWorkerContract.FrameResultKind,
        "frame", WorkerProtocol.ComputeSha256(StaticRenderWorkerContract.EncodeFrame(request)), [3, 2, 1, 255],
        outputSha256: WorkerProtocol.ComputeSha256([3, 2, 1, 255]), frameWidth: 1, frameHeight: 1, frameStride: 4,
        frameTimeUs: 42, frameFormat: RenderFrameV1WorkerResult.RequiredPixelFormat, sessionId: request.SessionId.ToString("N"),
        sourceSha256: request.SourceSha256, frameSequence: 1, frameTransparentBackground: false, diagnostics: new([], false),
        frameQuality: request.Quality);

    private static PreviewFrameRequest Request(string sequence) => new("generation-1", sequence, "1000", "seek");

    private static PreviewFrameMetadataDto Frame(string frameId, string sequence) => new(
        frameId, "preview-1", "generation-1", "source-1", sequence, "1000", 1, 1, PreviewAlphaMode.Straight,
        new("handle-" + frameId, "image/png", Png.Length, Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant(),
            Now.AddSeconds(30), "/api/v1/preview-frames/" + frameId + "/content"));
}
