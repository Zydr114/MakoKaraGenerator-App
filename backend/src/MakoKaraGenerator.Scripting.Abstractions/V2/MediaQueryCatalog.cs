using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.V2;

/// <summary>精确 owner、导出、完整参数和固定输入的不可变查询目录；没有采样服务。</summary>
public sealed class MediaQueryCatalog
{
    private readonly object _scope = new();
    private MediaQueryCatalog() { Profiles = []; }
    internal static MediaQueryCatalog Empty { get; } = new();

    internal MediaQueryCatalog(MediaFeatureInputInfo input,
        ImmutableArray<(string Id, MediaCoordinateSpace Space, long WindowUs)> declarations)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (declarations.IsDefault) throw new ArgumentException("媒体目录需要完整声明集合。", nameof(declarations));
        var profiles = ImmutableArray.CreateBuilder<MediaQueryProfile>(declarations.Length);
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            string id = declaration.Id;
            if (!ValidName(id) || !names.Add(id))
                throw new ArgumentException("媒体查询名须为唯一规范文本。", nameof(declarations));
            profiles.Add(new(new(id, _scope), declaration.Space, declaration.WindowUs));
        }
        Input = input; Profiles = profiles.MoveToImmutable();
    }

    /// <summary>固定输入；仅旧的无媒体能力入口为空，不表示 NoMedia。</summary>
    public MediaFeatureInputInfo? Input { get; }
    /// <summary>当前导出实际引用的固定查询；不暴露包的其他导出。</summary>
    public ImmutableArray<MediaQueryProfile> Profiles { get; }
    /// <summary>获取已声明查询身份；不能动态增加查询、窗口或权限。</summary>
    public MediaQueryId GetId(string declaredId)
    {
        foreach (var profile in Profiles)
            if (string.Equals(profile.Id.Value, declaredId, StringComparison.Ordinal)) return profile.Id;
        throw new EffectOutputException("UndeclaredMediaQuery", "查询名不属于当前固定导出目录。");
    }

    internal MediaQueryProfile Resolve(MediaQueryId id)
    {
        if (id.BelongsTo(_scope))
            foreach (var profile in Profiles)
                if (string.Equals(profile.Id.Value, id.Value, StringComparison.Ordinal)) return profile;
        throw new EffectOutputException("UndeclaredMediaQuery", "查询身份不属于当前固定导出目录。");
    }

    private static bool ValidName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        for (int index = 0; index < value.Length; index++)
        {
            char item = value[index];
            if (char.IsControl(item) || item == '\uFEFF') return false;
            if (char.IsHighSurrogate(item))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false;
            }
            else if (char.IsLowSurrogate(item)) return false;
        }
        return true;
    }
}
