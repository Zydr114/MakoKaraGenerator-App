using System.Text.Json;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;
using static MakoKaraGenerator.Core.Diagnostics.DiagnosticList;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// 项目目录的读写（roadmap CORE-04）。
/// </summary>
/// <remarks>
/// <para>
/// 首期使用可读的目录形式（<c>song.mkgproj/</c>）而不是私有二进制格式，
/// 结构见 docs/development.md §6.1：先发布媒体、分析、效果包和内容地址歌词，
/// 再原子替换唯一入口；中途失败不混批、不覆写历史资源。
/// </para>
/// <para>
/// 读取<b>从不修改</b>磁盘上的文件：schema 版本不支持时只报告，不尝试就地转换，
/// 以免在用户还没有备份的时候破坏原文件（docs/development.md §6.4）。
/// </para>
/// <para>
/// 本类不做"是否允许保存"的判断。按 docs/development.md §6.3，
/// 打开项目、导入歌词与导出运行完整校验；保存收集展示诊断，但保留非法草稿原值。
/// </para>
/// </remarks>
public static class ProjectStore
{
    /// <summary>把项目写入指定目录。目录不存在时会被创建。</summary>
    /// <param name="project">待保存的项目。</param>
    /// <param name="directoryPath">项目包目录。</param>
    /// <param name="issues">诊断收集器；资源收拢的结果会记在这里。</param>
    /// <param name="limits">显式持久IO限额，默认完整媒体16GiB／分析256MiB。</param>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="ArgumentException"><paramref name="directoryPath"/> 为空白时抛出。</exception>
    public static void Save(
        Project project,
        string directoryPath,
        List<ProjectIssue>? issues = null,
        ProjectStoreLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        limits ??= new();
        if (!limits.IsValid) throw new ArgumentException("持久IO限额必须为正。", nameof(limits));

        // 格式保护先于创建目录或发布任何资源；禁止把 v2 数据写入旧 schema。
        if (project.SchemaVersion != Project.CurrentSchemaVersion)
            throw new EffectPackageException("ProjectSchemaVersionUnsupported", "project.json.schemaVersion", "只允许保存当前工程格式；旧工程未更改。");
        foreach (var package in project.Effects.Packages) EffectPackageProfiles.RequireSupported(package.Profile);

        SourceMaterialCatalog.RequireResources(project);
        PresetApplicationsJson.RequireResources(project);
        issues?.AddRange(PresentationValidator.Validate(project).Issues);
        Directory.CreateDirectory(directoryPath);

        // 资源收拢只影响写盘用的路径：内存里的工程仍然指向用户选的那个文件，
        // 免得每次保存都要改动编辑器持有的实例。
        Project stored = ProjectAssetCollector.Collect(project, directoryPath, issues ?? [], limits);

        ProjectFileDto dto = ProjectDtoMapper.ToDto(stored);
        using var effectsJson = JsonDocument.Parse(ProjectEffectsJson.Encode(stored.Effects));
        dto.Effects = effectsJson.RootElement.Clone();
        using var presetsJson = JsonDocument.Parse(PresetApplicationsJson.Encode(stored.PresetApplications));
        dto.PresetApplications = presetsJson.RootElement.Clone();
        dto.Media = ProjectResourceJson.Media(stored);
        dto.AudioAnalysis = ProjectResourceJson.PublishAnalysis(stored, directoryPath, limits);

        // 完整收拢成功后只保存受管内容地址，工程目录可以独立搬走。
        dto.AudioPath = ProjectMediaPath.ToStored(stored.AudioPath, directoryPath);

        // 背景图片／视频也已由收拢器流式发布到 data/media/。
        if (dto.Background is { } background)
        {
            background.Path = ProjectMediaPath.ToStored(stored.Background.Path, directoryPath);
        }

        // 先发布不可变歌词，再发布唯一入口。中途失败只留下未引用修订。
        foreach (var package in stored.Effects.Packages)
        {
            EffectPackageStorage.Publish(Path.Combine(directoryPath, ProjectEffectsJson.PackagePath(package.Revision)), package);
        }
        // 缺失修订不能发布为一个后来无法重新打开的工程；非法参数草稿仍允许保存。
        foreach (var instance in stored.Effects.Instances) _ = stored.Effects.GetPackage(instance.PackageId, instance.Revision);
        dto.LyricsFile = LyricRevisionStore.Publish(directoryPath, ProjectDtoMapper.ToDto(project.Lyrics));
        WriteAtomically(Path.Combine(directoryPath, ProjectJson.ProjectFileName), dto);
    }

