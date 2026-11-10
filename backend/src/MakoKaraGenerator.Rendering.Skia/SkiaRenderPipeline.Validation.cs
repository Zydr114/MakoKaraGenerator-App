using System.Runtime.CompilerServices;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    // F6：请求（sealed record，只有 init 成员）与准备帧都不可变，门禁结果只取决于实例本身；
    // 同一实例通过一次即记下，帧内各次捕获不再重扫全工程源材质、重算输入指纹、重验全部矩阵与叠加图元。
    // 只按引用记，失败不记：新实例或上次失败的实例照常完整校验。
    private static readonly object ValidatedMarker = new();
    private readonly ConditionalWeakTable<RenderRequest, object> _validatedRequests = new();
    private readonly ConditionalWeakTable<PreparedRenderFrame, object> _validatedNativeFrames = new();

    /// <summary>与 <see cref="PreparedRenderFrame.ValidateRequest"/> 相同，同一请求实例只校验一次。</summary>
    private void ValidateRequestOnce(RenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_validatedRequests.TryGetValue(request, out _)) return;
        PreparedRenderFrame.ValidateRequest(request);
        _validatedRequests.AddOrUpdate(request, ValidatedMarker);
    }

    /// <summary>与 <c>ValidateNativePreparedFrame</c> 相同，同一准备帧实例只校验一次。</summary>
    private void ValidateNativeOnce(PreparedRenderFrame? prepared)
    {
        if (prepared is null || _validatedNativeFrames.TryGetValue(prepared, out _)) return;
        ValidateNativePreparedFrame(prepared);
        _validatedNativeFrames.AddOrUpdate(prepared, ValidatedMarker);
    }
}
