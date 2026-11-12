using System.Collections.Immutable;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>图效果与材质兜底诊断的公共投影；预览、导出与同帧测量共用同一套身份与文字。</summary>
public static class BackendFallbackDiagnostics
{
    /// <summary>不透明输出缺背景媒体（未设置、文件丢失或该时刻无帧）。</summary>
    public const string BackgroundMediaUnavailable = "BackgroundMediaUnavailable";

    /// <summary>透明输出没有背景层。</summary>
    public const string TransparentBackgroundDependency = "TransparentBackgroundDependency";

    /// <summary>投影一条图效果兜底诊断。</summary>
    /// <param name="diagnostic">渲染给出的图效果诊断。</param>
    /// <returns>来源为 imageEffect 的公共诊断。</returns>
    public static BackendDiagnosticDto Map(RenderImageEffectDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new(diagnostic.Code, "warning", diagnostic.Code switch
        {
            BackgroundMediaUnavailable => "背景媒体不可用（未设置、文件丢失或该时刻无帧），图效果已旁路为原样。",
            TransparentBackgroundDependency => "透明输出没有背景，图效果已旁路为原样。",
            _ => "图效果使用了已声明的降级行为。",
        }, null, OriginKind: "imageEffect",
            ImageEffect: new(diagnostic.InstanceId.Value, diagnostic.ArtifactHash, Resource(diagnostic.Graph.Identity), diagnostic.InputId, diagnostic.Code));
    }

    /// <summary>投影一条材质兜底诊断。</summary>
    /// <param name="diagnostic">渲染给出的材质诊断。</param>
    /// <returns>来源为 material 的公共诊断。</returns>
    public static BackendDiagnosticDto Map(RenderMaterialDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new(diagnostic.Code, "warning", diagnostic.Code switch
        {
            BackgroundMediaUnavailable => "背景媒体不可用（未设置、文件丢失或该时刻无帧），材质已退回包内兜底外观。",
            TransparentBackgroundDependency => "透明输出没有背景，材质已退回包内兜底外观。",
            _ => "材质使用了已声明的降级行为。",
        }, null, OriginKind: "material",
            Material: new(Owner(diagnostic.Owner), Resource(diagnostic.Material.Identity), diagnostic.InputId, diagnostic.Code));
    }

    private static BackendDiagnosticResourceDto Resource(ResourceIdentity value) => new(value.PackageId, value.Revision, value.ExportId);

    private static BackendDiagnosticMaterialOwnerDto Owner(PreparedMaterialOwner owner) => owner switch
    {
        PreparedMaterialOwner.Script s => new("script", Script: new(s.InstanceId.Value, s.ArtifactHash,
            s.PackageIdentity is null ? null : new(s.PackageIdentity.PackageId, s.PackageIdentity.Revision, s.PackageIdentity.ExportId))),
        PreparedMaterialOwner.Source s => new("source", Source: new(s.Reference.Path, s.Reference.Region, (int)s.Slot,
            new(s.Reference.Material.PackageId, s.Reference.Material.Revision, s.Reference.Material.ExportId), s.Reference.Material.Parameters.Json)),
        _ => throw new RenderSessionFailureException("FallbackDiagnosticsInvalid", "材质诊断缺少真实来源。"),
    };
}

/// <summary>
/// 逐帧收集兜底诊断：按公共投影去重，保留首次出现顺序；超过
/// <see cref="BackendFallbackDiagnosticsContract.MaximumItems"/> 条或
/// <see cref="BackendFallbackDiagnosticsContract.MaximumJsonBytes"/> 字节后不再加入，只记截断。
/// </summary>
public sealed class BackendFallbackDiagnosticCollector
{
    // 渲染身份到公共投影基本一一对应；记住的渲染身份封顶，避免截断后每帧重复投影。
    private const int MaximumRemembered = 1024;
    private readonly HashSet<RenderImageEffectDiagnostic> _images = [];
    private readonly HashSet<RenderMaterialDiagnostic> _materials = [];
    private readonly HashSet<BackendDiagnosticDto> _accepted = [];
    private readonly List<BackendDiagnosticDto> _items = [];
    private int _bytes = 2;

    /// <summary>含至少一条兜底诊断的帧数。</summary>
    public int FrameCount { get; private set; }

    /// <summary>是否因条数或字节上限丢掉了去重后的诊断。</summary>
    public bool Truncated { get; private set; }

    /// <summary>已收集的去重诊断，按首次出现排序。</summary>
    public BackendDiagnosticDto[] ToArray() => [.. _items];

    /// <summary>收集一帧的兜底诊断。</summary>
    /// <param name="frame">渲染完成的帧。</param>
    public void Add(RenderFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Add(frame.ImageEffectDiagnostics, frame.MaterialDiagnostics);
    }

    /// <summary>收集一帧的兜底诊断。</summary>
    /// <param name="images">该帧的图效果诊断。</param>
    /// <param name="materials">该帧的材质诊断。</param>
    public void Add(ImmutableArray<RenderImageEffectDiagnostic> images, ImmutableArray<RenderMaterialDiagnostic> materials)
    {
        if (images.IsDefaultOrEmpty && materials.IsDefaultOrEmpty) return;
        FrameCount = checked(FrameCount + 1);
        if (!images.IsDefault)
            foreach (RenderImageEffectDiagnostic diagnostic in images)
                if (Remember(_images, diagnostic)) Accept(BackendFallbackDiagnostics.Map(diagnostic));
        if (!materials.IsDefault)
            foreach (RenderMaterialDiagnostic diagnostic in materials)
                if (Remember(_materials, diagnostic)) Accept(BackendFallbackDiagnostics.Map(diagnostic));
    }

    private bool Remember<T>(HashSet<T> set, T value)
    {
        if (set.Contains(value)) return false;
        if (_images.Count + _materials.Count >= MaximumRemembered) { Truncated = true; return false; }
        set.Add(value);
        return true;
    }

    private void Accept(BackendDiagnosticDto item)
    {
        if (_accepted.Contains(item)) return;
        if (_items.Count >= BackendFallbackDiagnosticsContract.MaximumItems) { Truncated = true; return; }
        int bytes = JsonSerializer.SerializeToUtf8Bytes(item, BackendFallbackDiagnosticsContract.JsonOptions).Length + (_items.Count == 0 ? 0 : 1);
        if (_bytes + bytes > BackendFallbackDiagnosticsContract.MaximumJsonBytes) { Truncated = true; return; }
        _accepted.Add(item); _items.Add(item); _bytes += bytes;
    }
}
