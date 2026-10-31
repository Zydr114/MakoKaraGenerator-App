namespace MakoKaraGenerator.Scripting;

/// <summary>运行控制器的固定包／内容修订／导出身份，与 C# 编译产物身份独立。</summary>
public sealed record EffectPackageIdentity
{
    /// <summary>建立真实包导出身份；修订要求完整小写 SHA-256，不从 DLL hash 推导。</summary>
    public EffectPackageIdentity(string packageId, string revision, string exportId)
    {
        ResourceIdentity.ValidateRevision(packageId, revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportId);
        PackageId = packageId; Revision = revision; ExportId = exportId;
    }
    /// <summary>固定包身份。</summary>
    public string PackageId { get; }
    /// <summary>完整包内容修订。</summary>
    public string Revision { get; }
    /// <summary>实际运行的控制器导出。</summary>
    public string ExportId { get; }
}
