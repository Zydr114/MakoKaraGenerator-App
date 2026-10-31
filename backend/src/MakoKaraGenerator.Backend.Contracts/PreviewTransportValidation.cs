namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>
/// 预览二进制和诊断分页的纯传输边界校验。
/// 该类型不读取文件、不创建帧，也不推断真实渲染结果；它只拒绝
/// 不能安全绑定到当前预览会话的响应。
/// </summary>
public static class PreviewTransportValidation
{
    /// <summary>
    /// 校验帧元数据是否仍绑定当前会话和请求。
    /// </summary>
    /// <param name="session">服务公布的当前预览会话。</param>
    /// <param name="request">产生该帧的请求。</param>
    /// <param name="frame">待发布的帧元数据。</param>
    /// <param name="nowUtc">用于检查句柄租约的当前 UTC 时间。</param>
    /// <returns>成功返回 <see langword="null"/>；失败返回稳定错误码。</returns>
    public static BackendErrorResponse? ValidateFrame(
        PreviewSessionResponse session,
        PreviewFrameRequest request,
        PreviewFrameMetadataDto frame,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frame);

        if (!string.Equals(frame.PreviewId, session.PreviewId, StringComparison.Ordinal))
            return Error(BackendErrorCode.PreviewSessionNotFound, "帧不属于当前预览会话。", "previewId");
        if (!string.Equals(request.PreviewGeneration, session.PreviewGeneration, StringComparison.Ordinal)
            || !string.Equals(frame.PreviewGeneration, session.PreviewGeneration, StringComparison.Ordinal))
            return Error(BackendErrorCode.PreviewGenerationConflict, "帧的预览代次已经失效。", "previewGeneration");
        if (!string.Equals(frame.SourceRevision, session.Source.SourceRevision, StringComparison.Ordinal))
            return Error(BackendErrorCode.PreviewGenerationConflict, "帧的来源修订已经失效。", "sourceRevision");
        if (!string.Equals(frame.RequestSeq, request.RequestSeq, StringComparison.Ordinal))
            return Error(BackendErrorCode.PreviewRequestOutOfOrder, "帧序号与请求不一致。", "requestSeq");
        if (frame.Width != session.Size.Width || frame.Height != session.Size.Height || frame.AlphaMode != session.AlphaMode)
            return Error(BackendErrorCode.PreviewGenerationConflict, "帧的输出设置已经失效。", "size");
        if (frame.RenderBackend != session.RenderBackend || frame.RenderBackend is not ("cpu" or "gpu"))
            return Error(BackendErrorCode.PreviewGenerationConflict, "帧的渲染后端已经失效。", "renderBackend");
        if (frame.Width <= 0 || frame.Height <= 0 || session.Limits.MaxRawFrameBytes <= 0
            || (long)frame.Width * frame.Height > session.Limits.MaxRawFrameBytes / 4)
            return Error(BackendErrorCode.InvalidRequest, "帧原始像素超过服务公布的字节上限。", "size");

