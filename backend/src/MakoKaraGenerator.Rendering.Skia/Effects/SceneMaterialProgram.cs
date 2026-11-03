using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>固定包材质的真实句柄与捕获框，借用包，不持有作者可写native对象。</summary>
public sealed class SceneMaterialProgram
{
    private readonly EffectPackageImageGraph _definition;
    internal SceneMaterialProgram(MaterialHandle handle, CompiledPackageGraphExports package, EffectPackageImageGraph definition, GraphRect bounds)
    {
        if (!bounds.IsValid) throw new ArgumentException("材质捕获框须明确有效。", nameof(bounds));
        Handle = handle; Package = package; Bounds = bounds; _definition = definition;
        foreach (var capture in definition.Captures)
            if (capture.Value.IncludeOwnedOverlay) throw new EffectPackageException("PackageOwnedOverlayCapturePending", capture.Key, "材质owned overlay捕获尚未接入。");
    }
    /// <summary>真实固定材质句柄，不冒充图句柄。</summary>
    public MaterialHandle Handle { get; }
    /// <summary>借用的固定包。</summary>
    public CompiledPackageGraphExports Package { get; }
    /// <summary>明确Canvas逻辑框。</summary>
    public GraphRect Bounds { get; }
    /// <summary>借用的实际编译图。</summary>
    public CompiledImageGraph Graph => Package.GetMaterialGraph(Handle);
    /// <summary>读取逐输入配方，MaterialRegion由实际绑定解析。</summary>
    public SceneCaptureRecipe GetInputRecipe(string inputId, SceneCaptureRegion actualRegion)
    {
        var value = _definition.Captures.TryGetValue(inputId, out var capture) ? capture
            : throw new EffectPackageException("PackageGraphCaptureMissing", inputId, "目标输入没有实际捕获配方。");
        var region = value.Region switch { EffectCaptureRegion.Fill => SceneCaptureRegion.Fill,
            EffectCaptureRegion.Stroke => SceneCaptureRegion.Stroke, EffectCaptureRegion.FillAndStroke => SceneCaptureRegion.FillAndStroke,
            EffectCaptureRegion.MaterialRegion => actualRegion, _ => throw new ArgumentOutOfRangeException(nameof(actualRegion)) };
        return new(value.IncludeRuby, value.IncludeStroke, value.IncludeShadow, value.IncludeDescendantGroups, region, value.IncludeGlow);
    }
}
