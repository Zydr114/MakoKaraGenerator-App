using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class WorkerProcessProxy
{
    private static readonly JsonSerializerOptions FontJson = FontWorkerContract.JsonOptions;

    /// <summary>在真实 Worker 字体环境中读取字体族目录。</summary>
    public async Task<FontListResponse> ListFontsAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        byte[] payload = FontWorkerContract.ListPayload();
        WorkerMessage response = await InvokeTimedMessageAsync(
            new(FontWorkerContract.ListKind, Guid.NewGuid().ToString("N"),
                WorkerProtocol.ComputeSha256(payload), payload), "result",
            timeout, _ => { }, cancellationToken).ConfigureAwait(false);
        if (response.Payload.Length is <= 0 or > WorkerProtocol.MaxPayloadBytes)
            throw new WorkerProtocolException("字体目录响应超限。", "FontWorkerBudgetExceeded");
        FontListResponse value = JsonSerializer.Deserialize<FontListResponse>(response.Payload, FontJson)
            ?? throw new WorkerProtocolException("字体目录为空。", "FontWorkerInvalid");
        FontWorkerContract.ValidateListResponse(value);
        return value;
    }

    /// <summary>在真实 Worker 字体环境中执行整形、回退和缺字检查。</summary>
    public async Task<FontInspectResponse> InspectFontAsync(FontInspectRequest input,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        byte[] payload = FontWorkerContract.EncodeInspect(input);
        WorkerMessage response = await InvokeTimedMessageAsync(
            new(FontWorkerContract.InspectKind, Guid.NewGuid().ToString("N"),
                WorkerProtocol.ComputeSha256(payload), payload), "result",
            timeout, _ => { }, cancellationToken).ConfigureAwait(false);
        if (response.Payload.Length is <= 0 or > WorkerProtocol.MaxPayloadBytes)
            throw new WorkerProtocolException("字体检查响应超限。", "FontWorkerBudgetExceeded");
        FontInspectResponse value = JsonSerializer.Deserialize<FontInspectResponse>(response.Payload, FontJson)
            ?? throw new WorkerProtocolException("字体检查为空。", "FontWorkerInvalid");
        FontWorkerContract.ValidateInspectResponse(value);
        return value;
    }
}
