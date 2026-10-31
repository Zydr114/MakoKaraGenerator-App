using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>精确修订读模型的固定响应预算。</summary>
public static class ProjectReadModelLimits
{
    /// <summary>每页最多来源或歌词行数。</summary>
    public const int MaxPageSize = 100;
    /// <summary>完整响应 JSON 的最大 UTF-8 字节数。</summary>
    public const int MaxResponseBytes = 1024 * 1024;
    /// <summary>来源、行、元素、注音组和注音片段的合计最大项数。</summary>
    public const int MaxItems = 20000;
}

/// <summary>歌词来源的产品角色。</summary>
public enum ProjectLyricBranchRole
{
    /// <summary>主歌词。</summary>
    Primary,
    /// <summary>翻译。</summary>
    Translation,
    /// <summary>读音来源。</summary>
    Annotation,
    /// <summary>自定义来源。</summary>
    Custom,
}

/// <summary>来源保存的时间来源策略，不合成跟随时间。</summary>
public enum ProjectLyricTimingMode
{
    /// <summary>独立时间。</summary>
    Independent,
    /// <summary>跟随关联主行。</summary>
    FollowPrimary,
    /// <summary>没有时间。</summary>
    None,
}

/// <summary>行或元素保存的时间状态。</summary>
public enum ProjectLyricTimingStatus
{
    /// <summary>时间缺失。</summary>
    Missing,
    /// <summary>从文件导入。</summary>
    Imported,
    /// <summary>估算时间。</summary>
    Estimated,
    /// <summary>用户编辑。</summary>
    Edited,
    /// <summary>用户确认。</summary>
    Confirmed,
    /// <summary>保存的时间非法。</summary>
    Invalid,
}

/// <summary>注音与基础字的水平对齐。</summary>
public enum ProjectRubyAlignment
{
    /// <summary>居中。</summary>
    Center,
    /// <summary>起点。</summary>
    Start,
    /// <summary>终点。</summary>
    End,
}

/// <summary>来源元数据，不包含样式或场景。</summary>
public sealed record ProjectLyricBranchDto(string Id, string Name, ProjectLyricBranchRole Role,
    string? Language, ProjectLyricTimingMode TimingMode, int LineCount);

