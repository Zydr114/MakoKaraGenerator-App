using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>独立区域的最终材质属性。</summary>
public enum PreparedMaterialSlot
{
    /// <summary>未唱填充。</summary>
    UnsungFill,
    /// <summary>已唱填充。</summary>
    SungFill,
    /// <summary>完整外描边。</summary>
    Stroke,
}
/// <summary>逐属性保存真实最后写入者，不从其他样式字段推断归属。</summary>
/// <param name="Owner">真实源对象或最后脚本写入者。</param>
/// <param name="Target">真实元素族或标记装载。</param><param name="Slot">材质属性。</param><param name="Material">完整包材质。</param>
public sealed record PreparedMaterialBinding(PreparedMaterialOwner Owner, ImageEffectTarget Target,
    PreparedMaterialSlot Slot, MaterialValue.PackageMaterial Material)
{
    /// <summary>建立真实脚本写入者的区域绑定。</summary>
    public PreparedMaterialBinding(EffectInstanceId instanceId, string artifactHash, ImageEffectTarget target,
        PreparedMaterialSlot slot, MaterialValue.PackageMaterial material) : this(new PreparedMaterialOwner.Script(instanceId, artifactHash), target, slot, material) { }
    /// <summary>脚本来源的真实实例；源材质为null。</summary>
    public EffectInstanceId? InstanceId => (Owner as PreparedMaterialOwner.Script)?.InstanceId;
    /// <summary>脚本来源的编译代次；源材质为null。</summary>
    public string? ArtifactHash => (Owner as PreparedMaterialOwner.Script)?.ArtifactHash;
}

public sealed partial class PreparedRenderFrame
{
    private readonly ImmutableDictionary<TokenFamilyTarget, StyleView> _appearances;
    private readonly ImmutableDictionary<TextMarkTarget, StyleView> _markAppearances;
    /// <summary>有效包材质及逐属性写入者；白色绘制种子不是最终外观。</summary>
    public ImmutableArray<PreparedMaterialBinding> Materials { get; } = [];
    /// <summary>匹配当前快照的源材质固定投影；没有源包时为null。</summary>
    public PreparedSourceMaterials? SourceMaterials { get; }
    /// <summary>读取真实最终中立外观，保留包句柄与完整参数。</summary>
    public StyleView GetTokenAppearance(TokenFamilyTarget target) => _appearances.TryGetValue(target, out var style) ? style
        : throw new PreparedFrameException("MissingPreparedStyle", "该目标没有有效准备外观。");
    /// <summary>读取真实标记最终中立外观。</summary>
    public StyleView GetMarkAppearance(TextMarkTarget target) => _markAppearances.TryGetValue(target, out var style) ? style
        : throw new PreparedFrameException("MissingPreparedMarkStyle", "标记没有有效准备外观。");
    /// <summary>仅供固定字形绘制／区域捕获的描述，包材质颜色必须由材质协调器消费。</summary>
    public LyricStyle GetTokenDrawingStyle(TokenFamilyTarget target) => _styles.TryGetValue(target, out var style) ? style
        : throw new PreparedFrameException("MissingPreparedStyle", "目标没有准备绘制描述。");
    /// <summary>标记固定字形绘制描述，包材质种子不能作为最终彩色捕获。</summary>
    public LyricStyle GetMarkDrawingStyle(TextMarkTarget target) => _markStyles.TryGetValue(target, out var style) ? style
        : throw new PreparedFrameException("MissingPreparedMarkStyle", "标记没有准备绘制描述。");
    private static bool ContainsPackageMaterial(StyleUpdate update) => update.UnsungMaterial is MaterialValue.PackageMaterial
        || update.SungMaterial is MaterialValue.PackageMaterial || update.Stroke?.Value?.Material is MaterialValue.PackageMaterial;
    private bool HasMaterial(ImageEffectTarget target) => Materials.Any(binding => binding.Target == target);
    private void ValidateMaterials()
    {
        if (Materials.IsDefault) throw new PreparedFrameException("InvalidPreparedMaterial", "材质归属集合不能是default。");
        Dictionary<(ImageEffectTarget, PreparedMaterialSlot), MaterialValue.PackageMaterial> expected = [];
        foreach (var pair in _appearances) Add(new ImageEffectTarget.Token(pair.Key), pair.Value);
        foreach (var pair in _markAppearances) Add(new ImageEffectTarget.Mark(pair.Key), pair.Value);
        HashSet<(ImageEffectTarget, PreparedMaterialSlot)> seen = [];
        foreach (var binding in Materials)
        {
            if (binding is null || binding.Owner is not { IsValid: true }
                || binding.Target is null || binding.Material is null || !Enum.IsDefined(binding.Slot)
                || !seen.Add((binding.Target, binding.Slot)) || !expected.TryGetValue((binding.Target, binding.Slot), out var material)
                || material != binding.Material)
                throw new PreparedFrameException("InvalidPreparedMaterial", "材质归属须逐属性唯一且与真实最终样式匹配。");
            if (binding.Owner is PreparedMaterialOwner.Source source && (source.Slot != binding.Slot
                || SourceMaterials is null || !SourceMaterials.MatchesSource(binding, HierarchyScene)))
                throw new PreparedFrameException("InvalidPreparedSourceMaterial", "源材质绑定必须属于当前固定源集合及真实继承路径。");
        }
        if (seen.Count != expected.Count) throw new PreparedFrameException("MaterialOwnershipRequired", "包材质缺少逐属性真实写入者。");
        void Add(ImageEffectTarget target, StyleView style)
        {
            if (style.UnsungMaterial is MaterialValue.PackageMaterial unsung) expected.Add((target, PreparedMaterialSlot.UnsungFill), unsung);
            if (style.SungMaterial is MaterialValue.PackageMaterial sung) expected.Add((target, PreparedMaterialSlot.SungFill), sung);
            if (style.Stroke?.Material is MaterialValue.PackageMaterial stroke) expected.Add((target, PreparedMaterialSlot.Stroke), stroke);
        }
    }
}
