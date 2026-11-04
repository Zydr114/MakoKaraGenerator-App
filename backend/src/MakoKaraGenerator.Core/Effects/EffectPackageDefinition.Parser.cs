using System.Collections.Immutable;
using System.Text.Json;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Effects;

public sealed partial class EffectPackageDefinition
{
    /// <summary>完整读取控制器、材质、图及全部资源；不会部分接受坏导出或猜测缺少字段。</summary>
    public static EffectPackageDefinition Read(EffectPackageContents contents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents); cancellationToken.ThrowIfCancellationRequested();
        using var json = ReadJson(contents.Manifest.AsMemory(), "manifest.json");
        var files = contents.Files.ToImmutableDictionary(file => file.Path, StringComparer.Ordinal);
        var parameters = EffectPackageParameterParser.Read(json.RootElement.GetProperty("parameters"), files, cancellationToken);
        var programs = EffectPackageShaderPrograms.Read(contents, cancellationToken);
        var graphs = EffectPackageImageGraphs.Read(contents, parameters, programs, null, cancellationToken);
        var exports = ImmutableArray.CreateBuilder<EffectExportDefinition>(); HashSet<string> ids = new(StringComparer.Ordinal); int index = 0;
        var parameterIds = parameters.Select(parameter => parameter.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in Array(json.RootElement.GetProperty("exports"), "exports"))
        {
            cancellationToken.ThrowIfCancellationRequested(); string path = "exports[" + index++ + "]";
            string kind = RequiredText(item, "kind", path);
            string[] fields = kind switch
            {
                "controller" => ["id", "kind", "name", "entryType", "parameters", "bindingKinds", "outputs"],
                "material" => ["id", "kind", "name", "graph", "parameters", "regions", "fallback", "coverageMode", "outputs"],
                "graph" => ["id", "kind", "name", "graph", "parameters", "bindingKinds", "outputs"],
                _ => throw Error("PackageExportKindUnsupported", path + ".kind", "未知导出种类。"),
            };
            Keys(item, path, fields, []);
            string id = RequiredText(item, "id", path), name = RequiredText(item, "name", path);
            if (!ids.Add(id)) throw Error("PackageExportDuplicate", path + ".id", "跨种类导出身份重复。");
            var references = Strings(item.GetProperty("parameters"), path + ".parameters");
            foreach (string reference in references)
                if (!parameterIds.Contains(reference)) throw Error("PackageParameterMissing", path + ".parameters", "引用不存在的参数：" + reference);
            var outputs = ReadOutputs(item.GetProperty("outputs"), kind, path + ".outputs", EffectPackageProfiles.IsV2(contents.Profile), cancellationToken);
            if (EffectPackageProfiles.IsV2(contents.Profile))
                foreach (var output in outputs)
                    foreach (string capability in output.Requires)
                    {
                        EffectPackageProfiles.RequireCapability(capability, path + ".outputs." + output.Id + ".requires");
                        if (!contents.Requires.Contains(capability, StringComparer.Ordinal))
                            throw Error("PackageCapabilityMissing", path + ".outputs." + output.Id + ".requires", "输出能力必须同时在包 requires 中声明。");
                    }
            if (EffectPackageProfiles.IsV2(contents.Profile)) ValidateV2Stages(outputs, contents.Emitters, path);
            var ordered = Sort(outputs, path);
            if (kind == "controller")
            {
                exports.Add(new EffectControllerDefinition(id, name, RequiredText(item, "entryType", path), references, ReadBindings(item, path), outputs, ordered));
                continue;
            }
            string graphPath = RequiredText(item, "graph", path);
            if (!graphs.TryGetValue(graphPath, out var graph)) throw Error("PackageExportGraphMissing", path + ".graph", "须引用同闭包 role:graph 文件。");
            foreach (string parameter in graph.Plan.Definition.ParameterIds)
                if (!references.Contains(parameter, StringComparer.Ordinal)) throw Error("PackageExportGraphParameterMissing", path + ".parameters", "遗漏图参数：" + parameter);
            if (kind == "material")
            {
                var regions = Strings(item.GetProperty("regions"), path + ".regions");
                if (regions.IsEmpty || regions.Any(region => region is not ("fill" or "stroke")))
                    throw Error("PackageMaterialRegionInvalid", path + ".regions", "材质须明确允许 fill／stroke 区域。");
                if (outputs.Length != 1 || outputs[0].Kind != "ImageGraph" || outputs[0].Target != "region"
                    || outputs[0].Bypass is not EffectOutputBypass.MaterialFallback)
                    throw Error("PackageMaterialOutputInvalid", path + ".outputs", "材质须唯一 region 图输出并明确 materialFallback／identity。");
                exports.Add(new EffectMaterialDefinition(id, name, graphPath, references, regions,
                    ReadFallback(item.GetProperty("fallback"), path + ".fallback", cancellationToken),
                    RequiredText(item, "coverageMode", path) switch
                    {
                        "unmasked" => EffectMaterialCoverageMode.Unmasked,
                        "masked" => EffectMaterialCoverageMode.Masked,
                        _ => throw Error("PackageMaterialCoverageModeInvalid", path + ".coverageMode", "须明确声明 unmasked／masked 覆盖模式。"),
                    }, outputs, ordered));
            }
            else
            {
                if (outputs.IsEmpty || outputs.Any(output => output.Kind != "ImageGraph"))
                    throw Error("PackageGraphOutputInvalid", path + ".outputs", "图导出须声明真实目标的 ImageGraph 输出。");
                foreach (var output in outputs)
                {
                    if (output.Bypass is EffectOutputBypass.MaterialFallback) throw Error("PackageGraphBypassInvalid", path + ".outputs." + output.Id + ".bypass", "图不能使用材质回退。");
                    if (output.Bypass is EffectOutputBypass.GraphInput input && graph.Plan.Definition.BypassInput != GraphReference.Input(input.InputId))
                        throw Error("PackageGraphBypassMismatch", path + ".outputs." + output.Id + ".bypass", "须与图根原输入旁路一致。");
                    if (HasBackgroundDependency(graph.Plan) && output.Bypass is not EffectOutputBypass.GraphInput)
                        throw Error("PackageGraphBypassMissing", path + ".outputs." + output.Id + ".bypass", "背景依赖图须明确原输入／identity 旁路。");
                }
                exports.Add(new EffectGraphDefinition(id, name, graphPath, references, ReadBindings(item, path), outputs, ordered));
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); return new(contents, parameters, exports.ToImmutable(), programs, graphs);
    }
    private static ImmutableArray<string> ReadBindings(JsonElement item, string path)
    {
        var bindings = Strings(item.GetProperty("bindingKinds"), path + ".bindingKinds");
        if (bindings.Any(binding => binding is not ("project" or "branch" or "lines" or "tokens" or "clips" or "node")))
            throw Error("PackageBindingKindUnsupported", path + ".bindingKinds", "未知绑定种类。");
        return bindings;
    }
    private static ImmutableArray<EffectOutputDefinition> ReadOutputs(JsonElement value, string exportKind, string path, bool v2, CancellationToken token)
    {
        var outputs = ImmutableArray.CreateBuilder<EffectOutputDefinition>(); HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var output in Array(value, path))
        {
            token.ThrowIfCancellationRequested(); string location = path + "[" + outputs.Count + "]";
            Keys(output, location, ["id", "kind", "target", "requires", "dependsOn"], ["bypass"]);
            string id = RequiredText(output, "id", location), kind = RequiredText(output, "kind", location), target = RequiredText(output, "target", location);
            if (!ids.Add(id)) throw Error("PackageOutputDuplicate", location + ".id", "输出身份重复。");
            if (kind is not ("StyleModulation" or "VisualTransform" or "GeometryMapping" or "ImageGraph" or "Overlay"))
                throw Error("PackageOutputKindUnsupported", location + ".kind", "未知输出种类。");
            bool v2Target = v2 && exportKind == "controller" && (target == "baseToken" && (kind is "VisualTransform" or "StyleModulation")
                || target == "cluster" && kind == "VisualTransform" || target == "canvas" && kind == "Overlay");
            if (v2 && kind == "Overlay" && (target != "canvas" || exportKind != "controller"))
                throw Error("PackageOutputTargetUnsupported", location + ".target", "SDK v2 Overlay 必须明确使用 controller 的 canvas 目标。");
            if (target is not ("tokenFamily" or "line" or "group" or "textMark") && !(exportKind == "material" && target == "region") && !v2Target)
                throw Error("PackageOutputTargetUnsupported", location + ".target", "须声明真实目标类别。");
            EffectOutputBypass? bypass = null;
            if (output.TryGetProperty("bypass", out var field))
            {
                if (kind != "ImageGraph") throw Error("PackageOutputBypassInvalid", location + ".bypass", "此旁路编码仅适用于图输出。");
                bypass = ReadBypass(field, location + ".bypass");
            }
            outputs.Add(new(id, kind, target, Strings(output.GetProperty("requires"), location + ".requires"), Strings(output.GetProperty("dependsOn"), location + ".dependsOn"), bypass));
        }
        return outputs.ToImmutable();
    }

    private static void ValidateV2Stages(ImmutableArray<EffectOutputDefinition> outputs,
        ImmutableArray<EffectEmitterDefinition> emitters, string path)
    {
        var byId = outputs.ToDictionary(output => output.Id, StringComparer.Ordinal);
        var emitterIds = emitters.Select(emitter => emitter.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var output in outputs)
            foreach (string dependency in output.DependsOn)
                if (byId.TryGetValue(dependency, out var parent) && parent.Kind == "Overlay"
                    && (output.Kind != "Overlay" || emitterIds.Contains(output.Id)))
                    throw Error("PackageOutputStageDependencyInvalid", path + ".outputs." + output.Id + ".dependsOn",
                        "晚期图元输出不能成为文字输出或发射轨的输入。");
    }
    private static EffectOutputBypass ReadBypass(JsonElement value, string path)
    {
        string kind = RequiredText(value, "kind", path);
        Keys(value, path, kind == "materialFallback" ? ["kind", "mapping"] : ["kind", "input", "mapping"], []);
        if (RequiredText(value, "mapping", path) != "identity") throw Error("PackageBypassMappingUnsupported", path + ".mapping", "当前旁路必须显式使用 identity。");
        if (kind == "materialFallback") return new EffectOutputBypass.MaterialFallback();
        if (kind != "graphInput") throw Error("PackageOutputBypassInvalid", path + ".kind", "未知旁路种类。");
        string input = RequiredText(value, "input", path);
        if (!input.StartsWith("input:", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(input[6..]))
            throw Error("PackageOutputBypassInvalid", path + ".input", "须显式指定 input: 身份。");
        return new EffectOutputBypass.GraphInput(input[6..]);
    }
    internal static bool HasBackgroundDependency(CompiledImageGraphDefinition plan)
    {
        var active = new HashSet<GraphReference>(); var nodes = plan.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        void Visit(GraphReference reference)
        {
            if (!active.Add(reference) || reference.IsInput) return;
            foreach (var source in ImageGraphCompiler.References(nodes[reference.Id])) Visit(source);
        }
        Visit(plan.Definition.Output); if (plan.Definition.CompositeCoverage is { } coverage) Visit(coverage);
        return plan.Definition.Inputs.Any(input => active.Contains(GraphReference.Input(input.Id)) && input.BackgroundRequirement != GraphBackgroundRequirement.None);
    }

    private static ImmutableArray<EffectOutputDefinition> Sort(ImmutableArray<EffectOutputDefinition> outputs, string path)
    {
        var byId = outputs.Select((output, index) => (output.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
        int[] counts = new int[outputs.Length]; List<int>[] consumers = [.. outputs.Select(_ => new List<int>())];
        for (int index = 0; index < outputs.Length; index++)
            foreach (string dependency in outputs[index].DependsOn)
            {
                if (!byId.TryGetValue(dependency, out int parent)) throw Error("PackageOutputDependencyMissing", path + ".outputs." + outputs[index].Id, "依赖输出不存在：" + dependency);
                counts[index]++; consumers[parent].Add(index);
            }
        SortedSet<int> ready = [.. Enumerable.Range(0, outputs.Length).Where(index => counts[index] == 0)];
        var ordered = ImmutableArray.CreateBuilder<EffectOutputDefinition>();
        while (ready.Count > 0)
        {
            int index = ready.Min; ready.Remove(index); ordered.Add(outputs[index]);
            foreach (int consumer in consumers[index]) if (--counts[consumer] == 0) ready.Add(consumer);
        }
        if (ordered.Count != outputs.Length) throw Error("PackageOutputCycle", path + ".outputs", "输出依赖含环。");
        return ordered.ToImmutable();
    }

}
