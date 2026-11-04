using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>开发期显式并存的包合同；不会从产品版本猜测 SDK 或授予未实现能力。</summary>
public static class EffectPackageProfiles
{
    /// <summary>保留至 v2 发布验收结束的有状态 SDK v1 包合同。</summary>
    public static EffectPackageProfile V1 { get; } = new(2, "1.0.0", ShaderProgramDefinition.RasterProfile);
    /// <summary>纯求值 SDK v2 包合同；包 profile 与内部 shader 程序 profile 分别校验。</summary>
    public static EffectPackageProfile V2 { get; } = new(3, "2.0.0", "skia-ganesh-3.119.4-v2");
    // 只由源码集成开通；不能由清单、环境变量或作者代码绕过未接通执行器。
    internal const bool EnableV2ParticlesAndVisuals = true;
    /// <summary>当前已接通的 v2 必须能力；后续能力在实际接通前明确拒绝。</summary>
    public static IReadOnlySet<string> SupportedV2Capabilities { get; } = BuildV2Capabilities();

    private static ImmutableHashSet<string> BuildV2Capabilities()
    {
        var text = ImmutableHashSet.Create(StringComparer.Ordinal,
        "token.affine", "line.affine", "group.affine", "mark.affine", "cluster.affine", "token.opacity", "group.opacity",
        "style.fill", "style.stroke", "style.shadow", "style.glow", "timeline.index", "line.exit",
        "token.warp", "cluster.warp", "mark.warp", "visuals.paths", "image.graph",
        EffectPackageSvgResources.VectorCapability, EffectPackageSvgResources.AtlasCapability);
        return EnableV2ParticlesAndVisuals ? text.Union(["particles.continuous", "particles.burst", "visuals.dots",
            "visuals.segments", "visuals.trails", "visuals.sprites"]) : text;
    }

    /// <summary>是否属于已支持的 SDK v2 包身份；不只比较 SDK 数字。</summary>
    public static bool IsV2(EffectPackageProfile profile)
        => profile is not null && profile.PackageVersion == V2.PackageVersion && profile.ShaderProfile == V2.ShaderProfile
            && TryVersion(profile.SdkVersion, out int major, out int minor) && major == 2 && minor == 0;
    /// <summary>是否是明确支持的开发期 v1 或 v2 包身份。</summary>
    public static bool IsSupported(EffectPackageProfile profile)
        => profile == V1 || IsV2(profile);
    /// <summary>当前包执行合同采用的独立 SkSL 程序 ABI；不将 v2 的 Vulkan 执行身份当作程序格式。</summary>
    public static string ShaderProgramProfile(EffectPackageProfile profile)
    {
        RequireSupported(profile);
        return ShaderProgramDefinition.RasterProfile;
    }
    /// <summary>拒绝未知包／SDK／shader profile，不将未知身份回退成 v1。</summary>
    public static void RequireSupported(EffectPackageProfile profile)
    {
        if (!IsSupported(profile)) throw Mismatch();
    }
    /// <summary>只为选择严格读取器检查清单身份；完整字段、字节与能力随后仍须验证。</summary>
    public static EffectPackageProfile SelectSupported(ReadOnlyMemory<byte> manifest)
    {
        using JsonDocument json = EffectPackageJson.ReadJson(manifest, "manifest.json");
        JsonElement root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("packageVersion", out var value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int version)) throw Mismatch();
        var profile = new EffectPackageProfile(version, EffectPackageJson.RequiredText(root, "sdkVersion", "manifest.json"),
            EffectPackageJson.RequiredText(root, "shaderProfile", "manifest.json"));
        RequireSupported(profile);
        return IsV2(profile) ? V2 : V1;
    }

    internal static bool Compatible(EffectPackageProfile actual, EffectPackageProfile supported)
    {
        return IsV2(supported) ? IsV2(actual) : actual == supported;
    }
    internal static void RequireCapability(string capability, string path)
    {
        if (!SupportedV2Capabilities.Contains(capability))
            throw new EffectPackageException("PackageCapabilityUnsupported", path, "宿主尚不支持必须能力：" + capability);
    }
    // SVG 描述可在执行器开通前完整验证；只能由 Definition 的 supported 门禁批准运行。
    internal static void RequireKnownCapability(string capability, string path)
    {
        if (capability is EffectPackageSvgResources.VectorCapability or EffectPackageSvgResources.AtlasCapability) return;
        RequireCapability(capability, path);
    }
    private static bool TryVersion(string text, out int major, out int minor)
    {
        major = minor = -1;
        if (string.IsNullOrEmpty(text)) return false;
        string[] parts = text.Split('.');
        if (parts.Length != 3) return false;
        int[] values = new int[3];
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length == 0 || parts[i].Length > 1 && parts[i][0] == '0'
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) return false;
        major = values[0]; minor = values[1]; return true;
    }
    private static EffectPackageException Mismatch()
        => new("PackageProfileMismatch", "manifest.json", "包版本、SDK 或 shader profile 不属于明确支持的 v1／v2 合同。");
}
