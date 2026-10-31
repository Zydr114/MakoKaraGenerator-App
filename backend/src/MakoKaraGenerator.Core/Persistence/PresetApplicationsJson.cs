using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>工程固定应用快照的严格编码；完整包由工程唯一资源库存持有。</summary>
public static class PresetApplicationsJson
{
    /// <summary>编码快照、相对输入映射和持久实例身份，不保存投影状态。</summary>
    public static byte[] Encode(PresetApplications applications)
    {
        ArgumentNullException.ThrowIfNull(applications); using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("contractVersion", 1); writer.WriteNumber("templateSchemaVersion", StyleTemplateLibrary.CurrentSchemaVersion);
            writer.WriteStartArray("items");
            foreach (var item in applications.Items)
            {
                writer.WriteStartObject(); writer.WriteString("id", item.Id); writer.WriteBoolean("enabled", item.Enabled);
                writer.WritePropertyName("target"); ProjectEffectsJson.WriteBinding(writer, item.Target);
                writer.WritePropertyName("snapshot"); StyleTemplateLibrary.WriteDefinition(writer, item.Snapshot);
                writer.WriteStartObject("branchInputs"); foreach (var pair in item.BranchInputs) writer.WriteString(pair.Key, pair.Value); writer.WriteEndObject();
                writer.WriteStartArray("identities");
                foreach (var identity in item.Identities)
                {
                    writer.WriteStartObject(); writer.WriteString("recipeId", identity.RecipeId); writer.WriteString("instanceId", identity.InstanceId);
                    writer.WriteString("seed", identity.Seed.ToString("x16", CultureInfo.InvariantCulture)); writer.WriteBoolean("enabled", identity.Enabled);
                    writer.WritePropertyName("binding"); ProjectEffectsJson.WriteBinding(writer, identity.Binding); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>固定包缺失或快照身份错误拒绝整个应用集合；不存在的目标保留供诊断。</summary>
    public static PresetApplications Read(JsonElement value, ProjectEffects effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        Keys(value, "presetApplications", ["contractVersion", "templateSchemaVersion", "items"], []);
        if (Whole(value.GetProperty("contractVersion"), "presetApplications") != 1
            || Whole(value.GetProperty("templateSchemaVersion"), "presetApplications") != StyleTemplateLibrary.CurrentSchemaVersion) throw Type("presetApplications.version");
        var packages = effects.Packages.ToDictionary(package => (package.PackageId, package.Revision)); List<PresetApplication> items = [];
        foreach (var item in Array(value.GetProperty("items"), "presetApplications.items"))
        {
            Keys(item, "application", ["id", "enabled", "target", "snapshot", "branchInputs", "identities"], []);
            var snapshot = StyleTemplateLibrary.ReadDefinition(item.GetProperty("snapshot"), packages);
            var mappings = item.GetProperty("branchInputs"); if (mappings.ValueKind != JsonValueKind.Object) throw Type("application.branchInputs");
            Dictionary<string, string> inputs = new(StringComparer.Ordinal);
            foreach (var pair in mappings.EnumerateObject())
                if (!inputs.TryAdd(pair.Name, String(pair.Value, "application.branchInputs." + pair.Name))) throw Type("application.branchInputs");
            List<PresetEffectIdentity> identities = [];
            foreach (var identity in Array(item.GetProperty("identities"), "application.identities"))
            {
                Keys(identity, "identity", ["recipeId", "instanceId", "seed", "enabled", "binding"], []);
                ulong seed = StyleTemplateLibrary.ReadSeed(identity.GetProperty("seed")) ?? throw Type("identity.seed");
                identities.Add(new(RequiredText(identity, "recipeId", "identity"), ProjectEffectsJson.ReadBinding(identity.GetProperty("binding")),
                    RequiredText(identity, "instanceId", "identity"), seed, StyleTemplateLibrary.ReadBoolean(identity.GetProperty("enabled"), "identity.enabled")));
            }
            items.Add(new(RequiredText(item, "id", "application"), ProjectEffectsJson.ReadBinding(item.GetProperty("target")), snapshot, identities, inputs,
                StyleTemplateLibrary.ReadBoolean(item.GetProperty("enabled"), "application.enabled")));
        }
        var result = new PresetApplications(items);
        if (result.Items.SelectMany(item => item.Identities).Any(identity => effects.Instances.Any(instance => instance.Id == identity.InstanceId)))
            throw Type("presetApplications.instanceId");
        return result;
    }

    /// <summary>存储前确认所有快照仍拥有注册的完整字节；包括禁用应用。</summary>
    public static void RequireResources(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (var package in project.PresetApplications.Items.SelectMany(item => item.Snapshot.Packages))
            _ = project.Effects.GetPackage(package.PackageId, package.Revision);
    }
}
