using System.Collections.Concurrent;
using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>最终静态外观与逐属性持久来源；不会用材质代表色替代包。</summary>
public sealed record PresetStyleResolution
{
    /// <summary>完整有效外观。</summary>
    public LyricStyle Style { get; }
    /// <summary>属性来源的实际源路径。</summary>
    public ImmutableDictionary<LyricStyleProperty, string> Sources { get; }

    internal PresetStyleResolution(LyricStyle style, IReadOnlyDictionary<LyricStyleProperty, string> sources)
    {
        Style = style;
        Sources = sources.ToImmutableDictionary();
    }

    /// <summary>包材质的真实源引用；普通材质返回空。</summary>
    /// <param name="property">填充或描边属性：<c>unsung.fill</c>、<c>sung.fill</c>、<c>unsung.strokes</c>、<c>sung.strokes</c>。</param>
    /// <returns>引用；路径与 <see cref="SourceMaterialCatalog"/> 收集的一致。</returns>
    /// <remarks>包材质只允许用在最内层描边；已唱描边跟随未唱时引用未唱一侧的来源。</remarks>
    public SourceMaterialReference? MaterialReference(LyricStyleProperty property)
    {
        if (property == LyricStyleProperty.SungStrokes && Style.Sung.Strokes.FollowsUnsung)
            property = LyricStyleProperty.UnsungStrokes;
        var (material, state, member, region) = property switch
        {
            LyricStyleProperty.UnsungFill => (Style.Unsung.Fill.Material, false, SourceMaterialCatalog.FillMember, "fill"),
            LyricStyleProperty.SungFill => (Style.Sung.Fill.Material, true, SourceMaterialCatalog.FillMember, "fill"),
            LyricStyleProperty.UnsungStrokes => (Style.Unsung.Strokes.Innermost?.Material, false, SourceMaterialCatalog.StrokeMember, "stroke"),
            LyricStyleProperty.SungStrokes => (Style.Sung.Strokes.Value?.Innermost?.Material, true, SourceMaterialCatalog.StrokeMember, "stroke"),
            _ => throw new ArgumentOutOfRangeException(nameof(property)),
        };
        if (material is not MaterialSpec.PackageMaterial package) return null;
        string path = Sources[property];
        bool scene = path.StartsWith("lyricPresentation.", StringComparison.Ordinal);
        return new(path + SourceMaterialCatalog.Suffix(state, scene, member), region, package);
    }
}

/// <summary>只读源样式解析器；场景装载路径由实际帧传入，不猜测动态槽位。</summary>
public sealed class PresetStyleResolver
{
    private readonly LyricStyle _style;
    private readonly PresetApplications _applications;
    private readonly ImmutableDictionary<string, (LyricBranch Branch, LyricLine Line)> _lines;
    // 解析器只读不可变输入，同一参数的结果恒定：逐帧与每次锚点补算重复解析同一行／文字对象时复用，
    // 不再逐次叠加各层并分配来源表。上限只防异常的路径组合失控，超出后照常解析不再记忆。
    private const int MaximumLyricsMemo = 16_384;
    private readonly ConcurrentDictionary<LyricsKey, PresetStyleResolution> _lyricsMemo = new(LyricsKeyComparer.Instance);
    private int _lyricsMemoCount;

