using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Projects;
using static MakoKaraGenerator.Core.Diagnostics.DiagnosticList;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>完整媒体闭包收拢；所有保存和备份都流式发布音频及背景。</summary>
public static class ProjectAssetCollector
{
    /// <summary>真实内容地址媒体目录，旧assets目录不删除。</summary>
    public const string AssetsDirectoryName = "data/media";
    /// <summary>发布完整资源并返回写盘位置；不改变输入工程或历史。</summary>
    public static Project Collect(Project project, string directoryPath, List<ProjectIssue> issues, ProjectStoreLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath); ArgumentNullException.ThrowIfNull(issues);
        limits ??= new(); if (!limits.IsValid) throw new ArgumentException("持久IO限额必须为正。", nameof(limits));
        string? audioPath = project.AudioPath; var audio = project.AudioMedia; var background = project.Background;
        if (audioPath is null && audio is not null) throw new IOException("没有音频源时不能保留媒体修订。");
        if (background.Kind == BackgroundKind.Color && (background.Path is not null || background.Media is not null)) throw new IOException("纯色背景不能保留媒体源。");
        if (audioPath is not null)
        {
            var revision = Publish(audioPath, audio, "音频");
            audioPath = Path.Combine(Path.GetFullPath(directoryPath), revision.RelativePath); audio = revision;
        }
        if (background.Kind != BackgroundKind.Color)
        {
            if (string.IsNullOrWhiteSpace(background.Path)) throw new IOException("媒体背景没有真实源路径。");
            var revision = Publish(background.Path, background.Media, background.Kind == BackgroundKind.Video ? "背景视频" : "背景图片");
            background = background with { Path = Path.Combine(Path.GetFullPath(directoryPath), revision.RelativePath), Media = revision };
        }
        return audioPath == project.AudioPath && audio == project.AudioMedia && background == project.Background ? project
            : project with { AudioPath = audioPath, AudioMedia = audio, Background = background };

        MediaFileRevision Publish(string path, MediaFileRevision? expected, string word)
        {
            string source = ProjectMediaPath.ToAbsolute(path, directoryPath)!;
            var revision = ContentAddressFileStore.PublishMedia(source, directoryPath, expected, limits);
            if (Path.GetFullPath(source) != Path.GetFullPath(Path.Combine(directoryPath, revision.RelativePath)))
                Add(issues, word == "音频" ? DiagnosticCode.AudioAssetCopied : DiagnosticCode.BackgroundAssetCopied, IssueSeverity.Info,
                    $"{word}「{revision.OriginalName}」已固定到工程内容地址（{revision.ByteLength}字节）；备份和另存为均独立持有资源。", word == "音频" ? "audioPath" : "background.path");
            return revision;
        }
    }
}
