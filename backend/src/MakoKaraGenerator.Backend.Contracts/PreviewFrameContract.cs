using System.Security.Cryptography;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>验证已发布预览帧的固定元数据、租约和二进制内容。</summary>
public static class PreviewFrameContract
{
    /// <summary>首版无损帧发布允许的最大字节数。</summary>
    public const long MaxFrameBytes = 64L * 1024 * 1024;

    /// <summary>
    /// 在发布前验证帧是否仍属于指定预览输入，并且句柄、租约和内容哈希一致。
    /// 返回 null 表示通过；返回值可直接映射为稳定的 problem 响应。
    /// </summary>
    public static BackendErrorResponse? Validate(
        PreviewFrameMetadataDto metadata,
        ReadOnlySpan<byte> content,
        PreviewLimitsDto limits,
        string expectedPreviewId,
        string expectedPreviewGeneration,
        string expectedSourceRevision,
        long lastRequestSequence,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(limits);
        if (string.IsNullOrWhiteSpace(expectedPreviewId) || string.IsNullOrWhiteSpace(expectedPreviewGeneration)
            || string.IsNullOrWhiteSpace(expectedSourceRevision))
            return Invalid("预览输入标识不能为空。", "preview");
        if (!string.Equals(metadata.PreviewId, expectedPreviewId, StringComparison.Ordinal))
            return new(BackendErrorCode.PreviewFrameNotFound, "预览帧不属于当前预览会话。", "previewId");
        if (!string.Equals(metadata.PreviewGeneration, expectedPreviewGeneration, StringComparison.Ordinal)
            || !string.Equals(metadata.SourceRevision, expectedSourceRevision, StringComparison.Ordinal))
            return new(BackendErrorCode.PreviewGenerationConflict, "预览帧属于过期的输入代次。", "previewGeneration");
        if (!long.TryParse(metadata.RequestSeq, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long sequence) || sequence <= lastRequestSequence)
            return new(BackendErrorCode.PreviewRequestOutOfOrder, "预览请求序号不是当前会话中的下一个序号。", "requestSeq");
        if (metadata.Width <= 0 || metadata.Height <= 0 || metadata.Width > limits.MaxWidth || metadata.Height > limits.MaxHeight)
            return Invalid("预览帧尺寸超过服务公布的上限。", "size");
        if (limits.MaxRawFrameBytes <= 0 || (long)metadata.Width * metadata.Height > limits.MaxRawFrameBytes / 4)
            return Invalid("预览帧原始像素超过服务公布的字节上限。", "size");
        if (!Enum.IsDefined(metadata.AlphaMode)) return Invalid("透明模式无效。", "alphaMode");
        PreviewBinaryHandleDto handle = metadata.Content;
        if (handle is null || !string.Equals(handle.ContentType, "image/png", StringComparison.OrdinalIgnoreCase))
            return Invalid("预览帧必须使用image/png内容。", "contentType");
        if (handle.SizeBytes <= 0 || handle.SizeBytes > MaxFrameBytes || handle.SizeBytes != content.Length
            || handle.SizeBytes > limits.MaxRetainedFrameBytes)
            return Invalid("预览帧大小不符合服务公布的上限。", "sizeBytes");
        if (handle.ExpiresAtUtc <= nowUtc)
            return new(BackendErrorCode.PreviewFrameExpired, "预览帧句柄已经到期。", "expiresAtUtc");
        if (limits.FrameLeaseLifetimeSeconds <= 0 || handle.ExpiresAtUtc - nowUtc > TimeSpan.FromSeconds(limits.FrameLeaseLifetimeSeconds))
            return Invalid("预览帧租约超过服务公布的上限。", "expiresAtUtc");
        if (string.IsNullOrWhiteSpace(handle.HandleId) || string.IsNullOrWhiteSpace(handle.ContentUrl)
            || handle.Sha256 is not { Length: 64 } || !IsHex(handle.Sha256))
            return Invalid("预览帧句柄不完整。", "content");
        string actualHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(actualHash, handle.Sha256, StringComparison.OrdinalIgnoreCase))
            return new(BackendErrorCode.PreviewFrameNotFound, "预览帧内容校验失败。", "sha256");
        // This validates the PNG container, not the decompressed pixels. Rendering
        // acceptance still requires a real decoder/rendering test.
        return PreviewPngValidation.IsValid(content, metadata.Width, metadata.Height)
            ? null : Invalid("预览帧PNG结构、校验和或尺寸无效。", "content");
    }

    private static BackendErrorResponse Invalid(string message, string field) =>
        new(BackendErrorCode.InvalidRequest, message, field);

    private static bool IsHex(string value)
    {
        foreach (char character in value)
            if (!Uri.IsHexDigit(character)) return false;
        return true;
    }
}
