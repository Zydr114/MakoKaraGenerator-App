using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Persistence;

public static partial class ScriptConfigurationLibrary
{
    /// <summary>规范元数据，参数保留完整精度，种子使用完整16位hex。</summary>
    public static byte[] Encode(IEnumerable<ScriptConfiguration> configurations) => EncodeItems(Validate(configurations));

    private static byte[] EncodeItems(ScriptConfiguration[] items)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            writer.WriteStartArray("packages");
            foreach (var package in Packages(items))
            {
                writer.WriteStartObject(); writer.WriteString("packageId", package.PackageId);
                writer.WriteString("revision", package.Revision); writer.WriteString("path", PackagePath(package.Revision)); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteStartArray("configurations");
            foreach (var item in items)
            {
                writer.WriteStartObject(); writer.WriteString("id", item.Id); writer.WriteString("name", item.Name);
                writer.WriteString("packageId", item.Package.PackageId); writer.WriteString("revision", item.Package.Revision);
                writer.WriteString("exportId", item.ExportId); writer.WritePropertyName("parameters");
                using var parameters = JsonDocument.Parse(item.Parameters.Json); parameters.RootElement.WriteTo(writer);
                writer.WriteString("target", item.Target.ToString()); writer.WriteString("readScope", item.ReadScope.ToString());
                writer.WriteString("branchInput", item.BranchInput);
                writer.WriteString("fixedSeed", item.FixedSeed?.ToString("x16", CultureInfo.InvariantCulture));
                writer.WriteString("overlayLayer", ProjectEffectsJson.OverlayName(item.OverlayLayer)); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        byte[] bytes = stream.ToArray();
        if (bytes.Length > MaximumIndexBytes) throw new IOException("脚本配置入口超过16MiB限额。");
        return bytes;
    }

    private static ScriptConfiguration ReadConfiguration(JsonElement item, Dictionary<(string, string), EffectPackageContents> packages)
    {
        Keys(item, "configuration", ["id", "name", "packageId", "revision", "exportId", "parameters", "target", "readScope", "branchInput", "fixedSeed", "overlayLayer"], []);
        string id = RequiredText(item, "packageId", "configuration"), revision = RequiredText(item, "revision", "configuration");
        if (!packages.TryGetValue((id, revision), out var package)) throw new ArgumentException("配置缺少固定修订。");
        string? seedText = NullableText(item.GetProperty("fixedSeed"), "fixedSeed"); ulong? seed = null;
        if (seedText is not null)
        {
            if (seedText.Length != 16 || !ulong.TryParse(seedText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value)
                || seedText != value.ToString("x16", CultureInfo.InvariantCulture)) throw new ArgumentException("种子必须为规范16位小写hex。");
            seed = value;
        }
        return ScriptConfiguration.Create(RequiredText(item, "id", "configuration"), RequiredText(item, "name", "configuration"), package,
            RequiredText(item, "exportId", "configuration"), EffectParameterOverrides.Parse(item.GetProperty("parameters").GetRawText()),
            EnumValue<ScriptRelativeTarget>(item, "target"), EnumValue<ScriptReadScope>(item, "readScope"),
            NullableText(item.GetProperty("branchInput"), "branchInput"), seed,
            ProjectEffectsJson.ReadOverlay(RequiredText(item, "overlayLayer", "configuration")));
    }

    private static T EnumValue<T>(JsonElement item, string name) where T : struct, Enum
    {
        string text = RequiredText(item, name, "configuration");
        return Enum.TryParse<T>(text, out var value) && Enum.IsDefined(value) && value.ToString() == text ? value : throw Type(name);
    }

    private static string? NullableText(JsonElement value, string path)
        => value.ValueKind == JsonValueKind.Null ? null : String(value, path);
}
