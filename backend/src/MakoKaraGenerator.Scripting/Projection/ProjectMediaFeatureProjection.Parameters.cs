using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Scripting.Projection;

internal sealed partial class ProjectMediaFeatureProjection
{
    // This is semantic comparison of the existing closed Core/SDK types. The
    // Core parameter reader and ParametersDigest encoding remain the sole ones.
    /// <summary>Compares formally resolved actual-source values; it does not prove snapshot membership or resource admission.</summary>
    internal static bool SdkParametersEqual(ImmutableDictionary<string, EffectParameterValue> expected, ParameterValues actual,
        EffectPackageContents package)
    {
        if (expected.Count != actual.Ids.Length) return false;
        foreach ((string id, ParameterValue value) in actual.Entries)
        {
            if (!expected.TryGetValue(id, out EffectParameterValue? fixedValue)) return false;
            bool equal = (fixedValue, value) switch
            {
                (EffectParameterValue.Number left, ParameterValue.Number right) => Same(left.Value, right.Value),
                (EffectParameterValue.Whole left, ParameterValue.Whole right) => left.Value == right.Value,
                (EffectParameterValue.Toggle left, ParameterValue.Toggle right) => left.Value == right.Value,
                (EffectParameterValue.Duration left, ParameterValue.Duration right) => left.Value == right.Value,
                (EffectParameterValue.Choice left, ParameterValue.Choice right) => left.Value == right.Value,
                (EffectParameterValue.Text left, ParameterValue.Text right) => left.Value == right.Value,
                (EffectParameterValue.Color left, ParameterValue.Color right) => Same(left.R, right.Value.Red)
                    && Same(left.G, right.Value.Green) && Same(left.B, right.Value.Blue) && Same(left.A, right.Value.Alpha),
                (EffectParameterValue.Curve left, ParameterValue.Curve right) => CurveEqual(left.Value, right.Value),
                (EffectParameterValue.Asset left, ParameterValue.Asset right) => right.Value.Identity.PackageId == package.PackageId
                    && right.Value.Identity.Revision == package.Revision && right.Value.Identity.ExportId == left.Path,
                _ => false,
            };
            if (!equal) return false;
        }
        return true;
    }

    private static bool CurveEqual(EffectCurveDefinition expected, CurveValue actual) => (expected.Kind, actual) switch
    {
        ("easing", CurveValue.Easing value) => expected.Easing switch
        {
            "linear" => value.Kind == EasingKind.Linear,
            "easeOut" => value.Kind == EasingKind.EaseOut,
            "easeInOut" => value.Kind == EasingKind.EaseInOut,
            _ => false,
        },
        ("cubicBezier", CurveValue.CubicBezier value) => expected.Points.Length == 2
            && PointEqual(expected.Points[0], value.First) && PointEqual(expected.Points[1], value.Second),
        ("controlPoints", CurveValue.ControlPoints value) => expected.Points.Length == value.Points.Length
            && expected.Points.Zip(value.Points).All(pair => PointEqual(pair.First, pair.Second)),
        _ => false,
    };

    private static bool PointEqual(EffectCurvePoint expected, Point2 actual) => Same(expected.X, actual.X) && Same(expected.Y, actual.Y);

    private static bool CoreParametersEqual(ImmutableDictionary<string, EffectParameterValue> expected,
        ImmutableDictionary<string, EffectParameterValue> actual)
        => expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out EffectParameterValue? value)
            && CoreValueEqual(pair.Value, value));

    private static bool CoreValueEqual(EffectParameterValue expected, EffectParameterValue actual) => (expected, actual) switch
    {
        (EffectParameterValue.Number left, EffectParameterValue.Number right) => Same(left.Value, right.Value),
        (EffectParameterValue.Color left, EffectParameterValue.Color right) => Same(left.R, right.R) && Same(left.G, right.G)
            && Same(left.B, right.B) && Same(left.A, right.A),
        (EffectParameterValue.Curve left, EffectParameterValue.Curve right) => left.Value.Kind == right.Value.Kind
            && left.Value.Easing == right.Value.Easing && left.Value.Points.Length == right.Value.Points.Length
            && left.Value.Points.Zip(right.Value.Points).All(pair => Same(pair.First.X, pair.Second.X) && Same(pair.First.Y, pair.Second.Y)),
        _ => expected.Equals(actual),
    };
}
