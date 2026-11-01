using System.Text.Json;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Projects;
using static MakoKaraGenerator.Core.Diagnostics.DiagnosticList;

namespace MakoKaraGenerator.Core.Persistence;

internal static partial class ProjectDtoMapper
{
    private static GlowSpecDto? ToDto(GlowSpec? glow) => glow is null ? null : new()
    {
        Color = glow.Color.ToHex(), Opacity = glow.Opacity,
        BlurRadiusPx = glow.BlurRadiusPx, SpreadPx = glow.SpreadPx,
    };

    private static JsonElement ToOptionalGlowDto(OptionalValue<GlowSpecOverride?> value) =>
        value.IsSet ? JsonSerializer.SerializeToElement(value.Value is { } glow ? new GlowSpecOverrideDto
        {
            Color = glow.Color?.ToHex(), Opacity = glow.Opacity,
            BlurRadiusPx = glow.BlurRadiusPx, SpreadPx = glow.SpreadPx,
        } : null, ProjectJson.Options) : default;

    private static GlowSpec? MapGlowSpec(GlowSpecDto dto, string location, List<ProjectIssue> issues)
    {
        if (dto.Color is null || !RgbaColor.TryParse(dto.Color, out RgbaColor color))
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "辉光颜色无效。", location + ".color");
            return null;
        }
        GlowSpec result = new()
        {
            Color = color, Opacity = dto.Opacity ?? 1f,
            BlurRadiusPx = dto.BlurRadiusPx ?? 8f, SpreadPx = dto.SpreadPx ?? 0f,
        };
        if (result.IsValid) return result;
        Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "辉光参数无效。", location);
        return null;
    }

    private static OptionalValue<GlowSpecOverride?> MapOptionalGlowOverride(
        JsonElement value, string location, List<ProjectIssue> issues)
    {
        if (value.ValueKind == JsonValueKind.Undefined) return default;
        if (value.ValueKind == JsonValueKind.Null) return OptionalValue.Set<GlowSpecOverride?>(null);
        try
        {
            GlowSpecOverrideDto dto = JsonSerializer.Deserialize<GlowSpecOverrideDto>(value, ProjectJson.Options)!;
            GlowSpecOverride result = new()
            {
                Color = MapOptionalStyleColor(dto.Color, location + ".color", issues),
                Opacity = dto.Opacity, BlurRadiusPx = dto.BlurRadiusPx, SpreadPx = dto.SpreadPx,
            };
            if (!result.IsValid)
                Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, "辉光覆盖参数无效。", location);
            return OptionalValue.Set<GlowSpecOverride?>(result);
        }
        catch (JsonException exception)
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error,
                "辉光覆盖不是有效对象：" + exception.Message, location);
            return default;
        }
    }
}
