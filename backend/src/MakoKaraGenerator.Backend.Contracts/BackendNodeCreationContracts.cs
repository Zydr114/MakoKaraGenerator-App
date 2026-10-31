using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>有限节点创建与子树复制的准入预算。</summary>
public static class OrchestrationNodeCreationLimits
{
    /// <summary>单次命令 JSON 字节预算。</summary>
    public const int MaxIntentBytes = 16 * 1024;
    /// <summary>源树和候选树的节点数量上限。</summary>
    public const int MaxNodes = 2000;
    /// <summary>节点深度上限；画布直接子节点计为一层。</summary>
    public const int MaxDepth = 64;
    /// <summary>单次复制的完整子树节点上限。</summary>
    public const int MaxCopiedNodes = 128;
    /// <summary>一次创建的槽位数量上限。</summary>
    public const int MaxSlots = 64;
    /// <summary>命令名称的 UTF-16 长度上限。</summary>
    public const int MaxNameLength = 256;
    /// <summary>源与候选的节点、程序、引用、覆盖及歌词分支／行／元素的验证遍历项总预算；独立于编排输出项数。</summary>
    public const int MaxInputItems = ProjectReadModelLimits.MaxItems;
    /// <summary>源与候选读取的累计身份、名称和标记正文 UTF-16 长度预算；完整 JSON 字节预算另行检查。</summary>
    public const int MaxInputCharacters = ProjectReadModelLimits.MaxResponseBytes;
}

/// <summary>创建 group、auto 或 slotSequence；身份由服务器生成，槽位与容器原子创建。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record CreateOrchestrationNodeCommandDto(string? Kind, string? ParentId, string? Name, int? SlotCount)
    : ProjectCommandDto("orchestration.createNode");

/// <summary>同父复制完整节点子树；名称显式为空时清除副本根名称，不复制任何展示绑定。</summary>
[JsonConverter(typeof(ProjectCommandJsonConverter))]
public sealed record CopyOrchestrationSubtreeCommandDto(string? NodeId, string? Name)
    : ProjectCommandDto("orchestration.copySubtree");
