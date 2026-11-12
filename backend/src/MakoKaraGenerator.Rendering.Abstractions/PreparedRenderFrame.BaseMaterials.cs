using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering;

/// <summary>真实基础字材质部位；与可绑定 ImageEffectTarget 分离。</summary>
/// <param name="Owner">真实材质写入者或已验证父字族源来源。</param>
/// <param name="Target">当前真实基础字装载。</param><param name="Slot">材质部位。</param><param name="Material">完整最终包材质。</param>
public sealed record PreparedBaseMaterialBinding(PreparedMaterialOwner Owner, BaseTokenTarget Target,
    PreparedMaterialSlot Slot, MaterialValue.PackageMaterial Material);

public sealed partial class PreparedRenderFrame
{
    /// <summary>真实 Base 中立外观；仅含有 Base 外观输出的目标。</summary>
    public ImmutableDictionary<BaseTokenTarget, StyleView> BaseAppearances { get; private init; } = ImmutableDictionary<BaseTokenTarget, StyleView>.Empty;
    /// <summary>最终 Base 包材质的逐部位真实来源；Solid/Gradient/null 明确遮蔽父材质。</summary>
    public ImmutableArray<PreparedBaseMaterialBinding> BaseMaterials { get; private init; } = [];
    /// <summary>读取最终中立 Base 外观，不读取白色几何种子。</summary>
    public StyleView GetBaseAppearance(BaseTokenTarget target) => BaseAppearances.TryGetValue(target, out var value) ? value
        : throw PartitionError("MissingPreparedBaseStyle", "基础字没有独立外观输出。", new TextTarget.Base(target));

    private ImmutableArray<PreparedBaseMaterialBinding> ResolveBaseMaterials(ImmutableArray<BaseTokenStyle> styles,
        ImmutableDictionary<BaseTokenTarget, StyleView> appearances, ImmutableArray<PreparedBaseMaterialBinding> supplied)
    {
        Dictionary<(BaseTokenTarget, PreparedMaterialSlot), PreparedBaseMaterialBinding> incoming = [];
        foreach (var binding in supplied)
            if (binding is null || binding.Target is null || binding.Material is null || binding.Owner is not { IsValid: true }
                || !Enum.IsDefined(binding.Slot) || !incoming.TryAdd((binding.Target, binding.Slot), binding))
                throw PartitionError("InvalidPreparedBaseMaterial", "Base 材质来源须逐部位唯一且完整。", binding?.Target is { } target ? new TextTarget.Base(target) : null);
        var result = ImmutableArray.CreateBuilder<PreparedBaseMaterialBinding>();
        foreach (var output in styles)
        {
            var view = appearances[output.Target];
            foreach (var slot in Enum.GetValues<MaterialSlot>()) Add(PreparedMaterialSlots.From(slot), view.Material(slot));
            void Add(PreparedMaterialSlot slot, MaterialValue? value)
            {
                if (value is not MaterialValue.PackageMaterial material) return;
                TextTarget target = new TextTarget.Base(output.Target);
                bool written = output.Update.MaterialEdits.Any(edit => edit switch
                { MaterialEdit.ReplaceMaterial replace => (int)replace.Slot == (int)slot, MaterialEdit.Uniform uniform => (int)uniform.Slot == (int)slot, _ => false });
                if (written)
                {
                    if (!incoming.Remove((output.Target, slot), out var binding) || binding.Owner is not PreparedMaterialOwner.Script
                        || !SameMaterial(binding.Material, material))
                        throw PartitionError("BaseMaterialOwnershipRequired", "Base 材质写入须有该部位的真实脚本来源。", target);
                    result.Add(binding);
                }
                else
                {
                    // Inherit only the already validated complete parent binding. No fabricated source path/controller.
                    var parent = Materials.SingleOrDefault(binding => binding.Target == new ImageEffectTarget.Token(new(output.Target.TokenId, output.Target.BindingId)) && binding.Slot == slot);
                    if (parent is null || !SameMaterial(parent.Material, material))
                        throw PartitionError("InvalidPreparedBaseMaterialInheritance", "继承 Base 包材质须匹配已验证父字族同部位。", target);
                    result.Add(new(parent.Owner, output.Target, slot, material));
                }
            }
        }
        if (incoming.Count != 0)
            throw PartitionError("InvalidPreparedBaseMaterial", "Base 材质来源不属于最终存活包材质部位。", new TextTarget.Base(incoming.Keys.First().Item1));
        return result.ToImmutable();
    }
    // Keep public PackageMaterial/ParameterValues equality unchanged; validate full internal binding identity here.
    private static bool SameMaterial(MaterialValue.PackageMaterial a, MaterialValue.PackageMaterial b)
        => a.Handle == b.Handle && a.Parameters.ContentEquals(b.Parameters) && GraphBindingValue.UniformsEqual(a.Uniforms, b.Uniforms);
}
