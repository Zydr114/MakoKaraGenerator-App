using System.Collections.Immutable;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Application;

/// <summary>固定的两个模板目录，不是磁盘路径或用户账号。</summary>
public enum TemplateLibraryCatalog
{
    /// <summary>本产品的只读内置目录。</summary>
    BuiltIn,
    /// <summary>当前 profile 的个人目录。</summary>
    Personal,
}
/// <summary>完整静态定义；既有21属性选择独立于工程局部样式路径。</summary>
/// <param name="Name">名称，由原工厂去除两端空白。</param>
/// <param name="Kind">实际模板类型。</param>
/// <param name="Style">完整规范静态外观，所有未选字段也必须有效。</param>
/// <param name="Properties">原有属性选择及顺序。</param>
public record StaticTemplateDefinition(string Name, StyleTemplateKind Kind, LyricStyle Style,
    ImmutableArray<LyricStyleProperty> Properties);
/// <summary>沿用原完整模板值与包闭包，不引入第二种存储格式。</summary>
public sealed record FullTemplateDefinition(string Name, StyleTemplateKind Kind, LyricStyle Style,
    ImmutableArray<LyricStyleProperty> Properties, ImmutableArray<TemplateEffectRecipe> EffectRecipes,
    ImmutableArray<EffectPackageContents> Packages) : StaticTemplateDefinition(Name, Kind, Style, Properties);
/// <summary>库编辑的闭合中立意图；不包含工程目标。</summary>
public abstract record TemplateLibraryEdit
{
    /// <summary>新建静态定义。</summary>
    /// <param name="Definition">完整静态定义。</param>
    public sealed record Create(StaticTemplateDefinition Definition) : TemplateLibraryEdit;
    /// <summary>整体更新一个静态模板，保持身份。</summary>
    /// <param name="TemplateId">精确身份。</param>
    /// <param name="Definition">完整静态定义。</param>
    public sealed record Replace(string TemplateId, StaticTemplateDefinition Definition) : TemplateLibraryEdit;
    /// <summary>从另外固定捕获的目录复制完整对象和资源。</summary>
    /// <param name="TemplateId">固定源身份。</param>
    /// <param name="Name">新名称。</param>
    public sealed record Copy(string TemplateId, string Name) : TemplateLibraryEdit;
    /// <summary>仅改名称，允许高级模板。</summary>
    /// <param name="TemplateId">精确身份。</param>
    /// <param name="Name">新名称。</param>
    public sealed record Rename(string TemplateId, string Name) : TemplateLibraryEdit;
    /// <summary>删除个人条目，不删除历史包文件。</summary>
    /// <param name="TemplateId">精确身份。</param>
    public sealed record Delete(string TemplateId) : TemplateLibraryEdit;
}
/// <summary>由服务拥有的完整不可变读取，不允许客户端提交另一份目录。</summary>
public sealed class TemplateLibrarySnapshot
{
    internal TemplateLibrarySnapshot(object owner, TemplateLibraryCatalog catalog, LibraryIndexRevision revision,
        ImmutableArray<StyleTemplate> templates)
    { Owner = owner; Catalog = catalog; Revision = revision; Templates = templates; }
    internal object Owner { get; }
    /// <summary>实际目录类别。</summary>
    public TemplateLibraryCatalog Catalog { get; }
    /// <summary>真实个人索引字节或固定内置身份字节修订；Missing独立于空库。</summary>
    public LibraryIndexRevision Revision { get; }
    /// <summary>完整模板与固定资源。</summary>
    public ImmutableArray<StyleTemplate> Templates { get; }
}
/// <summary>纯准备的固定结果，供宿主在写盘前验证完整响应预算。</summary>
public sealed class PreparedTemplateLibraryEdit
{
    internal PreparedTemplateLibraryEdit(object owner, TemplateLibrarySnapshot source, ImmutableArray<StyleTemplate> templates,
        string templateId, bool changed)
    { Owner = owner; Source = source; Templates = templates; TemplateId = templateId; Changed = changed; }
    internal object Owner { get; }
    /// <summary>准备时捕获的完整个人目录。</summary>
    public TemplateLibrarySnapshot Source { get; }
    /// <summary>候选完整目录；资源对象保持原不可变身份。</summary>
    public ImmutableArray<StyleTemplate> Templates { get; }
    /// <summary>受影响身份；新建和复制仅分配一次。</summary>
    public string TemplateId { get; }
    /// <summary>是否真正改变定义。</summary>
    public bool Changed { get; }
}
/// <summary>中立拒绝；宿主不得将底层异常的机器路径传到HTTP。</summary>
public enum TemplateLibraryEditFailure
{
    /// <summary>索引、schema或资源闭包无效。</summary>
    LibraryInvalid,
    /// <summary>模板身份在固定快照中不存在。</summary>
    TemplateNotFound,
    /// <summary>此目录只读。</summary>
    ReadOnlyLibrary,
    /// <summary>全定义替换不支持高级对象。</summary>
    AdvancedDefinitionUnsupported,
    /// <summary>新静态定义、名称或成员不合法。</summary>
    InvalidDefinition,
    /// <summary>快照不属于当前仓储。</summary>
    SnapshotStale,
}
/// <summary>中立库编辑失败，不携带磁盘路径。</summary>
public sealed class TemplateLibraryEditException : Exception
{
    /// <summary>建立稳定中立错误。</summary>
    /// <param name="failure">中立失败代码。</param>
    public TemplateLibraryEditException(TemplateLibraryEditFailure failure) : base(failure.ToString()) => Failure = failure;
    /// <summary>稳定失败代码。</summary>
    public TemplateLibraryEditFailure Failure { get; }
}
