namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>snapshot-v1 的固定有限来源能力；传输合同不依赖领域或渲染程序集。</summary>
public static class BackendSnapshotProfiles
{
    public const string StaticLyricsScene = "static-lyrics-scene-v1";
    public const string StaticLyricsSceneAnalysis = "static-lyrics-scene-analysis-v1";
    /// <summary>完整纯静态固定预设，允许可选完整分析，不执行包或配方。</summary>
    public const string StaticLyricsScenePresets = "static-lyrics-scene-presets-v1";
    /// <summary>直接效果包的有限正式任务来源；只在各任务明确公布时可用。</summary>
    public const string PackageLyricsScene = "package-lyrics-scene-v1";
    /// <summary>直接效果包与固定预设的有限混合来源；仅供明确公布的分析/测量接缝。</summary>
    public const string PackageLyricsScenePresets = "package-lyrics-scene-presets-v1";
    /// <summary>领域五基础源与独立固定媒体授权共用的私有运行输入。</summary>
    public const string RuntimeProjectInput = "runtime-project-input-v1";
    public static bool IsSupported(string? profile) => profile is StaticLyricsScene or StaticLyricsSceneAnalysis or StaticLyricsScenePresets;
    public static bool IsExportSupported(string? profile) => IsSupported(profile) || profile is PackageLyricsScene or PackageLyricsScenePresets or RuntimeProjectInput;
}
