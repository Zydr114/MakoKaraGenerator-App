using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Media;

/// <summary>音频来源的稳定身份；普通工程编辑和显示文件名不改变该身份。</summary>
public sealed record AudioSourceIdentity
{
    private AudioSourceIdentity(string path, string? hash, long? byteLength)
    { CanonicalPath = path; RevisionHash = hash; ByteLength = byteLength; }

    /// <summary>完整来源路径；Windows下按不区分大小写的文件系统语义规范化。</summary>
    public string CanonicalPath { get; }
    /// <summary>工程已接受的媒体修订摘要；未保存外部来源可为空。</summary>
    public string? RevisionHash { get; }
    /// <summary>已接受修订的真实字节数。</summary>
    public long? ByteLength { get; }

    /// <summary>读取工程中的音频身份；没有音频时返回空。</summary>
    public static AudioSourceIdentity? FromProject(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.AudioPath is { } path
            ? new(NormalizePath(path), project.AudioMedia?.Hash, project.AudioMedia?.ByteLength) : null;
    }

    internal static string NormalizePath(string path)
    {
        string full = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }
}