    /// <summary>从指定目录读取项目，并立即按"打开项目"上下文做一次校验。</summary>
    /// <param name="directoryPath">项目包目录。</param>
    /// <param name="limits">显式持久IO限额；超限拒绝打开，不裁切。</param>
    /// <returns>读取结果；结构层面失败时 <see cref="ProjectLoadResult.Project"/> 为 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="directoryPath"/> 为空白时抛出。</exception>
    public static ProjectLoadResult Load(string directoryPath, ProjectStoreLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        limits ??= new();
        if (!limits.IsValid) throw new ArgumentException("持久IO限额必须为正。", nameof(limits));

        List<ProjectIssue> issues = [];

        string projectPath = Path.Combine(directoryPath, ProjectJson.ProjectFileName);

        if (!File.Exists(projectPath))
        {
            Add(
                issues,
                DiagnosticCode.MissingProjectFile,
                IssueSeverity.Error,
                $"项目目录中找不到 {ProjectJson.ProjectFileName}。",
                projectPath);

            return Failure(issues);
        }

        // 原子入口只读一次：门禁、schema 和歌词引用来自同一批字节。
        byte[]? projectBytes = ProjectJsonReader.ReadBytes(projectPath, issues);
        if (projectBytes is null || !CheckApplicationVersion(projectBytes, issues))
        {
            return Failure(issues);
        }

        ProjectFileDto? projectDto = ProjectJsonReader.Deserialize<ProjectFileDto>(
            projectBytes, projectPath,
            Project.CurrentSchemaVersion,
            issues);

        if (projectDto is null)
        {
            return Failure(issues);
        }

        ProjectEffects? effects = LoadEffects(directoryPath, projectDto.Effects, issues);
        if (effects is null) return Failure(issues);
        PresetApplications applications;
        try
        {
            if (projectDto.PresetApplications is not { } presetValue) throw new IOException("工程缺少必填的presetApplications入口。");
            applications = PresetApplicationsJson.Read(presetValue, effects);
        }
        catch (Exception error) when (ResourceError(error))
        {
            Add(issues, DiagnosticCode.InvalidEffectData, IssueSeverity.Error, $"工程预设应用读取失败：{error.Message}", "project.json.presetApplications");
            return Failure(issues);
        }

        MediaFileRevision? audioMedia, backgroundMedia;
        try
        {
            (audioMedia, backgroundMedia) = ProjectResourceJson.ReadMedia(projectDto.Media, projectDto.AudioPath, projectDto.Background?.Path, directoryPath, limits);
            if (projectDto.Background?.Kind == "color" && backgroundMedia is not null)
                throw new IOException("纯色背景不能携带媒体修订。");
        }
        catch (Exception error) when (ResourceError(error))
        {
            Add(issues, DiagnosticCode.InvalidMediaResource, IssueSeverity.Error, $"媒体闭包无效：{error.Message}", "project.json.media"); return Failure(issues);
        }
        ProjectAudioAnalysis? analysis;
        try { analysis = ProjectResourceJson.ReadAnalysis(projectDto.AudioAnalysis, audioMedia, directoryPath, limits); }
        catch (Exception error) when (ResourceError(error))
        {
            Add(issues, DiagnosticCode.InvalidAudioAnalysis, IssueSeverity.Error, $"音频分析无效：{error.Message}", "project.json.audioAnalysis"); return Failure(issues);
        }

        Composition composition = ProjectDtoMapper.MapComposition(projectDto.Composition, issues);
        LyricsDocument? lyrics = LoadLyrics(directoryPath, projectDto.LyricsFile, issues);
        if (lyrics is null)
        {
            return Failure(issues);
        }

        Project project;
        try
        {
            project = new()
            {
                SchemaVersion = Project.CurrentSchemaVersion,
                Name = ProjectDtoMapper.MapProjectName(projectDto.Name, issues),
                Composition = composition,
                Lyrics = lyrics,
                Effects = effects,
                PresetApplications = applications,
                LyricStyle = ProjectDtoMapper.MapLyricStyle(projectDto.LyricStyle, issues),
                LyricStylePresetId = projectDto.LyricStylePresetId,
                AudioPath = ProjectMediaPath.ToAbsolute(
                    MapAudioPath(projectDto.AudioPath, issues),
                    directoryPath),
                AudioMedia = audioMedia,
                AudioAnalysis = analysis,
                Background = MapBackground(projectDto.Background, directoryPath, issues) with { Media = backgroundMedia },
                EnterAnimation = ProjectDtoMapper.MapLineAnimation(projectDto.EnterAnimation, "enterAnimation", issues),
                LyricPresentation = ProjectDtoMapper.MapPresentation(projectDto.LyricPresentation, issues),
            };

        }
        catch (Exception error) when (ResourceError(error))
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, $"样式读取失败：{error.Message}",
                error is EffectPackageException package ? package.Path : "lyricStyle");
            return Failure(issues);
        }

        try { SourceMaterialCatalog.RequireResources(project); }
        catch (EffectPackageException error)
        {
            Add(issues, DiagnosticCode.InvalidEffectData, IssueSeverity.Error, error.Message, error.Path); return Failure(issues);
        }

        // 结构诊断在前、语义诊断在后：先让用户看到"文件哪里不对"，再看"数据哪里不对"。
        ValidationReport report = ValidationReport
            .From(issues)
            .Merge(ProjectValidator.Validate(project, ValidationContext.Open));

        return new ProjectLoadResult { Project = project, Report = report };
    }

    private static bool ResourceError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or EffectPackageException or OverflowException;

    private static ProjectEffects? LoadEffects(string directory, JsonElement? value, List<ProjectIssue> issues)
    {
        try
        {
            if (value is null) throw new EffectPackageException("EffectDataMissing", "project.json.effects", "工程缺少 effects 源数据，已取消打开。");
            return ProjectEffectsJson.Read(value.Value, path => EffectPackageStorage.ReadSupportedDirectoryAsync(
                Path.Combine(directory, path)).GetAwaiter().GetResult());
        }
        catch (Exception error) when (error is EffectPackageException or IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            Add(issues, DiagnosticCode.InvalidEffectData, IssueSeverity.Error,
                $"工程效果数据读取失败：{error.Message}", error is EffectPackageException package ? package.Path : "project.json.effects");
            return null;
        }
    }

    private static bool CheckApplicationVersion(byte[] projectBytes, List<ProjectIssue> issues)
    {
        const string location = "project.json.applicationVersion";
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                ProjectJsonReader.JsonBytes(projectBytes), ProjectJsonReader.DocumentOptions());

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Add(issues, DiagnosticCode.MalformedJson, IssueSeverity.Error,
                    "project.json 的顶层不是一个 JSON 对象。", ProjectJson.ProjectFileName);
                return false;
            }

            string? stored = document.RootElement.TryGetProperty("applicationVersion", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            bool valid = Version.TryParse(stored, out Version? parsed)
                && parsed.Build >= 0
                && parsed.ToString(3) == stored;

            // This export-only patch keeps the 0.114.0 project format unchanged.
            // Admit that one predecessor without rewriting it; schema gates below
            // still reject unsupported formats. Other release pairs stay strict.
            bool compatibleExportPatch = ProductVersion.Current == "0.114.1" && stored == "0.114.0";
            if (!valid || stored != ProductVersion.Current && !compatibleExportPatch)
            {
                Add(issues, DiagnosticCode.ApplicationVersionMismatch, IssueSeverity.Error,
                    $"工程版本：{stored ?? "未标注程序版本"}；当前程序版本：{ProductVersion.Current}。"
                    + "版本不一致，已取消打开，原文件未更改。", location);
                return false;
            }

            return true;
        }
        catch (JsonException exception)
        {
            Add(issues, DiagnosticCode.MalformedJson, IssueSeverity.Error,
                $"project.json 不是合法的 JSON：{exception.Message}", ProjectJson.ProjectFileName);
            return false;
        }
    }

    /// <summary>读出背景源，并把图片路径解析成绝对路径。</summary>
    private static BackgroundSource MapBackground(
        BackgroundDto? dto,
        string directoryPath,
        List<ProjectIssue> issues)
    {
        BackgroundSource background = ProjectDtoMapper.MapBackground(dto, issues);

        return background.Path is null
            ? background
            : background with { Path = ProjectMediaPath.ToAbsolute(background.Path, directoryPath) };
    }

    /// <summary>读出音频路径；空白值按"没有音频"处理并记一条警告。</summary>
    private static string? MapAudioPath(string? path, List<ProjectIssue> issues)
    {
        if (path is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            Add(
                issues,
                DiagnosticCode.InvalidMediaPath,
                IssueSeverity.Warning,
                "音频路径是空白文本，已按「尚未导入音频」处理。",
                "audioPath");

            return null;
        }

        return path;
    }

    private static LyricsDocument? LoadLyrics(string directoryPath, string? reference, List<ProjectIssue> issues)
    {
        byte[]? bytes = LyricRevisionStore.Read(directoryPath, reference, issues);
        if (bytes is null) { return null; }
        LyricsFileDto? dto = ProjectJsonReader.Deserialize<LyricsFileDto>(
            bytes, reference!, LyricsDocument.CurrentSchemaVersion, issues, ProjectSchemaMigrator.LyricsDefault);
        try { return dto is null ? null : ProjectDtoMapper.MapLyrics(dto, LyricsDocument.CurrentSchemaVersion, issues); }
        catch (Exception error) when (ResourceError(error))
        {
            Add(issues, DiagnosticCode.InvalidStyleValue, IssueSeverity.Error, $"歌词样式读取失败：{error.Message}",
                error is EffectPackageException package ? package.Path : reference);
            return null;
        }
    }

    internal static void WriteAtomically<T>(string path, T value)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ProjectJson.TemporaryFileSuffix;

        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, ProjectJson.Options);
                stream.Flush(flushToDisk: true);
            }

            MoveOverwrite(temporaryPath, path);
        }
        finally
        {
            // 写入或改名失败时不要留下临时文件，避免下次打开看到可疑的残留。
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static void WriteBytesAtomically(string path, ReadOnlySpan<byte> bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ProjectJson.TemporaryFileSuffix;
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            MoveOverwrite(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void MoveOverwrite(string temporaryPath, string path)
    {
        if (Directory.Exists(path))
            throw new IOException($"目标路径是目录，无法覆盖为文件：{path}");

        if (!OperatingSystem.IsWindows())
        {
            File.Move(temporaryPath, path, overwrite: true);
            return;
        }

        const int maxAttempts = 25;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, path, overwrite: true);
                return;
            }
            catch (Exception error) when (attempt < maxAttempts
                && error is IOException or UnauthorizedAccessException
                && !Directory.Exists(path)
                && (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0))
            {
                Thread.Sleep(10);
            }
        }
    }

    private static ProjectLoadResult Failure(List<ProjectIssue> issues) =>
        new() { Project = null, Report = ValidationReport.From(issues) };
}
