using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Scripting.Compilation;

public sealed partial class CompiledEffectPackage
{
    /// <summary>读取已由生产后端验证的包内基础回退；不自动绕过正常模式 shader 或制造第二个基础外观。</summary>
    public MaterialValue GetMaterialFallback(MaterialHandle handle)
    {
        EnsureSucceeded(); ArgumentNullException.ThrowIfNull(handle);
        if (!Resources.Contains(handle)) throw new EffectPackageException("InvalidMaterialResource", "exports", "材质不属于本固定包导出修订。");
        return Definition.GetMaterial(handle.Identity.ExportId).Fallback switch
        {
            EffectMaterialFallback.Solid solid => MaterialValue.Solid(Color(solid.Color)),
            EffectMaterialFallback.Gradient gradient => MaterialValue.Gradient(new([.. gradient.Value.Stops.Select(stop => new Scripting.GradientStop(stop.Offset, Color(stop.Color)))],
                (Scripting.GradientKind)gradient.Value.Kind, gradient.Value.AngleDegrees, (Scripting.GradientCoordinateSpace)gradient.Value.CoordinateSpace,
                (Scripting.GradientColorSpace)gradient.Value.ColorSpace, (Scripting.GradientHuePath)gradient.Value.HuePath, gradient.Value.CenterX,
                gradient.Value.CenterY, gradient.Value.RadiusX, gradient.Value.RadiusY)),
            _ => throw new InvalidOperationException("未知材质回退。"),
        };
    }
    private static ColorRgba Color(RgbaColor color) => new(color.Red / 255d, color.Green / 255d, color.Blue / 255d, color.Alpha / 255d);
}