        PreviewBinaryHandleDto content = frame.Content;
        if (content is null)
            return Error(BackendErrorCode.InvalidRequest, "帧句柄不能为空。", "content");
        if (content.SizeBytes <= 0 || content.SizeBytes > session.Limits.MaxRetainedFrameBytes)
            return Error(BackendErrorCode.ResourceTooLarge, "帧二进制超过服务公布的大小上限。", "content.sizeBytes");
        if (!string.Equals(content.ContentType, "image/png", StringComparison.OrdinalIgnoreCase))
            return Error(BackendErrorCode.UnsupportedMediaType, "帧句柄必须指向 PNG 内容。", "content.contentType");
        if (!IsSha256(content.Sha256))
            return Error(BackendErrorCode.InvalidRequest, "帧句柄缺少有效 SHA-256。", "content.sha256");
        if (string.IsNullOrWhiteSpace(content.ContentUrl))
            return Error(BackendErrorCode.InvalidRequest, "帧句柄缺少内容地址。", "content.contentUrl");
        if (content.ExpiresAtUtc <= nowUtc)
            return Error(BackendErrorCode.PreviewFrameExpired, "帧租约已经到期。", "content.expiresAtUtc");
        if (session.Limits.FrameLeaseLifetimeSeconds <= 0
            || content.ExpiresAtUtc - nowUtc > TimeSpan.FromSeconds(session.Limits.FrameLeaseLifetimeSeconds))
            return Error(BackendErrorCode.InvalidRequest, "帧租约超过服务公布的上限。", "content.expiresAtUtc");
        return null;
    }

    /// <summary>
    /// 校验诊断页是否可以拼接到当前预览代次。
    /// </summary>
    /// <param name="session">服务公布的当前预览会话。</param>
    /// <param name="page">待返回的诊断页。</param>
    /// <param name="requestedCursor">本次请求携带的游标；首页可为 <see langword="null"/>。</param>
    /// <returns>成功返回 <see langword="null"/>；失败返回稳定错误码。</returns>
    public static BackendErrorResponse? ValidateDiagnosticsPage(
        PreviewSessionResponse session,
        PreviewDiagnosticsPageDto page,
        string? requestedCursor = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(page);

        if (!string.Equals(page.PreviewId, session.PreviewId, StringComparison.Ordinal))
            return Error(BackendErrorCode.PreviewSessionNotFound, "诊断页不属于当前预览会话。", "previewId");
        if (!string.Equals(page.PreviewGeneration, session.PreviewGeneration, StringComparison.Ordinal))
            return Error(BackendErrorCode.PreviewGenerationConflict, "诊断页的预览代次已经失效。", "previewGeneration");
        if (!string.Equals(page.SourceRevision, session.Source.SourceRevision, StringComparison.Ordinal))
            return Error(BackendErrorCode.PreviewGenerationConflict, "诊断页的来源修订已经失效。", "sourceRevision");
        if (requestedCursor is not null && !string.Equals(page.Cursor, requestedCursor, StringComparison.Ordinal))
            return Error(BackendErrorCode.DiagnosticCursorExpired, "诊断游标已经失效，请从当前代次重新读取。", "cursor");
        if (page.Diagnostics is null)
            return Error(BackendErrorCode.InvalidRequest, "诊断页内容不能为空。", "diagnostics");
        if (page.Diagnostics.Length > session.Limits.MaxDiagnosticsPageSize)
            return Error(BackendErrorCode.DiagnosticLimitExceeded, "诊断页超过服务公布的条数上限。", "limit");
        foreach (BackendDiagnosticDto diagnostic in page.Diagnostics)
        {
            if (diagnostic is null || string.IsNullOrWhiteSpace(diagnostic.Code)
                || diagnostic.Severity is not ("info" or "warning" or "error") || diagnostic.Message is null
                || diagnostic.OriginKind != "compileSource" && string.IsNullOrWhiteSpace(diagnostic.Message))
                return Error(BackendErrorCode.InvalidRequest, "诊断条目内容无效。", "diagnostics");
            bool payloads = diagnostic.Source is not null || diagnostic.ImageEffect is not null || diagnostic.Material is not null;
            if (payloads || diagnostic.OriginKind is "compileSource" or "imageEffect" or "material")
            {
                if (!ValidPackageDiagnostic(diagnostic))
                    return Error(BackendErrorCode.InvalidRequest, "诊断来源与完整payload不匹配。", "diagnostics");
                continue;
            }
            BackendDiagnosticOccurrenceDto? occurrence = diagnostic.Occurrence;
            if (diagnostic.OriginKind is null && occurrence is null) continue; // Existing B2 reports remain unchanged.
            if (diagnostic.OriginKind == "transportFailure" && occurrence is null) continue;
            if (diagnostic.OriginKind != "runtime" || occurrence is null || occurrence.Epoch == Guid.Empty
                || !IsNumber(occurrence.Sequence, positive: true) || !IsNumber(occurrence.TimeUs, positive: false)
                || string.IsNullOrWhiteSpace(occurrence.InstanceId) || occurrence.ArtifactHash is null
                || string.IsNullOrWhiteSpace(occurrence.Stage)
                || occurrence.Location is { } location && (string.IsNullOrWhiteSpace(location.Path) || location.Line < 1 || location.Column < 1)
                || occurrence.Package is { } package && (string.IsNullOrWhiteSpace(package.PackageId)
                    || string.IsNullOrWhiteSpace(package.Revision) || string.IsNullOrWhiteSpace(package.ExportId)))
                return Error(BackendErrorCode.InvalidRequest, "诊断来源或实际发生元数据无效。", "diagnostics.occurrence");
        }
        if (page.HasMore != (page.NextCursor is not null))
            return Error(BackendErrorCode.InvalidRequest, "诊断页的后续游标与HasMore不一致。", "nextCursor");
        return null;
    }

    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool Revision(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) && IsSha256(value[7..]);
    private static bool Artifact(string? value) => IsSha256(value) || Revision(value);
    private static bool Resource(BackendDiagnosticResourceDto? value) => value is not null && Text(value.PackageId) && Revision(value.Revision) && Text(value.ExportId);
    private static bool ValidPackageDiagnostic(BackendDiagnosticDto d)
    {
        if (d.Occurrence is not null || d.Location is not null) return false;
        if (d.OriginKind == "compileSource" && d.Source is { } s && d.ImageEffect is null && d.Material is null)
            return Text(s.PackageId) && Revision(s.Revision) && Text(s.Code) && s.Message is not null
                && (s.InstanceId is null && s.ExportId is null || Text(s.InstanceId) && Text(s.ExportId))
                && s.Line >= 0 && s.Column >= 0 && d.Code == s.Code && d.Message == s.Message
                && d.Severity == (s.IsError ? "error" : "warning");
        if (d.OriginKind == "imageEffect" && d.ImageEffect is { } i && d.Source is null && d.Material is null)
            return Text(i.InstanceId) && Artifact(i.ArtifactHash) && Resource(i.Graph) && Text(i.InputId) && Text(i.Code)
                && d.Code == i.Code && d.Severity == "warning";
        if (d.OriginKind == "material" && d.Material is { } m && d.Source is null && d.ImageEffect is null)
            return Resource(m.Material) && Text(m.InputId) && Text(m.Code) && d.Code == m.Code && d.Severity == "warning" && Owner(m.Owner);
        return false;
    }
    private static bool Owner(BackendDiagnosticMaterialOwnerDto? owner)
    {
        if (owner is { Kind: "script", Script: { } script, Source: null })
            return Text(script.InstanceId) && Artifact(script.ArtifactHash) && (script.PackageIdentity is null || Resource(script.PackageIdentity));
        if (owner is { Kind: "source", Script: null, Source: { } source })
        {
            if (!Text(source.Path) || !Text(source.Region) || source.Slot is < 0 or > 2 || !Resource(source.SourceMaterial) || source.ParametersJson is null) return false;
            try { using var json = System.Text.Json.JsonDocument.Parse(source.ParametersJson); return json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object; }
            catch (System.Text.Json.JsonException) { return false; }
        }
        return false;
    }

    private static BackendErrorResponse Error(BackendErrorCode code, string message, string field) => new(code, message, field);

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsNumber(string? value, bool positive)
        => long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out long number) && (positive ? number > 0 : number >= 0)
            && string.Equals(value, number.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
