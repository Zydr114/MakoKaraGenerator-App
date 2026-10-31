using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class PreviewFrameContractValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    [Fact]
    public void 合法PNG帧通过并核对大小和哈希()
    {
        byte[] content = PreviewPngFixture.Create();
        PreviewFrameMetadataDto metadata = Frame(content, Now.AddSeconds(30));

        BackendErrorResponse? result = PreviewFrameContract.Validate(metadata, content, Limits(),
            "preview-1", "generation-1", "source-1", 0, Now);

        Assert.Null(result);
    }

    [Fact]
    public void 过期句柄和旧代次在内容校验前被拒绝()
    {
        byte[] content = PreviewPngFixture.Create();
        PreviewFrameMetadataDto expired = Frame(content, Now.AddSeconds(-1));
        BackendErrorResponse? expiry = PreviewFrameContract.Validate(expired, content, Limits(),
            "preview-1", "generation-1", "source-1", 0, Now);
        Assert.Equal(BackendErrorCode.PreviewFrameExpired, expiry?.Code);

        PreviewFrameMetadataDto old = Frame(content, Now.AddSeconds(30)) with { PreviewGeneration = "generation-0" };
        BackendErrorResponse? generation = PreviewFrameContract.Validate(old, content, Limits(),
            "preview-1", "generation-1", "source-1", 0, Now);
        Assert.Equal(BackendErrorCode.PreviewGenerationConflict, generation?.Code);
    }

    [Fact]
    public void 序号倒退和哈希不一致拒绝帧()
    {
        byte[] content = PreviewPngFixture.Create();
        PreviewFrameMetadataDto metadata = Frame(content, Now.AddSeconds(30)) with
        { RequestSeq = "4" };
        BackendErrorResponse? sequence = PreviewFrameContract.Validate(metadata, content, Limits(),
            "preview-1", "generation-1", "source-1", 4, Now);
        Assert.Equal(BackendErrorCode.PreviewRequestOutOfOrder, sequence?.Code);

        PreviewFrameMetadataDto badHash = Frame(content, Now.AddSeconds(30)) with
        { Content = new("handle-1", "image/png", content.Length, new string('0', 64), Now.AddSeconds(30), "/frames/1") };
        BackendErrorResponse? hash = PreviewFrameContract.Validate(badHash, content, Limits(),
            "preview-1", "generation-1", "source-1", 0, Now);
        Assert.Equal(BackendErrorCode.PreviewFrameNotFound, hash?.Code);
    }

    [Fact]
    public void PNG拒绝只有签名缺尾损坏CRC或与元数据不同的IHDR尺寸()
    {
        byte[] png = PreviewPngFixture.Create();
        byte[] corruptedCrc = [.. png];
        corruptedCrc[32] ^= 1;
        byte[][] invalid = [[137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4], png[..^12], corruptedCrc, [.. png, 0]];
        foreach (byte[] content in invalid)
            Assert.Equal(BackendErrorCode.InvalidRequest,
                PreviewFrameContract.Validate(Frame(content, Now.AddSeconds(30)), content, Limits(),
                    "preview-1", "generation-1", "source-1", 0, Now)?.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest,
            PreviewFrameContract.Validate(Frame(png, Now.AddSeconds(30)) with { Width = 2 }, png, Limits(),
                "preview-1", "generation-1", "source-1", 0, Now)?.Code);
    }

    [Fact]
    public void 租约严格限制公布TTL且边界值有效()
    {
        byte[] content = PreviewPngFixture.Create();
        Assert.Null(PreviewFrameContract.Validate(Frame(content, Now.AddSeconds(30)), content, Limits(),
            "preview-1", "generation-1", "source-1", 0, Now));
        Assert.Equal(BackendErrorCode.InvalidRequest,
            PreviewFrameContract.Validate(Frame(content, Now.AddSeconds(30).AddTicks(1)), content, Limits(),
                "preview-1", "generation-1", "source-1", 0, Now)?.Code);
    }

    [Fact]
    public void 压缩PNG很小时原始像素仍必须满足公布预算()
    {
        byte[] content = PreviewPngFixture.Create();
        PreviewFrameMetadataDto frame = Frame(content, Now.AddSeconds(30));
        Assert.Null(PreviewFrameContract.Validate(frame, content, Limits() with { MaxRawFrameBytes = 4 },
            "preview-1", "generation-1", "source-1", 0, Now));
        Assert.Equal(BackendErrorCode.InvalidRequest,
            PreviewFrameContract.Validate(frame, content, Limits() with { MaxRawFrameBytes = 3 },
                "preview-1", "generation-1", "source-1", 0, Now)?.Code);
    }

    private static PreviewFrameMetadataDto Frame(byte[] content, DateTimeOffset expiresAt) => new(
        "frame-1", "preview-1", "generation-1", "source-1", "1", "1000", 1, 1,
        PreviewAlphaMode.Straight,
        new("handle-1", "image/png", content.Length, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), expiresAt, "/frames/1"));

    private static PreviewLimitsDto Limits() => new(1920, 1080, 1, 8, PreviewFrameContract.MaxFrameBytes, 30, 100);
}
