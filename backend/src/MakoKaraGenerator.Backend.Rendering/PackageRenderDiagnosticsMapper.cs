using System.Collections.Immutable;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>完整包诊断的固定源映射；只从已验证目录恢复SDK句柄。</summary>
public sealed class PackageRenderDiagnosticsMapper
{
    private readonly ProjectSnapshot _snapshot;
    private readonly Dictionary<(string, string), (EffectPackageDefinition Definition, ResourceCatalog Resources)> _packages;
    private readonly ImmutableArray<SourceMaterialReference> _sources;
    /// <summary>按固定包源完整映射诊断；身份不匹配时拒绝。</summary>
    public PackageRenderDiagnosticsMapper(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _packages = snapshot.Effects.Packages.Select(p => EffectPackageDefinition.Read(p)).ToDictionary(
            p => (p.Contents.PackageId, p.Contents.Revision), p => (p, new ResourceCatalog(p.Contents.PackageId, p.Contents.Revision,
                [.. p.Materials.Select(m => m.Id)], [.. p.Graphs.Select(g => g.Id)], p.ImagePaths)));
        _sources = SourceMaterialCatalog.Collect(snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation, snapshot.PresetApplications);
    }
    /// <summary>按固定包源完整映射诊断；身份不匹配时拒绝。</summary>
    public static WorkerPackageResourceDto Encode(ResourceIdentity value) => new(value.PackageId, value.Revision, value.ExportId);
    private static WorkerPackageResourceDto Encode(EffectPackageIdentity value) => new(value.PackageId, value.Revision, value.ExportId);
    /// <summary>按固定包源完整映射诊断；身份不匹配时拒绝。</summary>
    public static WorkerPackageDiagnosticsDto EncodeFrame(RenderFrame frame)
    {
        var result = new WorkerPackageDiagnosticsDto(WorkerPackageDiagnosticsContract.Version, [],
            [.. frame.ImageEffectDiagnostics.Select(d => new WorkerPackageImageDiagnosticDto(d.InstanceId.Value, d.ArtifactHash, Encode(d.Graph.Identity), d.InputId, d.Code))],
            [.. frame.MaterialDiagnostics.Select(d => new WorkerPackageMaterialDiagnosticDto(EncodeOwner(d.Owner), Encode(d.Material.Identity), d.InputId, d.Code))]);
        WorkerPackageDiagnosticsContract.Validate(result); return result;
    }
    /// <summary>按固定包源完整映射诊断；身份不匹配时拒绝。</summary>
    public static WorkerPackageDiagnosticsDto EncodeSource(IEnumerable<RenderSourceDiagnostic> source)
    {
        var result = new WorkerPackageDiagnosticsDto(WorkerPackageDiagnosticsContract.Version,
            [.. source.Select(d => new WorkerPackageSourceDiagnosticDto(d.PackageId, d.Revision, d.InstanceId, d.ExportId, d.Code, d.Message, d.IsError, d.Path, d.Line, d.Column))], [], []);
        WorkerPackageDiagnosticsContract.Validate(result); return result;
    }
    private static WorkerPackageMaterialOwnerDto EncodeOwner(PreparedMaterialOwner value) => value switch
    {
        PreparedMaterialOwner.Script s => new("script", s.InstanceId.Value, s.ArtifactHash,
            s.PackageIdentity is null ? null : Encode(s.PackageIdentity), null, null, null, null, null),
        PreparedMaterialOwner.Source s => new("source", null, null, null, s.Reference.Path, s.Reference.Region, (int)s.Slot,
            new(s.Reference.Material.PackageId, s.Reference.Material.Revision, s.Reference.Material.ExportId), s.Reference.Material.Parameters.Json),
        _ => throw Invalid(),
    };
    /// <summary>按固定包源完整映射诊断；身份不匹配时拒绝。</summary>
    public ImmutableArray<RenderSourceDiagnostic> DecodeSource(WorkerPackageDiagnosticsDto dto)
    {
        WorkerPackageDiagnosticsContract.Validate(dto);
        if (dto.Image.Length != 0 || dto.Material.Length != 0 || dto.Source.Length == 0 || !dto.Source.Any(d => d.IsError)) throw Invalid();
        foreach (var d in dto.Source)
        {
            _ = Package(new(d.PackageId, d.Revision, d.ExportId ?? "source"));
            var enabled = _snapshot.Effects.Instances.Where(i => i.Enabled && i.PackageId == d.PackageId && i.Revision == d.Revision).ToArray();
            if (d.InstanceId is null ? enabled.Length != 0 || !_sources.Any(r => r.Material.PackageId == d.PackageId && r.Material.Revision == d.Revision) : !enabled.Any(i => i.Id == d.InstanceId && i.ExportId == d.ExportId)) throw Invalid();
        }
        return [.. dto.Source.Select(d => new RenderSourceDiagnostic(d.PackageId, d.Revision, d.InstanceId, d.ExportId, d.Code, d.Message, d.IsError, d.Path, d.Line, d.Column))];
    }
    /// <summary>按固定包源完整映射诊断；身份不匹配时拒绝。</summary>
    public (ImmutableArray<RenderImageEffectDiagnostic> Image, ImmutableArray<RenderMaterialDiagnostic> Material) DecodeFrame(WorkerPackageDiagnosticsDto dto)
    {
        WorkerPackageDiagnosticsContract.Validate(dto);
        if (dto.Source.Length != 0) throw Invalid();
        try
        {
            return ([.. dto.Image.Select(d =>
            {
                var instance = Instance(d.InstanceId, d.Graph);
                Artifact(instance, d.ArtifactHash, d.Graph);
                Input(d.Graph, d.InputId, d.Code, false); var graph = Package(d.Graph).Resources.GetGraph(d.Graph.ExportId);
                return new RenderImageEffectDiagnostic(new(d.InstanceId), d.ArtifactHash, graph, d.InputId, d.Code);
            })], [.. dto.Material.Select(d =>
            {
                Input(d.Material, d.InputId, d.Code, true);
                return new RenderMaterialDiagnostic(DecodeOwner(d.Owner, d.Material), Package(d.Material).Resources.GetMaterial(d.Material.ExportId), d.InputId, d.Code);
            })]);
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or EffectPackageException or System.Text.Json.JsonException) { throw Invalid(); }
    }
    private PreparedMaterialOwner DecodeOwner(WorkerPackageMaterialOwnerDto owner, WorkerPackageResourceDto material)
    {
        if (owner.Kind == "script")
        {
            var instance = Instance(owner.InstanceId!, material);
            Artifact(instance, owner.ArtifactHash!, null);
            if (owner.PackageIdentity is not { } identity || identity.PackageId != instance.PackageId || identity.Revision != instance.Revision
                || identity.ExportId != instance.ExportId || Package(identity).Definition.GetExport(identity.ExportId) is not EffectControllerDefinition) throw Invalid();
            return new PreparedMaterialOwner.Script(new(owner.InstanceId!), owner.ArtifactHash!)
            { PackageIdentity = new(identity.PackageId, identity.Revision, identity.ExportId) };
        }
        var id = owner.SourceMaterial!;
        var parameters = EffectParameterOverrides.Parse(owner.ParametersJson!);
        if (parameters.Json != owner.ParametersJson) throw Invalid();
        var reference = new SourceMaterialReference(owner.Path!, owner.Region!, new(id.PackageId, id.Revision, id.ExportId, parameters));
        PreparedMaterialSlot slot = reference.Region == "stroke" ? PreparedMaterialSlot.Stroke
            : SourceMaterialCatalog.IsSung(reference) ? PreparedMaterialSlot.SungFill : PreparedMaterialSlot.UnsungFill;
        if (!_sources.Contains(reference) || owner.Slot != (int)slot || id != material) throw Invalid();
        SourceMaterialCatalog.Validate(reference, Package(id).Definition);
        return new PreparedMaterialOwner.Source(reference, slot);
    }
    private void Artifact(ProjectEffectInstance instance, string artifactHash, WorkerPackageResourceDto? graph)
    {
        var definition = _packages[(instance.PackageId, instance.Revision)].Definition.GetExport(instance.ExportId);
        if (definition is EffectGraphDefinition)
        {
            if (graph is null || graph.ExportId != instance.ExportId || artifactHash != instance.Revision) throw Invalid();
        }
        else if (definition is not EffectControllerDefinition || artifactHash.Length != 64) throw Invalid();
    }
    private void Input(WorkerPackageResourceDto id, string inputId, string code, bool material)
    {
        var definition = Package(id).Definition;
        string path = material ? definition.GetMaterial(id.ExportId).GraphPath : definition.GetGraph(id.ExportId).GraphPath;
        // 透明输出跳过背景依赖；不透明缺背景媒体时同路旁路／兜底（只可能是声明了媒体依赖的必需输入）。
        bool valid = code switch
        {
            "TransparentBackgroundDependency" => definition.ImageGraphs[path].Plan.Definition.Inputs.Any(i => i.Id == inputId
                && i.BackgroundRequirement != GraphBackgroundRequirement.None),
            "BackgroundMediaUnavailable" => definition.ImageGraphs[path].Plan.Definition.Inputs.Any(i => i.Id == inputId
                && i.Required && i.BackgroundRequirement == GraphBackgroundRequirement.Media),
            _ => false,
        };
        if (!valid) throw Invalid();
    }
    private ProjectEffectInstance Instance(string id, WorkerPackageResourceDto resource)
    {
        var instance = _snapshot.Effects.Instances.FirstOrDefault(i => i.Enabled && i.Id == id && i.PackageId == resource.PackageId && i.Revision == resource.Revision);
        return instance ?? throw Invalid();
    }
    private (EffectPackageDefinition Definition, ResourceCatalog Resources) Package(WorkerPackageResourceDto id)
        => _packages.TryGetValue((id.PackageId, id.Revision), out var p) ? p : throw Invalid();
    private static WorkerProtocolException Invalid() => new("包诊断身份不属于固定源目录。", "WorkerPackageDiagnosticsInvalid");
}
