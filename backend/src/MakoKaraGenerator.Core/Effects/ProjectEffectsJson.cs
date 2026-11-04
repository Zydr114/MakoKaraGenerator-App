using System.Globalization;
using System.Text.Json;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>工程效果源数据的唯一严格编码；包字节由调用方按内容地址提供。</summary>
public static class ProjectEffectsJson
{
    private static readonly System.Buffers.SearchValues<char> HashCharacters = System.Buffers.SearchValues.Create("0123456789abcdef");
    /// <summary>保留的 v1 包 profile；消费者须显式选择 v1 或 v2，不能据此推断全部能力。</summary>
    public static EffectPackageProfile PackageProfile => EffectPackageProfiles.V1;
    /// <summary>当前工程效果源格式；明确声明开发期 v1／v2 并存。</summary>
    public const int CurrentContractVersion = 2;
    /// <summary>规范包目录，仅接受完整小写修订。</summary>
    public static string PackagePath(string revision)
    {
        if (revision.Length != 71 || !revision.StartsWith("sha256:", StringComparison.Ordinal)
            || revision.AsSpan(7).IndexOfAnyExcept(HashCharacters) >= 0)
            throw Error("EffectRevisionInvalid", "revision", "包修订必须为 sha256:<64位小写摘要>。");
        return "effects/packages/" + revision[7..];
    }
    /// <summary>编码包含所有注册包、实例、顺序和预算的源数据。</summary>
    public static byte[] Encode(ProjectEffects effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        foreach (var package in effects.Packages) EffectPackageProfiles.RequireSupported(package.Profile);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("contractVersion", CurrentContractVersion);
            // 仅供 v1 分派使用；v2 以固定时间直接求值，不消费此推进合同。
            writer.WriteStartObject("simulation"); writer.WriteNumber("version", 1); writer.WriteNumber("stepHz", 120); writer.WriteString("random", "pcg32-v1"); writer.WriteEndObject();
            writer.WriteStartObject("budgets"); writer.WriteNumber("surfaceMemoryBytes", effects.Budgets.SurfaceMemoryBytes);
            writer.WriteNumber("liveParticles", effects.Budgets.LiveParticles); writer.WriteNumber("trailSamples", effects.Budgets.TrailSamples);
            writer.WriteNumber("emissionRecords", effects.Budgets.EmissionRecords); writer.WriteNumber("drawCommands", effects.Budgets.DrawCommands); writer.WriteEndObject();
            writer.WriteStartArray("packages");
            foreach (var package in effects.Packages)
            {
                writer.WriteStartObject(); writer.WriteString("packageId", package.PackageId); writer.WriteString("revision", package.Revision);
                writer.WriteString("path", PackagePath(package.Revision)); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteStartArray("instances");
            foreach (var instance in effects.Instances)
            {
                writer.WriteStartObject(); writer.WriteString("id", instance.Id); writer.WriteString("name", instance.Name); writer.WriteBoolean("enabled", instance.Enabled);
                writer.WriteString("packageId", instance.PackageId); writer.WriteString("revision", instance.Revision); writer.WriteString("exportId", instance.ExportId);
                writer.WritePropertyName("binding"); WriteBinding(writer, instance.Binding);
                writer.WriteStartObject("subscription"); writer.WriteString("kind", instance.Subscription.Kind);
                if (instance.Subscription.BranchId is { } branch) writer.WriteString("branchId", branch); writer.WriteEndObject();
                writer.WriteString("seed", instance.Seed.ToString("x16", CultureInfo.InvariantCulture));
                writer.WriteString("overlayLayer", OverlayName(instance.OverlayLayer));
                writer.WritePropertyName("templateOrigin");
                if (instance.TemplateOrigin is not { } origin) writer.WriteNullValue();
                else
                {
                    writer.WriteStartObject(); writer.WriteString("templateId", origin.TemplateId); writer.WriteString("recipeId", origin.RecipeId);
                    writer.WriteString("ownerKind", origin.OwnerKind == Projects.StyleTemplateKind.Line ? "line" : "token"); writer.WriteString("ownerId", origin.OwnerId); writer.WriteEndObject();
                }
                writer.WritePropertyName("parameters"); using var parameters = JsonDocument.Parse(instance.Parameters.Json); parameters.RootElement.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return stream.ToArray();
    }
    internal static void WriteBinding(Utf8JsonWriter writer, ProjectEffectBinding binding)
    {
        writer.WriteStartObject(); writer.WriteString("kind", KindName(binding.Kind));
        string? field = IdField(binding.Kind);
        if (binding.Kind is ProjectEffectBindingKind.Branch or ProjectEffectBindingKind.Node) writer.WriteString(field!, binding.Id);
        else if (field is not null) { writer.WriteStartArray(field); foreach (var id in binding.Ids) writer.WriteStringValue(id); writer.WriteEndArray(); }
        if (binding.Kind == ProjectEffectBindingKind.Node) writer.WriteBoolean("includeDescendants", binding.IncludeDescendants);
        writer.WriteEndObject();
    }
    /// <summary>解析严格结构并固定所有包；不编译或执行作者代码。</summary>
    public static ProjectEffects Read(JsonElement value, Func<string, EffectPackageContents> readPackage)
    {
        ArgumentNullException.ThrowIfNull(readPackage);
        const string path = "effects";
        Keys(value, path, ["contractVersion", "simulation", "budgets", "packages", "instances"], []);
        if (Whole(value.GetProperty("contractVersion"), path) != CurrentContractVersion)
            throw Error("EffectContractVersionUnsupported", path + ".contractVersion", "工程效果源版本不受支持；旧数据未更改。");
        var simulation = value.GetProperty("simulation"); Keys(simulation, path + ".simulation", ["version", "stepHz", "random"], []);
        if (Whole(simulation.GetProperty("version"), path) != 1 || Whole(simulation.GetProperty("stepHz"), path) != 120
            || RequiredText(simulation, "random", path) != "pcg32-v1") throw Range(path + ".simulation");
        var budget = value.GetProperty("budgets"); Keys(budget, path + ".budgets", ["surfaceMemoryBytes", "liveParticles", "trailSamples", "emissionRecords", "drawCommands"], []);
        ProjectEffectBudgets limits = new()
        {
            SurfaceMemoryBytes = Whole(budget.GetProperty("surfaceMemoryBytes"), path + ".budgets.surfaceMemoryBytes"),
            LiveParticles = PositiveInt(budget, "liveParticles"), TrailSamples = PositiveInt(budget, "trailSamples"),
            EmissionRecords = PositiveInt(budget, "emissionRecords"), DrawCommands = PositiveInt(budget, "drawCommands"),
        };
        if (!limits.IsValid) throw Range(path + ".budgets");
        List<EffectPackageContents> packages = [];
        foreach (var item in Array(value.GetProperty("packages"), path + ".packages"))
        {
            Keys(item, path + ".packages", ["packageId", "revision", "path"], []);
            string id = RequiredText(item, "packageId", path), revision = RequiredText(item, "revision", path);
            string relative = RequiredText(item, "path", path);
            if (relative != PackagePath(revision)) throw Error("EffectPackagePathInvalid", path + ".packages.path", "包目录与修订不符。");
            var contents = readPackage(relative);
            EffectPackageProfiles.RequireSupported(contents.Profile);
            if (contents.PackageId != id || contents.Revision != revision) throw Error("EffectPackageIdentityMismatch", relative, "包实际身份或修订与工程入口不符。");
            packages.Add(contents);
        }
        List<ProjectEffectInstance> instances = [];
        foreach (var item in Array(value.GetProperty("instances"), path + ".instances"))
        {
            Keys(item, path + ".instances", ["id", "name", "enabled", "packageId", "revision", "exportId", "binding", "subscription", "seed", "parameters", "overlayLayer", "templateOrigin"], []);
            var subscription = item.GetProperty("subscription"); string kind = RequiredText(subscription, "kind", path);
            Keys(subscription, path + ".subscription", kind == "branch" ? ["kind", "branchId"] : ["kind"], []);
            string seedText = RequiredText(item, "seed", path);
            if (seedText.Length != 16 || seedText.AsSpan().IndexOfAnyExcept(HashCharacters) >= 0) throw Type(path + ".seed");
            string revision = RequiredText(item, "revision", path); _ = PackagePath(revision);
            instances.Add(new()
            {
                Id = RequiredText(item, "id", path), Name = RequiredText(item, "name", path), Enabled = Boolean(item.GetProperty("enabled"), path + ".enabled"),
                PackageId = RequiredText(item, "packageId", path), Revision = revision, ExportId = RequiredText(item, "exportId", path),
                Binding = ReadBinding(item.GetProperty("binding")), Subscription = new(kind, kind == "branch" ? RequiredText(subscription, "branchId", path) : null),
                Seed = ulong.Parse(seedText, NumberStyles.HexNumber, CultureInfo.InvariantCulture), Parameters = EffectParameterOverrides.Parse(item.GetProperty("parameters").GetRawText()),
                OverlayLayer = ReadOverlay(RequiredText(item, "overlayLayer", path)), TemplateOrigin = ReadOrigin(item.GetProperty("templateOrigin")),
            });
        }
        var result = new ProjectEffects(packages, instances, limits);
        foreach (var instance in result.Instances) _ = result.GetPackage(instance.PackageId, instance.Revision);
        return result;
    }
    internal static ProjectEffectBinding ReadBinding(JsonElement value)
    {
        const string path = "effects.instances.binding"; string name = RequiredText(value, "kind", path);
        if (!Enum.TryParse<ProjectEffectBindingKind>(name, true, out var kind) || !Enum.IsDefined(kind) || KindName(kind) != name) throw Type(path + ".kind");
        string? field = IdField(kind);
        Keys(value, path, kind == ProjectEffectBindingKind.Project ? ["kind"] : kind == ProjectEffectBindingKind.Node ? ["kind", field!, "includeDescendants"] : ["kind", field!], []);
        return new(kind, kind is ProjectEffectBindingKind.Branch or ProjectEffectBindingKind.Node ? RequiredText(value, field!, path) : null,
            kind is ProjectEffectBindingKind.Lines or ProjectEffectBindingKind.Tokens or ProjectEffectBindingKind.Clips ? Strings(value.GetProperty(field!), path) : null,
            kind == ProjectEffectBindingKind.Node && Boolean(value.GetProperty("includeDescendants"), path));
    }
    internal static string KindName(ProjectEffectBindingKind kind) => kind.ToString().ToLowerInvariant();
    internal static string OverlayName(EffectOverlayLayer layer) => layer switch
    { EffectOverlayLayer.Package => "package", EffectOverlayLayer.BehindText => "behindText", EffectOverlayLayer.AboveText => "aboveText", _ => throw Type("overlayLayer") };
    internal static EffectOverlayLayer ReadOverlay(string name) => name switch
    { "package" => EffectOverlayLayer.Package, "behindText" => EffectOverlayLayer.BehindText, "aboveText" => EffectOverlayLayer.AboveText, _ => throw Type("overlayLayer") };
    private static ProjectEffectTemplateOrigin? ReadOrigin(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        Keys(value, "templateOrigin", ["templateId", "recipeId", "ownerKind", "ownerId"], []);
        var kind = RequiredText(value, "ownerKind", "templateOrigin") switch
        { "line" => Projects.StyleTemplateKind.Line, "token" => Projects.StyleTemplateKind.Token, _ => throw Type("templateOrigin.ownerKind") };
        return new(RequiredText(value, "templateId", "templateOrigin"), RequiredText(value, "recipeId", "templateOrigin"), kind, RequiredText(value, "ownerId", "templateOrigin"));
    }
    private static string? IdField(ProjectEffectBindingKind kind) => kind switch
    { ProjectEffectBindingKind.Branch => "branchId", ProjectEffectBindingKind.Node => "nodeId", ProjectEffectBindingKind.Lines => "lineIds", ProjectEffectBindingKind.Tokens => "tokenIds", ProjectEffectBindingKind.Clips => "clipIds", _ => null };
    private static bool Boolean(JsonElement value, string path) => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Type(path);
    private static int PositiveInt(JsonElement value, string key)
    { long number = Whole(value.GetProperty(key), "effects.budgets." + key); return number is > 0 and <= int.MaxValue ? (int)number : throw Range("effects.budgets." + key); }
}
