using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Rendering;

// IPC source, deliberately independent of the tolerant project-file style mapper.
internal static class SnapshotPresetApplicationsSourceCodec
{
    internal static JsonElement Encode(PresetApplications applications, JsonSerializerOptions options, ProjectEffects? effects = null)
    {
        ApplicationsSource source;
        try { source = Capture(applications, options, effects); }
        catch (SnapshotContractException) { throw; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NullReferenceException)
        { throw new SnapshotContractException("SnapshotPresetInvalid", "固定预设领域源无效。", error); }
        SnapshotJsonContract.ValidateSourceGraph(source, options, StaticProjectSnapshotCodec.MaximumSourceValues,
            StaticProjectSnapshotCodec.MaximumBytes, packageMaterials: effects is not null);
        using var stream = new StaticProjectSnapshotCodec.BoundedSnapshotStream(StaticProjectSnapshotCodec.MaximumBytes);
        JsonSerializer.Serialize(stream, source, options);
        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    internal static PresetApplications Decode(JsonElement? element, JsonSerializerOptions options, ProjectEffects? effects = null)
    {
        if (element is null) throw Invalid();
        var source = element.Value.Deserialize<ApplicationsSource>(options) ?? throw Invalid();
        if (source.ContractVersion != 1 || source.TemplateSchemaVersion != StyleTemplateLibrary.CurrentSchemaVersion || source.Items.Length == 0)
            throw Invalid();
        return new(source.Items.Select(item =>
        {
            if (effects is null && (item.Identities.Length != 0 || item.BranchInputs.Count != 0)) throw Unsupported();
            var template = item.Snapshot;
            if (effects is null && (template.EffectRecipes.Length != 0 || template.Packages.Length != 0)) throw Unsupported();
            StyleTemplateKind kind = template.Kind switch
            {
                "Line" => StyleTemplateKind.Line, "Token" => StyleTemplateKind.Token,
                "Composite" => StyleTemplateKind.Composite, _ => throw Invalid(),
            };
            var properties = template.Properties.Select(name =>
                LyricStylePropertyExtensions.TryParseJsonName(name, out var property) ? property : throw Invalid()).ToArray();
            if (properties.Distinct().Count() != properties.Length) throw Invalid();
            if (effects is null && !SourceMaterialCatalog.Collect(template.Style).IsEmpty) throw Unsupported();
            var recipes = template.EffectRecipes.Select(value => (value.Deserialize<RecipeSource>(options) ?? throw Invalid()).ToDomain()).ToArray();
            var packages = template.Packages.Select(value =>
            {
                var reference = value.Deserialize<PackageReference>(options) ?? throw Invalid();
                return effects!.GetPackage(reference.PackageId, reference.Revision);
            }).ToArray();
            StyleTemplate snapshot = StyleTemplate.Create(template.Id, template.Name, kind, template.Style, properties, recipes, packages);
            // The source must already be canonical; the factory must not silently trim it.
            if (snapshot.Name != template.Name) throw Invalid();
            return new PresetApplication(item.Id, item.Target.ToBinding(), snapshot,
                item.Identities.Select(value => (value.Deserialize<IdentitySource>(options) ?? throw Invalid()).ToDomain()),
                item.BranchInputs, item.Enabled);
        }));
    }

    private static ApplicationsSource Capture(PresetApplications applications, JsonSerializerOptions options, ProjectEffects? effects)
    {
        RequireMembers(typeof(PresetApplications), ["Items"]);
        RequireMembers(typeof(PresetApplication), ["Id", "Target", "Snapshot", "Identities", "BranchInputs", "Enabled"]);
        RequireMembers(typeof(StyleTemplate), ["Id", "Name", "Kind", "Style", "Properties", "EffectRecipes", "Packages"]);
        RequireMembers(typeof(ProjectEffectBinding), ["Kind", "Id", "Ids", "IncludeDescendants"]);
        if (applications.Items.IsEmpty) throw Invalid();
        if (applications.Items.Length > StaticProjectSnapshotCodec.MaximumSourceValues) throw Size();
        return new(1, StyleTemplateLibrary.CurrentSchemaVersion, [.. applications.Items.Select(item =>
        {
            // Inspect stored source, including disabled applications and unselected style fields.
            if (effects is null && (!item.Snapshot.EffectRecipes.IsEmpty || !item.Snapshot.Packages.IsEmpty
                || !item.Identities.IsEmpty || !item.BranchInputs.IsEmpty)) throw Unsupported();
            var snapshot = item.Snapshot;
            if (effects is null && !SourceMaterialCatalog.Collect(snapshot.Style).IsEmpty) throw Unsupported();
            if (!StyleTemplate.Create(snapshot.Id, snapshot.Name, snapshot.Kind, snapshot.Style, snapshot.Properties,
                snapshot.EffectRecipes, snapshot.Packages).Equals(snapshot))
                throw Invalid();
            foreach (var package in snapshot.Packages)
                _ = effects!.GetPackage(package.PackageId, package.Revision);
            return new ApplicationSource(item.Id, item.Enabled, BindingSource.FromBinding(item.Target),
                new(snapshot.Id, snapshot.Name, snapshot.Kind.ToString(), snapshot.Style,
                    [.. snapshot.Properties.Select(LyricStylePropertyExtensions.ToJsonName)],
                    [.. snapshot.EffectRecipes.Select(recipe => JsonSerializer.SerializeToElement(RecipeSource.From(recipe), options))],
                    [.. snapshot.Packages.Select(package => JsonSerializer.SerializeToElement(new PackageReference(package.PackageId, package.Revision), options))]),
                item.BranchInputs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                [.. item.Identities.Select(identity => JsonSerializer.SerializeToElement(new IdentitySource(identity.RecipeId,
                    BindingSource.FromBinding(identity.Binding), identity.InstanceId, identity.Seed, identity.Enabled), options))]);
        })]);
    }

    private sealed record ApplicationsSource(int ContractVersion, int TemplateSchemaVersion, ApplicationSource[] Items);
    private sealed record ApplicationSource(string Id, bool Enabled, BindingSource Target, TemplateSource Snapshot,
        Dictionary<string, string> BranchInputs, JsonElement[] Identities);
    private sealed record TemplateSource(string Id, string Name, string Kind, LyricStyle Style, string[] Properties,
        JsonElement[] EffectRecipes, JsonElement[] Packages);
    private sealed record PackageReference(string PackageId, string Revision);
    private sealed record IdentitySource(string RecipeId, BindingSource Binding, string InstanceId, ulong Seed, bool Enabled)
    { internal PresetEffectIdentity ToDomain() => new(RecipeId, Binding.ToBinding(), InstanceId, Seed, Enabled); }
    private sealed record RecipeSource(string RecipeId, string Name, TemplateEffectTarget Target, string PackageId,
        string Revision, string ExportId, JsonElement Parameters, EffectOverlayLayer OverlayLayer, bool Enabled,
        ScriptReadScope ReadScope, string? BranchInput, ulong? FixedSeed, string? ConfigurationId)
    {
        internal static RecipeSource From(TemplateEffectRecipe value) => new(value.RecipeId, value.Name, value.Target,
            value.PackageId, value.Revision, value.ExportId, ParametersElement(value.Parameters),
            value.OverlayLayer, value.Enabled, value.ReadScope, value.BranchInput, value.FixedSeed, value.ConfigurationId);
        internal TemplateEffectRecipe ToDomain() => new(RecipeId, Name, Target, PackageId, Revision, ExportId,
            EffectParameterOverrides.Parse(Parameters.GetRawText()), OverlayLayer, Enabled, ReadScope, BranchInput, FixedSeed, ConfigurationId);
        private static JsonElement ParametersElement(EffectParameterOverrides parameters)
        { using var document = JsonDocument.Parse(parameters.Json); return document.RootElement.Clone(); }
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(ProjectSource), "project")]
    [JsonDerivedType(typeof(BranchSource), "branch")]
    [JsonDerivedType(typeof(LinesSource), "lines")]
    [JsonDerivedType(typeof(TokensSource), "tokens")]
    [JsonDerivedType(typeof(ClipsSource), "clips")]
    [JsonDerivedType(typeof(NodeSource), "node")]
    private abstract record BindingSource
    {
        internal abstract ProjectEffectBinding ToBinding();
        internal static BindingSource FromBinding(ProjectEffectBinding value) => value.Kind switch
        {
            ProjectEffectBindingKind.Project => new ProjectSource(),
            ProjectEffectBindingKind.Branch => new BranchSource(value.Id!),
            ProjectEffectBindingKind.Lines when value.Ids.Length == 1 => new LinesSource([.. value.Ids]),
            ProjectEffectBindingKind.Tokens when value.Ids.Length == 1 => new TokensSource([.. value.Ids]),
            ProjectEffectBindingKind.Clips when value.Ids.Length == 1 => new ClipsSource([.. value.Ids]),
            ProjectEffectBindingKind.Node => new NodeSource(value.Id!, value.IncludeDescendants),
            _ => throw Invalid(),
        };
    }
    private sealed record ProjectSource : BindingSource
    { internal override ProjectEffectBinding ToBinding() => new(ProjectEffectBindingKind.Project); }
    private sealed record BranchSource(string BranchId) : BindingSource
    { internal override ProjectEffectBinding ToBinding() => new(ProjectEffectBindingKind.Branch, BranchId); }
    private sealed record LinesSource(string[] LineIds) : BindingSource
    { internal override ProjectEffectBinding ToBinding() => One(ProjectEffectBindingKind.Lines, LineIds); }
    private sealed record TokensSource(string[] TokenIds) : BindingSource
    { internal override ProjectEffectBinding ToBinding() => One(ProjectEffectBindingKind.Tokens, TokenIds); }
    private sealed record ClipsSource(string[] ClipIds) : BindingSource
    { internal override ProjectEffectBinding ToBinding() => One(ProjectEffectBindingKind.Clips, ClipIds); }
    private sealed record NodeSource(string NodeId, bool IncludeDescendants) : BindingSource
    { internal override ProjectEffectBinding ToBinding() => new(ProjectEffectBindingKind.Node, NodeId, includeDescendants: IncludeDescendants); }
    private static ProjectEffectBinding One(ProjectEffectBindingKind kind, string[] ids)
        => ids.Length == 1 ? new(kind, ids: ids) : throw Invalid();
    private static void RequireMembers(Type type, string[] expected)
    {
        if (!type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Select(property => property.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new SnapshotContractException("SnapshotTypeUnsupported", "固定预设源成员未全部登记：" + type.Name);
    }
    private static SnapshotContractException Invalid() => new("SnapshotPresetInvalid", "固定静态预设源不完整或不规范。");
    private static SnapshotContractException Unsupported() => new("SnapshotEffectsUnsupported", "纯静态预设能力不接受包、配方或运行身份，包括禁用记录。");
    private static SnapshotContractException Size() => new("SnapshotSizeExceeded", "固定应用集合超过源值预算。");
}
