using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>检查投影在中立模型与传输 DTO 之间转换；两者字段一一对应，经同一 JSON 形状往返并核对字节预算。</summary>
public static class FrameInspectionMapper
{
    /// <summary>Worker 侧：中立投影转为回执字段。</summary>
    public static PreviewInspectionDto Encode(RenderFrameInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(inspection, RenderFrameInspection.JsonOptions);
        if (json.Length > RenderFrameInspection.MaximumBytes)
            throw new WorkerProtocolException("检查投影超过字节预算。", "WorkerInspectionBudgetExceeded");
        return JsonSerializer.Deserialize<PreviewInspectionDto>(json, RenderFrameInspection.JsonOptions)
            ?? throw new WorkerProtocolException("检查投影无法编码。", "WorkerInspectionInvalid");
    }

    /// <summary>Host 侧：核对回执字段的预算与结构后转回中立投影。</summary>
    public static RenderFrameInspection Decode(PreviewInspectionDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(dto, RenderFrameInspection.JsonOptions);
        if (json.Length > RenderFrameInspection.MaximumBytes)
            throw new WorkerProtocolException("检查投影超过字节预算。", "WorkerInspectionBudgetExceeded");
        var result = JsonSerializer.Deserialize<RenderFrameInspection>(json, RenderFrameInspection.JsonOptions)
            ?? throw new WorkerProtocolException("检查投影无法解码。", "WorkerInspectionInvalid");
        if (result.Canvas is null || result.Tokens is null || result.Lines is null || result.Cursors is null || result.Particles is null
            || result.Effects is null || result.Cost is null || result.Truncated is null || result.Cost.Scripts is null || result.Cost.Anchors is null)
            throw new WorkerProtocolException("检查投影缺少必需字段。", "WorkerInspectionInvalid");
        return result;
    }

    /// <summary>Host 侧：中立投影转为 HTTP 正文。</summary>
    public static PreviewInspectionDto ToDto(RenderFrameInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(inspection, RenderFrameInspection.JsonOptions);
        return JsonSerializer.Deserialize<PreviewInspectionDto>(json, RenderFrameInspection.JsonOptions)
            ?? throw new InvalidOperationException("检查投影无法转换。");
    }
}
