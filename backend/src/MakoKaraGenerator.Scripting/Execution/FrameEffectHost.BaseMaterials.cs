using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private static void BaseMaterialAction(TextTarget? target, Action action)
    {
        try { action(); }
        catch (Exception error) when (target is TextTarget.Base && error is not (OperationCanceledException or EffectFailureException))
        {
            string code = error switch { EffectOutputException output => output.Code, PreparedFrameException prepared => prepared.Code, _ => "BaseMaterialCompositionFailed" };
            var marked = new PreparedFrameException(code, error.Message) { TextTarget = target };
            marked.Data["OriginalFailure"] = error; throw marked;
        }
    }

    private static IEnumerable<MaterialSlot> MaterialSlots(StyleUpdate update) => update.MaterialEdits.Select(edit => edit switch
    { MaterialEdit.ReplaceMaterial replace => replace.Slot, MaterialEdit.Uniform uniform => uniform.Slot, _ => throw new InvalidOperationException("未知材质操作。") }).Distinct();

    private static ImmutableArray<PreparedBaseMaterialBinding> ComposeBaseMaterials(PreparedRenderFrame parent,
        Dictionary<BaseTokenTarget, StyleUpdate> bases, Dictionary<(BaseTokenTarget, MaterialSlot), ActiveEffect> owners,
        long timeUs, RuntimeDiagnosticBuffer diagnostics, CancellationToken token)
    {
        var result = ImmutableArray.CreateBuilder<PreparedBaseMaterialBinding>();
        var sources = StaticStyleProjection.LoadedSourceStyles(parent.Snapshot, parent.HierarchyScene);
        foreach (var (target, update) in bases)
        {
            token.ThrowIfCancellationRequested();
            var family = new TokenFamilyTarget(target.TokenId, target.BindingId);
            var basis = parent.TokenStyles.Any(item => item.Target == family) ? parent.GetTokenAppearance(family)
                : StaticStyleProjection.View(sources[family], parent.SourceMaterials);
            foreach (var slot in MaterialSlots(update))
            {
                if (!owners.TryGetValue((target, slot), out var active))
                    throw new PreparedFrameException("BaseMaterialOwnershipRequired", "Base 材质部位缺少真实写入者。") { TextTarget = new TextTarget.Base(target) };
                Invoke(active, "ComposeOutputs", timeUs, diagnostics, _ => BaseMaterialAction(new TextTarget.Base(target), () =>
                {
                    var resolved = update.ApplyMaterials(basis);
                    var value = slot switch { MaterialSlot.UnsungFill => resolved.UnsungMaterial, MaterialSlot.SungFill => resolved.SungMaterial,
                        MaterialSlot.Stroke => resolved.Stroke?.Material, _ => throw new InvalidOperationException("未知材质部位。") };
                    if (value is MaterialValue.PackageMaterial material)
                    {
                        ValidateMaterialExport(active.Definition, material.Handle, slot, material.Parameters);
                        result.Add(new(new PreparedMaterialOwner.Script(active.Definition.Id, active.Definition.RuntimeHash)
                        { PackageIdentity = active.Definition.PackageIdentity }, target, (PreparedMaterialSlot)slot, material));
                    }
                }));
            }
        }
        return result.ToImmutable();
    }
}