    /// <summary>捕获不可变源引用，不保存另一份编辑工程。</summary>
    public PresetStyleResolver(LyricStyle style, LyricsDocument lyrics, PresetApplications applications)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(lyrics);
        ArgumentNullException.ThrowIfNull(applications);
        _style = style;
        _applications = applications;
        _lines = lyrics.Branches.SelectMany(branch => branch.Lines.Select(line =>
            new KeyValuePair<string, (LyricBranch, LyricLine)>(line.Id, (branch, line)))).ToImmutableDictionary(StringComparer.Ordinal);
    }

    /// <summary>解析源或实际场景中的歌词；节点顺序为根到直属父节点。</summary>
    public PresetStyleResolution ResolveLyrics(string lineId, string? tokenId = null,
        string? clipId = null, IReadOnlyList<string>? nodePath = null)
    {
        if (!_lines.TryGetValue(lineId, out var source)) throw new ArgumentException("歌词行已失效。", nameof(lineId));
        // 场景路径通常就是不可变数组（拆箱不复制）；其他列表复制一次，记忆键不引用可变输入。
        var path = nodePath switch { null => default, ImmutableArray<string> array => array, _ => [.. nodePath] };
        var key = new LyricsKey(lineId, tokenId, clipId, path);
        if (_lyricsMemo.TryGetValue(key, out var cached)) return cached;
        var resolved = ResolveLyrics(source, lineId, tokenId, clipId, nodePath);
        if (Volatile.Read(ref _lyricsMemoCount) < MaximumLyricsMemo && _lyricsMemo.TryAdd(key, resolved))
            Interlocked.Increment(ref _lyricsMemoCount);
        return resolved;
    }

    private PresetStyleResolution ResolveLyrics((LyricBranch Branch, LyricLine Line) source, string lineId, string? tokenId,
        string? clipId, IReadOnlyList<string>? nodePath)
    {
        var layers = new Layers(_style, "lyricStyle");
        Apply(layers, new(ProjectEffectBindingKind.Project));
        string branchPath = "lyrics.branches." + source.Branch.Id;
        layers.Overlay(source.Branch.StyleOverride, branchPath + ".styleOverride");
        Apply(layers, new(ProjectEffectBindingKind.Branch, id: source.Branch.Id));
        ApplyNodes(layers, nodePath);
        if (clipId is not null) Apply(layers, new(ProjectEffectBindingKind.Clips, ids: [clipId]));
        string linePath = branchPath + ".lines." + source.Line.Id;
        layers.Overlay(source.Line.StyleOverride, linePath + ".styleOverride");
        Apply(layers, new(ProjectEffectBindingKind.Lines, ids: [lineId]));
        if (tokenId is not null)
        {
            var token = source.Line.Tokens.SingleOrDefault(token => token.Id == tokenId)
                ?? throw new ArgumentException("文字对象不属于所选行。", nameof(tokenId));
            layers.Overlay(token.StyleOverride, linePath + ".tokens." + token.Id + ".styleOverride");
            Apply(layers, new(ProjectEffectBindingKind.Tokens, ids: [tokenId]));
        }
        return layers.Complete();
    }

    /// <summary>标记保持提示组／自身源外观，仅继承显式绑定在提示组或标记节点的模板，不继承工程／分支歌词预设。</summary>
    public PresetStyleResolution ResolveMark(LyricStyle appearance, string appearancePath,
        LyricStyleOverride? ownOverride, string ownPath, IReadOnlyList<string> nodePath, string? branchId = null)
    {
        _ = branchId;
        var layers = new Layers(appearance, appearancePath);
        if (nodePath is { Count: >= 2 })
        {
            // Marks inherit only their direct parent container preset (when IncludeDescendants is enabled),
            // ignoring outer ancestors such as root/default containers even if a full container path is passed.
            var parentTarget = new ProjectEffectBinding(ProjectEffectBindingKind.Node, id: nodePath[^2]);
            if (_applications.Find(parentTarget) is { Enabled: true } application && application.Target.IncludeDescendants)
                Apply(layers, parentTarget);
        }
        layers.Overlay(ownOverride, ownPath);
        if (nodePath is { Count: > 0 })
            Apply(layers, new(ProjectEffectBindingKind.Node, id: nodePath[^1]));
        return layers.Complete();
    }

    private void Apply(Layers layers, ProjectEffectBinding target)
    {
        if (_applications.Find(target) is not { Enabled: true } application) return;
        layers.Overlay(StyleBatchEditor.CopyProperties(LyricStyleOverride.Empty,
            application.Snapshot.Style, application.Snapshot.Properties), "presetApplications." + application.Id + ".snapshot.style");
    }

    private void ApplyNodes(Layers layers, IReadOnlyList<string>? path)
    {
        if (path is null) return;
        for (int index = 0; index < path.Count; index++)
        {
            var target = new ProjectEffectBinding(ProjectEffectBindingKind.Node, id: path[index]);
            if (_applications.Find(target) is { } application &&
                (index == path.Count - 1 || application.Target.IncludeDescendants)) Apply(layers, target);
        }
    }

    // 路径为 default 表示未给出场景路径，与空路径区分。
    private readonly record struct LyricsKey(string LineId, string? TokenId, string? ClipId, ImmutableArray<string> Path);

    private sealed class LyricsKeyComparer : IEqualityComparer<LyricsKey>
    {
        public static readonly LyricsKeyComparer Instance = new();

        public bool Equals(LyricsKey x, LyricsKey y)
            => string.Equals(x.LineId, y.LineId, StringComparison.Ordinal) && string.Equals(x.TokenId, y.TokenId, StringComparison.Ordinal)
                && string.Equals(x.ClipId, y.ClipId, StringComparison.Ordinal)
                && (x.Path.IsDefault ? y.Path.IsDefault : !y.Path.IsDefault && x.Path.AsSpan().SequenceEqual(y.Path.AsSpan()));

        public int GetHashCode(LyricsKey key)
        {
            HashCode hash = new();
            hash.Add(key.LineId, StringComparer.Ordinal);
            hash.Add(key.TokenId ?? string.Empty, StringComparer.Ordinal);
            hash.Add(key.ClipId ?? string.Empty, StringComparer.Ordinal);
            hash.Add(key.Path.IsDefault ? -1 : key.Path.Length);
            if (!key.Path.IsDefault)
                foreach (string id in key.Path) hash.Add(id, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }

    private sealed class Layers
    {
        private LyricStyle _style;
        private readonly Dictionary<LyricStyleProperty, string> _sources;

        public Layers(LyricStyle style, string path)
        {
            _style = style;
            _sources = LyricStylePropertyExtensions.All.ToDictionary(property => property, _ => path);
        }

        public void Overlay(LyricStyleOverride? overlay, string path)
        {
            if (overlay is null || overlay.IsEmpty) return;
            var resolved = LyricStyleResolver.Resolve(_style, lineOverride: overlay);
            foreach (var property in LyricStylePropertyExtensions.All)
                if (resolved.SourceOf(property) == LyricStyleSource.Line) _sources[property] = path;
            _style = resolved.Style;
        }

        public PresetStyleResolution Complete() => new(_style, _sources);
    }
}
