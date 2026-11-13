namespace MakoKaraGenerator.Rendering;

/// <summary>源材质的实际失败身份；不具有脚本实例或运行时发生序号。</summary>
/// <param name="Owner">真实持久源材质。</param><param name="Stage">实际失败阶段。</param>
/// <param name="TimeUs">实际整数微秒时刻。</param><param name="Code">稳定失败编号。</param>
/// <param name="Budget">实际中间表面超限时的安全数值；其他失败为空。</param>
public sealed record RenderSourceMaterialFailure(PreparedMaterialOwner.Source Owner, string Stage, long TimeUs,
    string Code, RenderImageSurfaceBudget? Budget = null);

/// <summary>实际申请之前的表面数量和估算字节；不包含异常消息或本地路径。</summary>
/// <param name="CurrentSurfaces">当前活动表面数。</param><param name="AdditionalSurfaces">申请表面数。</param>
/// <param name="MaximumSurfaces">固定表面上限。</param><param name="CurrentBytes">当前估算字节。</param>
/// <param name="AdditionalBytes">申请估算字节。</param><param name="MaximumBytes">固定字节上限。</param>
public sealed record RenderImageSurfaceBudget(int CurrentSurfaces, int AdditionalSurfaces, int MaximumSurfaces,
    long CurrentBytes, long AdditionalBytes, long MaximumBytes);
