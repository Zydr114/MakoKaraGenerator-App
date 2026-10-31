using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Persistence;

/// <summary>
/// schema 版本迁移的执行器（roadmap CORE-06）。
/// </summary>
/// <remarks>
/// <para>
/// 迁移是<b>逐步</b>的：每次只跨一个版本，多个步骤串成链。
/// 这样每一步都只关心"相邻两个版本之间发生了什么"，
/// 不必写"从任意旧版本直达当前版本"的组合代码（那会随版本数平方增长）。
/// </para>
/// <para>
/// 步骤缺失属于<b>编程错误</b>而不是数据问题：注册表本身不完整时应立刻抛异常，
/// 而不是把文件当成当前版本继续读——后者会让用户拿到一个悄悄缺字段的项目。
/// </para>
/// </remarks>
public static class ProjectSchemaMigrator
{
    /// <summary>内置迁移步骤（<c>project.json</c>）。</summary>
    public static IReadOnlyList<IProjectSchemaMigration> Default { get; } =
        [new V1ToV2Migration(), new V2ToV3Migration(), new V3ToV4Migration(), new V4ToV5Migration(), new V5ToV6Migration(), new V6ToV7Migration(), new V7ToV8Migration(), new V8ToV9Migration(), new OptionalColorMigration(9), new GradientSpecMigration(), new StrokeShadowSpecMigration(), new GradientOverrideMigration(12), new StyleEditorParametersMigration(13)];

    /// <summary>内置迁移步骤（<c>lyrics.json</c>）。</summary>
    /// <remarks>
    /// 歌词文件有<b>自己的</b>版本线（ADR-0029），因此不能与工程文件共用一张表：
    /// 两张表的 2 → 3 讲的是完全不同的事（工程文件是音频路径，歌词文件是样式覆盖字段）。
    /// 共用一张表会让歌词文件被工程文件的步骤“顺手”迁移一遍，日志与实际改动对不上。
    /// </remarks>
    public static IReadOnlyList<IProjectSchemaMigration> LyricsDefault { get; } =
        [new LyricsV1ToV2Migration(), new LyricsV2ToV3Migration(), new LyricsV3ToV4Migration(), new LyricsV4ToV5Migration(), new OptionalColorMigration(5), new GradientOverrideMigration(6), new StyleEditorParametersMigration(7), new LyricsV16ToV17Migration()];

    /// <summary>
    /// 把一个文档从旧版本迁移到目标版本。
    /// </summary>
    /// <param name="document">文档，<b>就地</b>修改。</param>
    /// <param name="fromVersion">文档当前版本。</param>
    /// <param name="toVersion">目标版本。</param>
    /// <param name="migrations">可用步骤；为 <see langword="null"/> 时使用 <see cref="Default"/>。</param>
    /// <returns>迁移日志；版本相同时为空日志。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">目标版本低于起始版本时抛出。</exception>
    /// <exception cref="InvalidOperationException">缺少完成整条链所需的某个步骤时抛出。</exception>
    public static MigrationLog Migrate(
        JsonObject document,
        int fromVersion,
        int toVersion,
        IReadOnlyList<IProjectSchemaMigration>? migrations = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        MigrationLog log = new();

        if (fromVersion == toVersion)
        {
            return log;
        }

        if (toVersion < fromVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toVersion),
                toVersion,
                $"目标版本 {toVersion} 低于起始版本 {fromVersion}，迁移只能向前。");
        }

        IReadOnlyList<IProjectSchemaMigration> steps = migrations ?? Default;
        int current = fromVersion;

        while (current < toVersion)
        {
            IProjectSchemaMigration? step = steps.FirstOrDefault(step => step.FromVersion == current)
                ?? throw new InvalidOperationException(
                    $"缺少从 schema v{current} 到更高版本的迁移步骤（目标 v{toVersion}）。");

            step.Apply(document, log);
            log.Add($"schema 迁移：{step.Description}（v{step.FromVersion} → v{step.ToVersion}）");
            current = step.ToVersion;
        }

        return log;
    }
}
