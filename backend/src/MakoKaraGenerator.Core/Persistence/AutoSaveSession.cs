using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// 上一次自动保存留下的"现场标记"。
/// </summary>
/// <param name="BackupDirectoryPath">那份备份的目录。</param>
/// <param name="ProjectDirectoryPath">当时工程所在的目录；还没另存为时为 <see langword="null"/>。</param>
/// <param name="ProjectName">工程名（用于向用户说明要恢复的是哪个）。</param>
/// <param name="SavedAtUtc">备份时间。</param>
/// <remarks>
/// <para>
/// 为什么需要这个标记：备份可能落在工程目录里，而应用启动时<b>并不知道上次开的是哪个工程</b>
/// （启动装载的是内置演示工程）。没有标记，"崩溃后重开能恢复到最后一次自动保存"这条验收
/// 对已保存的工程就落不了地——没人知道该去哪个目录找备份。
/// </para>
/// <para>
/// 标记写的是<b>备份目录的绝对路径</b>而不是"工程目录 + 时间戳"：读的时候不需要再推导一遍
/// 命名规则，也就不会因为命名规则改动而与旧备份失联。
/// </para>
/// </remarks>
public sealed record AutoSaveSession(
    string BackupDirectoryPath,
    string? ProjectDirectoryPath,
    string ProjectName,
    DateTimeOffset SavedAtUtc)
{
    /// <summary>标记文件名。</summary>
    public const string FileName = "last-session.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>标记文件的完整路径。</summary>
    /// <param name="stateRoot">应用状态目录。</param>
    /// <returns>路径。</returns>
    /// <exception cref="ArgumentException"><paramref name="stateRoot"/> 为空白时抛出。</exception>
    public static string PathFor(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);

        return Path.Combine(stateRoot, FileName);
    }

    /// <summary>写下标记（覆盖上一份）。</summary>
    /// <param name="stateRoot">应用状态目录。</param>
    /// <param name="session">现场信息。</param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> 为 <see langword="null"/> 时抛出。</exception>
    public static void Write(string stateRoot, AutoSaveSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        string path = PathFor(stateRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string temporaryPath = path + ProjectJson.TemporaryFileSuffix;

        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(session, Options));
        File.Move(temporaryPath, path, overwrite: true);
    }

    /// <summary>读标记。</summary>
    /// <param name="stateRoot">应用状态目录。</param>
    /// <returns>现场信息；没有标记或标记损坏时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 坏标记一律当作"没有"：这个文件存在的唯一目的是<b>帮</b>用户找回工作，
    /// 它自己出问题绝不该拦住启动（与"工程本身读不出来就明确报错"是两回事）。
    /// </remarks>
    public static AutoSaveSession? Read(string stateRoot)
    {
        string path = PathFor(stateRoot);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AutoSaveSession>(File.ReadAllText(path), Options);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>删掉标记（用户已经处理过这份现场）。</summary>
    /// <param name="stateRoot">应用状态目录。</param>
    public static void Clear(string stateRoot)
    {
        string path = PathFor(stateRoot);

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
