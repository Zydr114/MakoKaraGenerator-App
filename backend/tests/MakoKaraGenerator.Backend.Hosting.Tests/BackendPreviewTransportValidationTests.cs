using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendPreviewTransportValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 帧句柄校验绑定会话代次来源序号并检查PNG大小哈希与TTL()
    {
        PreviewSessionResponse session = Session();
        PreviewFrameRequest request = new("generation-3", "26", "180000000", "playback");
        PreviewFrameMetadataDto frame = Frame();

        Assert.Null(PreviewTransportValidation.ValidateFrame(session, request, frame, Now));
        Assert.Equal(BackendErrorCode.PreviewGenerationConflict,
            PreviewTransportValidation.ValidateFrame(session, request with { PreviewGeneration = "generation-2" }, frame, Now)!.Code);
        Assert.Equal(BackendErrorCode.PreviewGenerationConflict,
            PreviewTransportValidation.ValidateFrame(session, request, frame with { SourceRevision = "source-2" }, Now)!.Code);
        Assert.Equal(BackendErrorCode.PreviewRequestOutOfOrder,
            PreviewTransportValidation.ValidateFrame(session, request, frame with { RequestSeq = "25" }, Now)!.Code);
        Assert.Equal(BackendErrorCode.UnsupportedMediaType,
            PreviewTransportValidation.ValidateFrame(session, request, frame with { Content = frame.Content with { ContentType = "image/jpeg" } }, Now)!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest,
            PreviewTransportValidation.ValidateFrame(session, request, frame with { Content = frame.Content with { Sha256 = "bad" } }, Now)!.Code);
        Assert.Equal(BackendErrorCode.ResourceTooLarge,
            PreviewTransportValidation.ValidateFrame(session, request, frame with { Content = frame.Content with { SizeBytes = 65 } }, Now)!.Code);
        Assert.Equal(BackendErrorCode.PreviewFrameExpired,
            PreviewTransportValidation.ValidateFrame(session, request, frame with { Content = frame.Content with { ExpiresAtUtc = Now } }, Now)!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest,
            PreviewTransportValidation.ValidateFrame(session, request, frame with
            { Content = frame.Content with { ExpiresAtUtc = Now.AddSeconds(30).AddTicks(1) } }, Now)!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest,
            PreviewTransportValidation.ValidateFrame(session with { Limits = session.Limits with { MaxRawFrameBytes = 1 } },
                request, frame, Now)!.Code);
    }

    [Fact]
    public void 诊断页校验代次来源游标和页大小并把失效游标映射为稳定错误()
    {
        PreviewSessionResponse session = Session();
        PreviewDiagnosticsPageDto page = new(
            session.PreviewId, session.PreviewGeneration, session.Source.SourceRevision,
            [new BackendDiagnosticDto("MissingFont", "warning", "font missing", "line-1")],
            "cursor-2", true, "cursor-1");

        Assert.Null(PreviewTransportValidation.ValidateDiagnosticsPage(session, page, "cursor-1"));
        Assert.Equal(BackendErrorCode.DiagnosticCursorExpired,
            PreviewTransportValidation.ValidateDiagnosticsPage(session, page, "expired")!.Code);
        Assert.Equal(BackendErrorCode.PreviewGenerationConflict,
            PreviewTransportValidation.ValidateDiagnosticsPage(session, page with { PreviewGeneration = "generation-2" })!.Code);
        Assert.Equal(BackendErrorCode.DiagnosticLimitExceeded,
            PreviewTransportValidation.ValidateDiagnosticsPage(session, page with
            {
                Diagnostics = [.. Enumerable.Repeat(page.Diagnostics[0], session.Limits.MaxDiagnosticsPageSize + 1)],
            })!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest,
            PreviewTransportValidation.ValidateDiagnosticsPage(session, page with { HasMore = false })!.Code);
    }

    [Fact]
    public void 诊断来源区分旧报告实际Runtime和没有伪造发生的传输失败()
    {
        PreviewSessionResponse session = Session();
        BackendDiagnosticOccurrenceDto occurrence = Occurrence();
        BackendDiagnosticDto legacy = new("legacy", "warning", "existing report", null);
        BackendDiagnosticDto transport = new("WorkerExited", "error", "Worker transport failed", null,
            OriginKind: "transportFailure");
        BackendDiagnosticDto runtime = new("actual", "error", "actual runtime report", null, occurrence, "runtime");
        foreach (BackendDiagnosticDto item in new[] { legacy, transport, runtime })
            Assert.Null(PreviewTransportValidation.ValidateDiagnosticsPage(session, Page(item)));
        BackendDiagnosticDto[] invalid =
        [
            transport with { Occurrence = occurrence }, runtime with { Occurrence = null },
            legacy with { Occurrence = occurrence }, legacy with { OriginKind = "unknown" },
            runtime with { Occurrence = occurrence with { Epoch = Guid.Empty } },
            runtime with { Occurrence = occurrence with { Sequence = null! } },
            runtime with { Occurrence = occurrence with { Sequence = "0" } },
            runtime with { Occurrence = occurrence with { Sequence = "01" } },
            runtime with { Occurrence = occurrence with { TimeUs = "-1" } },
            runtime with { Occurrence = occurrence with { TimeUs = null! } },
            runtime with { Occurrence = occurrence with { InstanceId = null! } },
            runtime with { Occurrence = occurrence with { ArtifactHash = null! } },
            runtime with { Occurrence = occurrence with { Stage = null! } },
            runtime with { Occurrence = occurrence with { Location = new(null!, 1, 1) } },
            runtime with { Occurrence = occurrence with { Location = new("script.cs", 0, 1) } },
            runtime with { Occurrence = occurrence with { Package = new("package", null!, "export") } },
            null!,
        ];
        foreach (BackendDiagnosticDto item in invalid)
            Assert.Equal(BackendErrorCode.InvalidRequest,
                PreviewTransportValidation.ValidateDiagnosticsPage(session, Page(item))?.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest,
            PreviewTransportValidation.ValidateDiagnosticsPage(session, Page(legacy) with { Diagnostics = null! })?.Code);
        // Package revisions remain opaque; a real hash-prefixed revision is preserved, not parsed as identity.
        Assert.Null(PreviewTransportValidation.ValidateDiagnosticsPage(session,
            Page(runtime with { Occurrence = occurrence with { Package = new("package", "sha256:" + new string('a', 64), "export") } })));
    }

    [Fact]
    public void 缺失帧句柄或哈希安全返回稳定错误()
    {
        PreviewFrameRequest request = new("generation-3", "26", "180000000", "playback");
        Assert.Equal(BackendErrorCode.InvalidRequest,
            PreviewTransportValidation.ValidateFrame(Session(), request, Frame() with { Content = null! }, Now)?.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest,
            PreviewTransportValidation.ValidateFrame(Session(), request, Frame() with
            { Content = Frame().Content with { Sha256 = null! } }, Now)?.Code);
    }

    private static BackendDiagnosticOccurrenceDto Occurrence() => new(Guid.NewGuid(), "1", "actual-instance", "actual-artifact",
        "render", "123", new("logical.cs", 1, 2), new("package", "opaque-revision", "export"));
    private static PreviewDiagnosticsPageDto Page(BackendDiagnosticDto diagnostic) => new(
        "preview-1", "generation-3", "source-1", [diagnostic], null, false);

    private static PreviewSessionResponse Session() => new(
        "preview-1",
        new PreviewSourceDto("project", "project-1", "source-1"),
        "generation-3",
        new PreviewSizeDto(960, 540),
        PreviewAlphaMode.Straight,
        "ready",
        new PreviewLimitsDto(1920, 1080, 2, 4, 64, 30, 2));

    private static PreviewFrameMetadataDto Frame() => new(
        "frame-1", "preview-1", "generation-3", "source-1", "26", "180000000", 960, 540,
        PreviewAlphaMode.Straight,
        new("handle-1", "image/png", 12, new string('a', 64), Now.AddSeconds(30),
            "/api/v1/preview-frames/frame-1/content"),
        "cursor-1");
}
