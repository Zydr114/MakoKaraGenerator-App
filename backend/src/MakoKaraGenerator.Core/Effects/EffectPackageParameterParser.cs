using System.Collections.Immutable;
using System.Text.Json;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Effects;

// 参数默认值与实例覆盖共用此验证器；控制器、材质和图不能各自转换类型。
internal static class EffectPackageParameterParser
{
    internal static ImmutableDictionary<string, EffectParameterValue> Resolve(EffectExportDefinition export,
        ImmutableDictionary<string, EffectParameterDefinition> parameters, ImmutableDictionary<string, EffectPackageFile> files,
        ReadOnlyMemory<byte> overrides, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = export.ParameterIds.ToImmutableDictionary(id => id, id => parameters[id].DefaultValue, StringComparer.Ordinal).ToBuilder();
        if (overrides.IsEmpty) return values.ToImmutable();
        using var json = ReadJson(overrides, "parameters"); var root = json.RootElement;
        Keys(root, "parameters", [], export.ParameterIds.ToArray());
        foreach (var property in root.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested(); var parameter = parameters[property.Name];
            values[property.Name] = EffectPackageParameterParser.ReadValue(property.Value, parameter.Constraint, files, "parameters." + property.Name);
        }
        return values.ToImmutable();
    }

    internal static ImmutableArray<EffectParameterDefinition> Read(JsonElement declarations,
        ImmutableDictionary<string, EffectPackageFile> files, CancellationToken cancellationToken)
    {
        var parameters = ImmutableArray.CreateBuilder<EffectParameterDefinition>(); HashSet<string> ids = new(StringComparer.Ordinal);
        int index = 0;
        foreach (var item in declarations.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested(); string path = "parameters[" + index++ + "]";
            string id = RequiredText(item, "id", path), type = RequiredText(item, "type", path);
            if (!ids.Add(id)) throw Error("PackageParameterDuplicate", path + ".id", "参数身份重复。");
            string[] extra = type switch
            {
                "number" or "integer" or "duration" => ["min", "max"], "enum" => ["values"],
                "boolean" or "text" or "color" or "curve" or "asset" => [],
                _ => throw Error("PackageParameterTypeUnsupported", path + ".type", "未知参数类型。"),
            };
            Keys(item, path, ["id", "type", "label", "default", .. extra], ["unit"]);
            string label = RequiredText(item, "label", path), unit = item.TryGetProperty("unit", out _) ? RequiredText(item, "unit", path) : "";
            ImmutableArray<EffectParameterOption> options = [];
            EffectParameterConstraint constraint;
            if (type == "number")
            {
                double minimum = Number(item.GetProperty("min"), path + ".min"), maximum = Number(item.GetProperty("max"), path + ".max");
                if (minimum > maximum) throw Error("PackageParameterRangeInvalid", path, "参数区间反向。");
                constraint = new EffectParameterConstraint.Number(id, minimum, maximum);
            }
            else if (type is "integer" or "duration")
            {
                long minimum = Whole(item.GetProperty("min"), path + ".min"), maximum = Whole(item.GetProperty("max"), path + ".max");
                if (minimum > maximum || type == "duration" && minimum < 0) throw Error("PackageParameterRangeInvalid", path, "整数区间反向或时长为负。");
                constraint = type == "integer" ? new EffectParameterConstraint.Whole(id, minimum, maximum) : new EffectParameterConstraint.Duration(id, minimum, maximum);
            }
            else if (type == "enum")
            {
                var choices = ImmutableArray.CreateBuilder<EffectParameterOption>(); HashSet<string> values = new(StringComparer.Ordinal);
                foreach (var choice in Array(item.GetProperty("values"), path + ".values"))
                {
                    Keys(choice, path + ".values", ["value", "label"], []);
                    string value = RequiredText(choice, "value", path + ".values");
                    if (!values.Add(value)) throw Error("PackageParameterChoiceDuplicate", path + ".values", "选项身份重复。");
                    choices.Add(new(value, RequiredText(choice, "label", path + ".values")));
                }
                if (choices.Count == 0) throw Error("PackageParameterRangeInvalid", path + ".values", "枚举必须明确提供选项。");
                options = choices.ToImmutable(); constraint = new EffectParameterConstraint.Choice(id, options.Select(option => option.Value));
            }
            else constraint = type switch
            {
                "boolean" => new EffectParameterConstraint.Toggle(id), "text" => new EffectParameterConstraint.Text(id),
                "color" => new EffectParameterConstraint.Color(id), "curve" => new EffectParameterConstraint.Curve(id),
                "asset" => new EffectParameterConstraint.Asset(id), _ => throw new InvalidOperationException(),
            };
            var valueDefault = ReadValue(item.GetProperty("default"), constraint, files, path + ".default");
            parameters.Add(new(type, label, unit.Length == 0 ? null : unit, constraint, valueDefault, options));
        }
        return parameters.ToImmutable();
    }

