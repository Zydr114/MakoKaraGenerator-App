using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application;

/// <summary>无工程上下文的完整库读取、纯准备及原仓储CAS提交。</summary>
public sealed class TemplateLibraryEditService
{
    private readonly TemplateLibraryRepository _repository;
    private readonly TemplateLibrarySnapshot _builtIn;
    /// <summary>使用可信路径的既有仓储，不自动读取或迁移桌面库。</summary>
    /// <param name="repository">个人库的唯一仓储入口。</param>
    public TemplateLibraryEditService(TemplateLibraryRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository); _repository = repository;
        var templates = StyleTemplateLibrary.BuiltIns.ToImmutableArray();
        _builtIn = new(this, TemplateLibraryCatalog.BuiltIn,
            LibraryIndexRevision.FromBytes(StyleTemplateLibrary.EncodeForIdentity(templates)), templates);
    }
    /// <summary>内置读取独立于个人库及其锁；个人读取完整验证，不以空目录代替损坏。</summary>
    /// <param name="catalog">固定目录。</param>
    /// <returns>由本服务拥有的固定快照。</returns>
    public TemplateLibrarySnapshot Read(TemplateLibraryCatalog catalog)
    {
        if (catalog == TemplateLibraryCatalog.BuiltIn) return _builtIn;
        if (catalog != TemplateLibraryCatalog.Personal) throw new ArgumentOutOfRangeException(nameof(catalog));
        var loaded = _repository.Read();
        if (loaded.VersionMismatch || loaded.Issues.Count != 0)
            throw new TemplateLibraryEditException(TemplateLibraryEditFailure.LibraryInvalid);
        return new(this, catalog, loaded.Revision, loaded.Templates.ToImmutableArray());
    }
    /// <summary>纯准备，保持高级对象的参数与包；不改磁盘、工程或任何历史。</summary>
    /// <param name="destination">已捕获的个人库。</param>
    /// <param name="edit">完整中立意图。</param>
    /// <param name="copySource">copy的固定源快照；不构成跨库原子事务。</param>
    /// <returns>新身份固定且可先作预算的完整候选。</returns>
    public PreparedTemplateLibraryEdit Prepare(TemplateLibrarySnapshot destination, TemplateLibraryEdit edit,
        TemplateLibrarySnapshot? copySource = null)
    {
        ArgumentNullException.ThrowIfNull(destination); ArgumentNullException.ThrowIfNull(edit);
        RequireOwn(destination);
        if (destination.Catalog != TemplateLibraryCatalog.Personal)
            throw new TemplateLibraryEditException(TemplateLibraryEditFailure.ReadOnlyLibrary);
        ImmutableArray<StyleTemplate> items = destination.Templates; string id; StyleTemplate? before = null, after = null;
        switch (edit)
        {
            case TemplateLibraryEdit.Create create:
                id = NewId(items); after = FromDefinition(id, create.Definition); break;
            case TemplateLibraryEdit.Copy copy:
                if (copySource is null) throw new TemplateLibraryEditException(TemplateLibraryEditFailure.SnapshotStale);
                RequireOwn(copySource); var original = Find(copySource, copy.TemplateId);
                id = NewId(items); after = original with { Id = id, Name = Name(copy.Name) }; break;
            case TemplateLibraryEdit.Replace replace:
                before = Find(destination, replace.TemplateId); id = before.Id;
                if (replace.Definition is not FullTemplateDefinition && !IsStatic(before))
                    throw new TemplateLibraryEditException(TemplateLibraryEditFailure.AdvancedDefinitionUnsupported);
                after = FromDefinition(id, replace.Definition); break;
            case TemplateLibraryEdit.Rename rename:
                before = Find(destination, rename.TemplateId); id = before.Id; after = before with { Name = Name(rename.Name) }; break;
            case TemplateLibraryEdit.Delete delete:
                before = Find(destination, delete.TemplateId); id = before.Id; break;
            default: throw new TemplateLibraryEditException(TemplateLibraryEditFailure.InvalidDefinition);
        }
        bool changed = before is null || after is null || !before.Equals(after);
        if (!changed) return new(this, destination, items, id, false);
        items = before is null ? items.Add(after!) : after is null ? items.Remove(before) : items.SetItem(items.IndexOf(before), after);
        // Check the same full format ceiling before any package or index can be published.
        _ = StyleTemplateLibrary.Encode(items);
        return new(this, destination, items, id, true);
    }
    /// <summary>整批导入已验证的完整模板闭包，全部生成新身份；不编译或执行包内容。</summary>
    /// <param name="destination">本服务拥有的个人库快照。</param>
    /// <param name="templates">归档已完整验证的定义；输入顺序与新身份映射保持一致。</param>
    /// <returns>同一仓储一次CAS提交的完整候选。</returns>
    public PreparedTemplateLibraryEdit PrepareImport(TemplateLibrarySnapshot destination, IEnumerable<StyleTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(destination); ArgumentNullException.ThrowIfNull(templates);
        RequireOwn(destination);
        if (destination.Catalog != TemplateLibraryCatalog.Personal)
            throw new TemplateLibraryEditException(TemplateLibraryEditFailure.ReadOnlyLibrary);
        var items = destination.Templates;
        string lastId = "";
        foreach (StyleTemplate template in templates)
        {
            ArgumentNullException.ThrowIfNull(template);
            lastId = NewId(items);
            items = items.Add(template with { Id = lastId });
        }
        _ = StyleTemplateLibrary.Encode(items);
        return new(this, destination, items, lastId, items.Length != destination.Templates.Length);
    }

    /// <summary>提交先前固定候选；真正CAS由原仓储在协作锁与发布点再次核对。</summary>
    /// <param name="prepared">通过宿主预算的固定候选。</param>
    /// <param name="cancellationToken">提交前取消；发布后的迟到取消不改成功结果。</param>
    /// <returns>实际已提交或同锁复核的完整快照。</returns>
    public async Task<TemplateLibrarySnapshot> CommitPreparedAsync(PreparedTemplateLibraryEdit prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(prepared.Owner, this)) throw new TemplateLibraryEditException(TemplateLibraryEditFailure.SnapshotStale);
        var loaded = prepared.Changed
            ? await _repository.SaveAsync(prepared.Source.Revision, prepared.Templates, cancellationToken).ConfigureAwait(false)
            : await _repository.CheckCurrentAsync(prepared.Source.Revision, cancellationToken).ConfigureAwait(false);
        return new(this, TemplateLibraryCatalog.Personal, loaded.Revision, loaded.Templates.ToImmutableArray());
    }
    /// <summary>静态能力按完整对象判断，包括未选材质槽；不把高级对象降级。</summary>
    /// <param name="template">完整定义。</param>
    /// <returns>是否没有配方、包及包材质。</returns>
    public static bool IsStatic(StyleTemplate template) => template.EffectRecipes.IsEmpty && template.Packages.IsEmpty
        && SourceMaterialCatalog.Collect(template.Style).IsEmpty;

    private void RequireOwn(TemplateLibrarySnapshot snapshot)
    { if (!ReferenceEquals(snapshot.Owner, this)) throw new TemplateLibraryEditException(TemplateLibraryEditFailure.SnapshotStale); }
    private static StyleTemplate Find(TemplateLibrarySnapshot snapshot, string id) =>
        snapshot.Templates.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))
        ?? throw new TemplateLibraryEditException(TemplateLibraryEditFailure.TemplateNotFound);
    private static string Name(string name) => !string.IsNullOrWhiteSpace(name) ? name.Trim()
        : throw new TemplateLibraryEditException(TemplateLibraryEditFailure.InvalidDefinition);
    private static string NewId(ImmutableArray<StyleTemplate> items)
    {
        string id;
        do { id = Guid.NewGuid().ToString("N"); } while (items.Any(item => item.Id == id));
        return id;
    }
    private static StyleTemplate FromDefinition(string id, StaticTemplateDefinition definition)
    {
        if (definition is null || definition.Style is not { IsValid: true } || definition.Properties.IsDefault
            || (definition is not FullTemplateDefinition && !SourceMaterialCatalog.Collect(definition.Style).IsEmpty))
            throw new TemplateLibraryEditException(TemplateLibraryEditFailure.InvalidDefinition);
        try
        {
            return definition is FullTemplateDefinition full
                ? StyleTemplate.Create(id, full.Name, full.Kind, full.Style, full.Properties, full.EffectRecipes, full.Packages)
                : StyleTemplate.Create(id, definition.Name, definition.Kind, definition.Style, definition.Properties);
        }
        catch (Exception error) when (error is ArgumentException or EffectPackageException)
        { throw new TemplateLibraryEditException(TemplateLibraryEditFailure.InvalidDefinition); }
    }
}
