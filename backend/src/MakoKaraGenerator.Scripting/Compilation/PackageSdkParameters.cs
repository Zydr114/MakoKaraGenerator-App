using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Scripting.Compilation;

internal static class PackageSdkParameters
{
    internal static void Validate(EffectPackageDefinition package, EffectExportDefinition export, ParameterValues values, ResourceCatalog resources)
    {
        if (!values.Ids.ToHashSet(StringComparer.Ordinal).SetEquals(export.ParameterIds))
            throw new EffectOutputException("PackageParameterSetMismatch", "图参数须完整且仅包含该导出声明的身份。");
        values.ValidateResources(resources);
        foreach (var parameter in package.Parameters.Where(parameter => export.ParameterIds.Contains(parameter.Id, StringComparer.Ordinal)))
        {
            try
            {
                bool valid = parameter.Constraint switch
                {
                    EffectParameterConstraint.Number number => values.GetNumber(number.Id) >= number.Minimum && values.GetNumber(number.Id) <= number.Maximum,
                    EffectParameterConstraint.Whole whole => values.GetInteger(whole.Id) >= whole.Minimum && values.GetInteger(whole.Id) <= whole.Maximum,
                    EffectParameterConstraint.Duration duration => values.GetDuration(duration.Id) >= duration.Minimum && values.GetDuration(duration.Id) <= duration.Maximum,
                    EffectParameterConstraint.Choice choice => choice.Values.Contains(values.GetEnum(choice.Id), StringComparer.Ordinal),
                    EffectParameterConstraint.Toggle toggle => Read(values.GetBoolean(toggle.Id)),
                    EffectParameterConstraint.Color color => Read(values.GetColor(color.Id)),
                    EffectParameterConstraint.Text text => Read(values.GetText(text.Id)),
                    EffectParameterConstraint.Curve curve => Read(values.GetCurve(curve.Id)),
                    EffectParameterConstraint.Asset asset => resources.Contains(values.GetAsset(asset.Id)),
                    _ => false,
                };
                if (!valid) throw new EffectOutputException("PackageParameterOutOfRange", "图参数超出该导出真实约束：" + parameter.Id);
            }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException && error is not EffectOutputException)
            { throw new EffectOutputException("PackageParameterTypeMismatch", "图参数类型不符：" + parameter.Id); }
        }
    }
    internal static string BindingKind(EffectBinding binding) => binding switch
    {
        EffectBinding.Project => "project", EffectBinding.Branch => "branch", EffectBinding.Lines => "lines",
        EffectBinding.Tokens => "tokens", EffectBinding.Clips => "clips", EffectBinding.Node => "node",
        _ => throw new EffectOutputException("PackageBindingNotAllowed", "未知绑定类型。"),
    };
    private static bool Read<T>(T value) => value is not null;
}
