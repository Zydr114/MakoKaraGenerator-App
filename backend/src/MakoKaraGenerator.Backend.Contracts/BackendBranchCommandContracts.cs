using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>新增空翻译来源，沿共享用例保留 FollowPrimary 默认值。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record AddTranslationBranchCommandDto(string? Name, string? Language = null)
    : ProjectCommandDto("lyrics.addTranslationBranch");

/// <summary>完整来源元数据配置；language 必须出现，显式 null 清除原语言。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record ConfigureBranchCommandDto(string? BranchId, string? Name, string? Language,
    ProjectLyricBranchRole? Role, ProjectLyricTimingMode? TimingMode)
    : ProjectCommandDto("lyrics.configureBranch");

/// <summary>删除明确的非主来源，保留独立容器、效果和预设。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record RemoveBranchCommandDto(string? BranchId)
    : ProjectCommandDto("lyrics.removeBranch");

/// <summary>关联真实主来源行；primaryLineId 必须出现，显式 null 解除关联。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record LinkPrimaryLineCommandDto(string? BranchId, string? LineId, string? PrimaryLineId)
    : ProjectCommandDto("lyrics.linkPrimaryLine");

/// <summary>共享来源新增／删除用例给出的稳定来源选择建议。</summary>
public sealed record LyricBranchCommandResultDto(string SelectedBranchId);
