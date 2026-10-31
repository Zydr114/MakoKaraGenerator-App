using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

public static class PresetApplicationLimits
{
    public const int MaxIntentBytes = 64 * 1024;
}

[JsonConverter(typeof(PresetApplicationTargetJsonConverter))]
public sealed record PresetApplicationTargetDto(string Kind, string? BranchId = null, string? LineId = null, string? TokenId = null,
    string? NodeId = null, bool? IncludeDescendants = null, string[]? ClipIds = null);

[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ApplyPresetApplicationCommandDto(string? TemplateLibraryId, string? TemplateId, string? TemplateRevision, PresetApplicationTargetDto[]? Targets,
    Dictionary<string, string>? BranchInputs) : ProjectCommandDto("presetApplications.apply");
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record UpdatePresetApplicationCommandDto(string? ApplicationId, string? TemplateLibraryId, string? TemplateId, string? TemplateRevision,
    Dictionary<string, string>? BranchInputs) : ProjectCommandDto("presetApplications.update");
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record SetPresetApplicationEnabledCommandDto(string? ApplicationId, bool? Enabled)
    : ProjectCommandDto("presetApplications.setEnabled");
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record RemovePresetApplicationCommandDto(string[]? ApplicationIds)
    : ProjectCommandDto("presetApplications.remove");
