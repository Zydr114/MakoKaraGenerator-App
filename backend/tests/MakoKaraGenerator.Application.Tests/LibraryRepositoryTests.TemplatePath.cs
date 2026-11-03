using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Application.Tests;

public sealed partial class LibraryRepositoryTests
{
    [Fact]
    public void 延迟模板入口首次成功后固定且事件订阅无需选址()
    {
        using TemporaryDirectory temp = new(); int resolutions = 0, changes = 0;
        var repository = new TemplateLibraryRepository(() =>
        { resolutions++; return StyleTemplateLibrary.ResolvePath(temp.FilePath(resolutions.ToString(System.Globalization.CultureInfo.InvariantCulture))); });
        void Changed(StyleTemplateLibraryLoadResult _) => changes++;
        repository.Changed += Changed; Assert.Equal(0, resolutions);
        var service = new TemplateLibraryEditService(repository);
        Assert.NotEmpty(service.Read(TemplateLibraryCatalog.BuiltIn).Templates); Assert.Equal(0, resolutions);
        var empty = repository.Read(); string path = repository.Path;
        repository.Save(empty.Revision, [Template("one")]); Assert.Equal(1, changes);
        repository.Changed -= Changed;
        repository.Save(repository.ReadRevision(), [Template("two")]);
        Assert.Equal(1, changes); Assert.Equal(1, resolutions); Assert.Equal(path, repository.Path);
        Assert.Equal("two", Assert.Single(repository.Read().Templates).Id);
    }

    [Fact]
    public void 延迟模板选址失败不缓存异常且保留事件订阅()
    {
        using TemporaryDirectory temp = new(); int resolutions = 0, changes = 0;
        var repository = new TemplateLibraryRepository(() =>
        {
            if (++resolutions == 1) throw new IOException("not yet available");
            return StyleTemplateLibrary.ResolvePath(temp.Path);
        });
        repository.Changed += _ => changes++;
        Assert.Throws<IOException>(() => repository.Read());
        var empty = repository.Read(); repository.Save(empty.Revision, [Template("one")]);
        Assert.Equal(2, resolutions); Assert.Equal(1, changes);
    }
}
