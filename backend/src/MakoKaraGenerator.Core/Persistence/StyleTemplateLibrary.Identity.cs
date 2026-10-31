using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Persistence;

public static partial class StyleTemplateLibrary
{
    /// <summary>只读身份编码，复用唯一规范 writer，允许现有内置身份；不授予个人库发布权限。</summary>
    /// <param name="templates">已捕获的完整模板源对象及固定资源引用。</param>
    /// <returns>含完整定义和包修订清单的规范身份字节；不包含另一份包内容。</returns>
    /// <remarks>此入口不修复或降级旧定义。个人库 Encode、Validate 与 Save 的原校验保持。</remarks>
    public static byte[] EncodeForIdentity(IEnumerable<StyleTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        StyleTemplate[] items = [.. templates];
        if (items.Any(template => template is null || string.IsNullOrWhiteSpace(template.Id))
            || items.Select(template => template.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("模板身份缺失或重复。", nameof(templates));
        return EncodeItems(items);
    }
}
