namespace MakoKaraGenerator.Core.Persistence;

/// <summary>库入口、不可变资源和协作锁共用的受管路径检查。</summary>
public static class LibraryStoragePath
{
    /// <summary>拒绝自身或任意父目录的链接，不通过别名进入另一个库。</summary>
    public static void RequireNoLinks(string path) => ContentAddressFileStore.EnsureNoLinks(path);
}
