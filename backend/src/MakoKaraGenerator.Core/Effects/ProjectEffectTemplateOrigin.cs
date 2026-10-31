using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>宿主覆盖的临时图元层；Package保留作者逐输出设置。</summary>
public enum EffectOverlayLayer
{
    /// <summary>使用包的描述。</summary>
    Package,
    /// <summary>背景之后、全部文字之前。</summary>
    BehindText,
    /// <summary>全部文字之后。</summary>
    AboveText,
}

/// <summary>模板套用来源；真实目标只保存在单目标Binding中。</summary>
public sealed record ProjectEffectTemplateOrigin
{
    /// <summary>固定模板／配方身份以及本次套用的行或元素。</summary>
    public ProjectEffectTemplateOrigin(string templateId, string recipeId, StyleTemplateKind ownerKind, string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId); ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId); if (!Enum.IsDefined(ownerKind)) throw new ArgumentOutOfRangeException(nameof(ownerKind));
        TemplateId = templateId; RecipeId = recipeId; OwnerKind = ownerKind; OwnerId = ownerId;
    }
    /// <summary>原模板身份，不读取外部模板。</summary>
    public string TemplateId { get; }
    /// <summary>原模板内配方身份。</summary>
    public string RecipeId { get; }
    /// <summary>套用的行或元素。</summary>
    public StyleTemplateKind OwnerKind { get; }
    /// <summary>套用所有者的稳定源ID。</summary>
    public string OwnerId { get; }
}