    internal static EffectParameterValue ReadValue(JsonElement value, EffectParameterConstraint constraint, ImmutableDictionary<string, EffectPackageFile> files, string path)
    {
        switch (constraint)
        {
            case EffectParameterConstraint.Number number:
                double numeric = Number(value, path);
                if (numeric < number.Minimum || numeric > number.Maximum) throw Range(path);
                return new EffectParameterValue.Number(numeric);
            case EffectParameterConstraint.Whole integer:
                long whole = Whole(value, path);
                if (whole < integer.Minimum || whole > integer.Maximum) throw Range(path);
                return new EffectParameterValue.Whole(whole);
            case EffectParameterConstraint.Duration duration:
                long time = Whole(value, path);
                if (time < duration.Minimum || time > duration.Maximum) throw Range(path);
                return new EffectParameterValue.Duration(time);
            case EffectParameterConstraint.Toggle:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Type(path);
                return new EffectParameterValue.Toggle(value.GetBoolean());
            case EffectParameterConstraint.Text:
                return new EffectParameterValue.Text(String(value, path, false));
            case EffectParameterConstraint.Choice choice:
                string selected = String(value, path);
                if (!choice.Values.Contains(selected, StringComparer.Ordinal)) throw Range(path);
                return new EffectParameterValue.Choice(selected);
            case EffectParameterConstraint.Color:
                Keys(value, path, ["r", "g", "b", "a"], []);
                double Component(string key)
                { double component = Number(value.GetProperty(key), path + "." + key); if (component is < 0 or > 1) throw Range(path + "." + key); return component; }
                return new EffectParameterValue.Color(Component("r"), Component("g"), Component("b"), Component("a"));
            case EffectParameterConstraint.Curve:
                return new EffectParameterValue.Curve(ReadCurve(value, path));
            case EffectParameterConstraint.Asset:
                string asset = String(value, path); EffectPackageContents.ValidatePath(asset);
                if (!files.TryGetValue(asset, out var file) || file.Role != "image")
                    throw Error("PackageParameterImageMissing", path, "图片参数须引用本包明确声明 role:image 的文件。");
                return new EffectParameterValue.Asset(asset);
            default: throw Error("PackageParameterTypeUnsupported", path, "未知约束类型。");
        }
    }
    private static EffectCurveDefinition ReadCurve(JsonElement value, string path)
    {
        string kind = RequiredText(value, "kind", path);
        if (kind == "easing")
        {
            Keys(value, path, ["kind", "easing"], []); string easing = RequiredText(value, "easing", path);
            if (easing is not ("linear" or "easeOut" or "easeInOut")) throw Error("PackageCurveInvalid", path, "未知缓动。");
            return new(kind, easing, []);
        }
        ImmutableArray<EffectCurvePoint> points;
        if (kind == "cubicBezier")
        {
            Keys(value, path, ["kind", "first", "second"], []);
            points = [Point(value.GetProperty("first"), path + ".first"), Point(value.GetProperty("second"), path + ".second")];
        }
        else if (kind == "controlPoints")
        {
            Keys(value, path, ["kind", "points"], []); points = [.. Array(value.GetProperty("points"), path + ".points").Select(point => Point(point, path + ".points"))];
            if (points.Length < 2 || points[0] != new EffectCurvePoint(0, 0) || points[^1] != new EffectCurvePoint(1, 1)
                || points.Zip(points.Skip(1)).Any(pair => pair.First.X >= pair.Second.X))
                throw Error("PackageCurveInvalid", path, "折线须有首尾 (0,0)、(1,1) 且 X 严格递增。");
        }
        else throw Error("PackageCurveInvalid", path + ".kind", "未知曲线类型。");
        return new(kind, null, points);
    }
    private static EffectCurvePoint Point(JsonElement value, string path)
    {
        var components = Array(value, path).ToArray();
        if (components.Length != 2) throw Error("PackageCurveInvalid", path, "控制点须有两个分量。");
        double x = Number(components[0], path + "[0]"), y = Number(components[1], path + "[1]");
        if (x is < 0 or > 1) throw Error("PackageCurveInvalid", path, "曲线进度必须在 [0,1]。");
        return new(x, y);
    }
}
