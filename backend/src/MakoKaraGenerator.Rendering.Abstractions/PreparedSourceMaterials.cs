using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>会话前固定的源材质参数与真实继承路径；不持有脚本对象或native资源。</summary>
public sealed class PreparedSourceMaterials
{
    private readonly ProjectSnapshot _snapshot;
    private readonly PresetStyleResolver _resolver;
    private readonly ImmutableDictionary<MaterialSpec.PackageMaterial, MaterialValue.PackageMaterial> _values;
    private readonly ImmutableHashSet<SourceMaterialReference> _references;

    /// <summary>固定全部存储引用及真实参数投影，不丢掉不可见或被覆盖对象。</summary>
    public PreparedSourceMaterials(ProjectSnapshot snapshot, IEnumerable<KeyValuePair<SourceMaterialReference, MaterialValue.PackageMaterial>> values)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(values);
        _snapshot = snapshot;
        _resolver = PresetSceneStyleProjection.Resolver(snapshot);
        Fingerprint = snapshot.EffectInputsFingerprint ?? throw new PreparedFrameException("SourceMaterialFingerprintRequired", "源材质集合必须固定完整输入指纹。");
        var entries = values.ToImmutableDictionary();
        var references = SourceMaterialCatalog.Collect(snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation, snapshot.PresetApplications);
        if (entries.Count != references.Length || references.Any(reference => !entries.ContainsKey(reference)))
            throw new PreparedFrameException("SourceMaterialClosureMismatch", "源材质投影须覆盖全部真实源引用。");
        _references = entries.Keys.ToImmutableHashSet();
        var resolved = ImmutableDictionary.CreateBuilder<MaterialSpec.PackageMaterial, MaterialValue.PackageMaterial>();
        foreach (var pair in entries)
        {
            var identity = pair.Value.Handle.Identity;
            var source = pair.Key.Material;
            if (identity.PackageId != source.PackageId || identity.Revision != source.Revision || identity.ExportId != source.ExportId)
                throw new PreparedFrameException("SourceMaterialIdentityMismatch", "源材质投影不能更换固定包身份。");
            if (resolved.TryGetValue(source, out var previous) && previous != pair.Value)
                throw new PreparedFrameException("SourceMaterialParametersMismatch", "同一固定源材质须具有相同完整参数。");
            resolved[source] = pair.Value;
        }
        _values = resolved.ToImmutable();
    }

    /// <summary>包含源材质与精确参数的固定工程身份。</summary>
    public string Fingerprint { get; }
    /// <summary>拒绝把旧集合用于另一份工程输入。</summary>
    public void ValidateSnapshot(ProjectSnapshot snapshot)
    {
        if (snapshot.EffectInputsFingerprint != Fingerprint)
            throw new PreparedFrameException("SourceMaterialInputsMismatch", "源材质固定集合与快照身份不一致。");
    }

    /// <summary>媒体绑定入口核对捕获的引用全集和样式解析源；不重算或更换旧源指纹。</summary>
    public void ValidateSourceClosure(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        var references = SourceMaterialCatalog.Collect(snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation, snapshot.PresetApplications);
        // Resolve(Material) alone cannot prove path/region membership or absence
        // of removed references. Bindings also reads the captured immutable tree
        // and resolver, so compare those complete value inputs before reuse.
        if (!_references.SetEquals(references) || _snapshot.LyricStyle != snapshot.LyricStyle
            || _snapshot.Lyrics != snapshot.Lyrics || _snapshot.LyricPresentation != snapshot.LyricPresentation
            || !_snapshot.PresetApplications.Equals(snapshot.PresetApplications))
            throw new PreparedFrameException("SourceMaterialClosureMismatch", "源材质引用全集或捕获的样式解析源与当前快照不一致。");
    }
    /// <summary>读取会话前解析的完整参数，帧内不解析包JSON。</summary>
    public MaterialValue.PackageMaterial Resolve(MaterialSpec.PackageMaterial material) => _values.TryGetValue(material, out var value) ? value
        : throw new PreparedFrameException("SourceMaterialResourcesRequired", "没有当前源材质的固定参数与资源投影。");

    /// <summary>按当前真实装载及父树生成区域绑定；来源保留实际快照路径。</summary>
    public ImmutableArray<PreparedMaterialBinding> Bindings(SceneFrame scene)
    {
        var bindings = ImmutableArray.CreateBuilder<PreparedMaterialBinding>();
        var loaded = scene.Lines.ToDictionary(static line => line.BindingId!, StringComparer.Ordinal);
        foreach (var geometry in scene.TokenGeometries)
            Add(new ImageEffectTarget.Token(geometry.WithRuby),
                PresetSceneStyleProjection.LyricsForBinding(_snapshot, loaded[geometry.WithRuby.BindingId.Value], geometry.WithRuby.TokenId.Value, scene, _resolver));
        foreach (var geometry in scene.MarkGeometries)
            Add(new ImageEffectTarget.Mark(geometry.Target),
                PresetSceneStyleProjection.Mark(_snapshot, geometry.Target.NodeId.Value, scene, _resolver));
        return bindings.ToImmutable();

        void Add(ImageEffectTarget target, PresetStyleResolution resolution)
        {
            foreach (var slot in Enum.GetValues<PreparedMaterialSlot>())
                if (Reference(resolution, slot) is { } reference)
                    bindings.Add(new(new PreparedMaterialOwner.Source(reference, slot), target, slot, Resolve(reference.Material)));
        }
    }

    /// <summary>准备帧源绑定须匹配当前真实装载及源路径，不能含脚本uniform覆盖。</summary>
    public bool MatchesSource(PreparedMaterialBinding binding, SceneFrame scene)
    {
        if (binding.Owner is not PreparedMaterialOwner.Source source) return false;
        PresetStyleResolution? resolution = binding.Target switch
        {
            ImageEffectTarget.Token token when scene.TokenGeometries.FirstOrDefault(item => item.WithRuby == token.Target) is { } geometry
                => PresetSceneStyleProjection.LyricsForBinding(_snapshot, scene.Lines.Single(line => line.BindingId == token.Target.BindingId.Value), token.Target.TokenId.Value, scene, _resolver),
            ImageEffectTarget.Mark mark when scene.MarkGeometries.Any(item => item.Target == mark.Target)
                => PresetSceneStyleProjection.Mark(_snapshot, mark.Target.NodeId.Value, scene, _resolver),
            _ => null,
        };
        var reference = resolution is null ? null : Reference(resolution, binding.Slot);
        return reference == source.Reference && reference is not null && Resolve(reference.Material) == binding.Material;
    }

    private SourceMaterialReference? Reference(PresetStyleResolution resolution, PreparedMaterialSlot slot)
    {
        var property = slot switch
        {
            PreparedMaterialSlot.UnsungFill => LyricStyleProperty.UnsungColor,
            PreparedMaterialSlot.SungFill => LyricStyleProperty.SungColor,
            PreparedMaterialSlot.Stroke => LyricStyleProperty.StrokeColor,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };
        var reference = resolution.MaterialReference(property);
        if (reference is not null && !_references.Contains(reference))
            throw new PreparedFrameException("SourceMaterialOriginInvalid", "真实材质继承路径不属于源闭包。");
        return reference;
    }
}