/// <summary>同一精确修订下的有界来源列表；终页省略 nextOffset。</summary>
public sealed record ProjectLyricBranchesResponse(string ProjectSessionId, string ProjectRevision,
    string PrimaryBranchId, int Offset, int TotalCount, int? NextOffset, ProjectLyricBranchDto[] Branches,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>注音片段的完整 HTTP 读模型。</summary>
public sealed record ProjectRubyTextSegmentDto(string Id, string Text, LyricTimeDto Time,
    ProjectLyricTimingStatus TimingStatus);

/// <summary>完整注音元数据；兼容 Text 投影并携带独立片段时间。</summary>
public sealed record ProjectRubyAnnotationDto(string Id, string BaseTokenId, string Text, string? Language,
    double RelativeScale, double GapPx, ProjectRubyAlignment Alignment, ProjectRubyTextSegmentDto[] Segments);

/// <summary>元素正文与原始时间状态。</summary>
public sealed record ProjectLyricTokenDto(string Id, string Text, LyricTimeDto Time, ProjectLyricTimingStatus TimingStatus);

/// <summary>行正文、原始时间、关联主行和注音的完整编辑投影。</summary>
public sealed record ProjectLyricLineDto(string BranchId, string Id, string Text, LyricTimeDto Time,
    ProjectLyricTimingStatus TimingStatus, string? PrimaryLineId, ProjectLyricTokenDto[] Tokens,
    ProjectRubyAnnotationDto[] RubyAnnotations);

/// <summary>绑定精确修订的歌词窗口；不会截断单行或拼接不同修订。</summary>
public sealed record ProjectLyricsWindowResponse(string ProjectSessionId, string ProjectRevision, string BranchId,
    int Offset, int TotalCount, int? NextOffset, ProjectLyricLineDto[] Lines,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>工程标量设置；所有微秒字段采用十进制字符串。</summary>
public sealed record ProjectSettingsResponse(string ProjectSessionId, string ProjectRevision, string Name,
    int CanvasWidth, int CanvasHeight, int FrameRateNumerator, int FrameRateDenominator,
    byte BackgroundRed, byte BackgroundGreen, byte BackgroundBlue, byte BackgroundAlpha,
    string? OutputDurationUs, string AudioDurationUs, string ProtocolVersion = BackendProtocol.Version,
    ProjectBackgroundDto? Background = null);

public sealed record ProjectBackgroundDto(string Kind, string Fit, string Playback, string? ResourceId);

/// <summary>工程内预设应用的完整目标 union；每条应用指向一个具体对象，clips 保留完整片段绑定及所属分支。</summary>
public sealed record ProjectPresetApplicationTargetDto(string Kind,
    string? BranchId = null, string? LineId = null, string? TokenId = null,
    string? NodeId = null, string[]? ClipIds = null, bool IncludeDescendants = false);

/// <summary>固定预设快照的有界摘要；不暴露效果包、脚本或运行身份闭包。</summary>
public sealed record ProjectPresetApplicationSnapshotDto(string TemplateId, string Name, TemplateKind Kind,
    TemplateProperty[] Properties, bool HasEffectRecipes, bool HasPackageMaterials, bool HasPackages);

/// <summary>当前模型没有保存模板库来源修订；显式报告不可比较，禁止把定义哈希当作库修订。</summary>
public sealed record ProjectPresetApplicationSourceDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LibraryId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Revision,
    bool Comparable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? NewVersionAvailable);

/// <summary>工程预设应用的单项只读投影。</summary>
public sealed record ProjectPresetApplicationDto(string ApplicationId, ProjectPresetApplicationTargetDto Target,
    bool TargetValid, bool Enabled, ProjectPresetApplicationSnapshotDto Snapshot,
    ProjectPresetApplicationSourceDto Source, Dictionary<string, string> BranchInputs);

/// <summary>绑定精确工程修订的完整预设应用列表；超出统一读模型预算时整批拒绝。</summary>
public sealed record ProjectPresetApplicationsResponse(string ProjectSessionId, string ProjectRevision,
    ProjectPresetApplicationDto[] Applications, string ProtocolVersion = BackendProtocol.Version);


/// <summary>精确工程修订下的歌词编排结构树与分支默认配置。</summary>
public sealed record ProjectOrchestrationResponse(string ProjectSessionId, string ProjectRevision,
    ProjectOrchestrationNodeDto[] Nodes, ProjectOrchestrationProgramDto[] Programs, string ProtocolVersion = BackendProtocol.Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ProjectLineEnterAnimationDto? ProjectEnterAnimation = null);
public sealed record ProjectOrchestrationNodeDto(string Id, string? Name, string? ParentId,
    ProjectOrchestrationPlacementDto Placement, ProjectOrchestrationReferenceRectDto ReferenceRect,
    ProjectOrchestrationLayoutDto? Layout, string? ContentKind, ProjectOrchestrationContentDto? Content = null);
public sealed record ProjectOrchestrationPlacementDto(double X, double Y, string Anchor, double RotationDegrees, double Scale);
public sealed record ProjectOrchestrationReferenceRectDto(double WidthRatio, double HeightRatio);
public sealed record ProjectOrchestrationLayoutDto(string Kind, ProjectOrchestrationPlacementDto? DefaultPlacement,
    ProjectOrchestrationSafeAreaDto SafeArea, double MaxWidthRatio, string[] SlotIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ProjectOrchestrationSlotAnimationDto? SlotAnimation = null);
public sealed record ProjectOrchestrationSafeAreaDto(double Left, double Top, double Right, double Bottom);
public sealed record ProjectOrchestrationClipScheduleContextDto(string[] LineIds, bool IsDefaultSegment,
    string? PreviousLineId, string? NextLineId);
public sealed record ProjectOrchestrationClipDto(string Id, string? Name, string[] LineIds, string ContainerId,
    ProjectOrchestrationClipScheduleContextDto? ScheduleContext);
public sealed record ProjectOrchestrationProgramDto(string Id, string BranchId, string RootContainerId, string DefaultContainerId, string? AnimationPolicy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RecognizedLayoutPreset = null,
    ProjectOrchestrationClipDto[]? Clips = null, ProjectOrchestrationLeadInDto[]? LeadIns = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] LeadInGenerationSettingsDto? LeadInGeneration = null);
