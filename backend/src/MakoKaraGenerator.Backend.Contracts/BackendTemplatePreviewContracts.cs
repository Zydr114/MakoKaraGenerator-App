using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>模板固定试样的版本化样本身份。</summary>
public static class TemplatePreviewLimits
{
    /// <summary>兼容原三秒固定试样。</summary>
    public const string DefaultSampleId = "default-v1";
    /// <summary>原工坊的单行十二秒试样。</summary>
    public const string SingleLineSampleId = "workshop-single-line-v1";
    /// <summary>原工坊的多行十二秒试样。</summary>
    public const string MultiLineSampleId = "workshop-multi-line-v1";
    public const int MaxDraftRevisionLength = 256;
}

[JsonConverter(typeof(TemplatePreviewSourceJsonConverter))]
public sealed record TemplatePreviewSourceDto(
    string Kind,
    string? LibraryId = null,
    string? LibraryRevision = null,
    string? TemplateId = null,
    string? TemplateRevision = null,
    string? DraftRevision = null,
    StaticTemplateDefinitionDto? Definition = null,
    string? DefinitionFormat = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TemplatePreviewSessionRequest(
    TemplatePreviewSourceDto Source,
    string SampleId,
    PreviewSizeDto Size,
    PreviewAlphaMode AlphaMode = PreviewAlphaMode.Opaque,
    RequestMetadataDto? Metadata = null,
    string? TargetKey = null,
    string? MaterialSlot = null,
    string? RenderBackend = null);

/// <summary>只读解析完整模板，返回原工坊试样选项。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TemplatePreviewOptionsRequest(TemplatePreviewSourceDto Source);

/// <summary>同一固定模板来源可用的工坊试样、材质槽位和时长。</summary>
public sealed record TemplatePreviewOptionsResponse(PreviewSourceDto Source,
    TemplateSampleOptionDto[] Samples, TemplateMaterialSlotOptionDto[] MaterialSlots,
    string DefaultSampleId = TemplatePreviewLimits.SingleLineSampleId,
    string ProtocolVersion = BackendProtocol.Version);

/// <summary>来自原工坊的版本化样本；目标由正式兼容判据筛选。</summary>
public sealed record TemplateSampleOptionDto(string SampleId, bool MultiLine, string DurationUs,
    TemplateSampleTargetDto[] Targets);

/// <summary>稳定目标键及原领域绑定；不是当前工程的目标。</summary>
public sealed record TemplateSampleTargetDto(string Key, TemplateSampleBindingDto Binding);

/// <summary>原领域绑定的完整只读投影。</summary>
public sealed record TemplateSampleBindingDto(string Kind, string? Id, string[] Ids, bool IncludeDescendants);

/// <summary>原工坊三材质位置；Selected说明当前属性选择，Present区别无描边；焦点不隔离整模板。</summary>
public sealed record TemplateMaterialSlotOptionDto(string Slot, bool Editable, bool Selected, bool Present,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] StyleMaterialDto? Material);

/// <summary>模板试样已固定的样本/目标与可选材质编辑焦点，焦点不改变整模板应用。</summary>
public sealed record TemplatePreviewSelectionDto(string SampleId, string TargetKey, string? MaterialSlot = null);
