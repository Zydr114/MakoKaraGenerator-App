using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private sealed class MaterialUniformScopes
    {
        private sealed record Pending(long Order, ActiveEffect Owner, TextWrite.MaterialUniform Write);
        private long _sequence;
        private readonly Dictionary<(TextTarget Target, MaterialSlot Slot), Dictionary<(MaterialHandle Handle, string Node, string Name), Pending>> _scopes = [];

        internal void Observe(ActiveEffect active, FrameOutput output, GraphBindingBudget budget, CancellationToken token)
        {
            if (!active.Definition.IsV2)
            {
                // Legacy edits retain their existing eager identity semantics, but
                // a real legacy replacement also closes any preceding v2 scope.
                foreach (var item in output.TokenStyles.Select(item => (Target: (TextTarget)new TextTarget.Family(item.Target), Update: item.Update))
                    .Concat(output.MarkStyles.Select(item => (Target: (TextTarget)new TextTarget.Mark(item.Target), Update: item.Update))))
                    foreach (var edit in item.Update.MaterialEdits)
                        switch (edit)
                        {
                            case MaterialEdit.ReplaceMaterial replace: Reset(item.Target, replace.Slot); break;
                            case MaterialEdit.Uniform uniform:
                                if (_scopes.TryGetValue((item.Target, uniform.Slot), out var scope)) scope.Remove((uniform.Handle, uniform.NodeId, uniform.Name));
                                break;
                        }
                return;
            }
            if (output.MaterialWrites.IsDefault) throw new EffectOutputException("InvalidMaterialWrite", "材质事务不能是 default。");
            foreach (var write in output.MaterialWrites)
            {
                token.ThrowIfCancellationRequested();
                var definition = active.Definition;
                string target = write.Target switch { TextTarget.Family => "tokenFamily", TextTarget.Mark => "textMark", TextTarget.Base => "baseToken",
                    _ => throw new EffectOutputException("MaterialUniformTargetPending", "此材质目标的生产调度尚未接通。") };
                if (definition.PackageDeclaration is not { } declaration || !declaration.Outputs.Any(item => item.Kind == "StyleModulation" && item.Target == target))
                    throw new EffectOutputException("UndeclaredPackageOutput", "材质写入须声明真实 StyleModulation 目标：" + target);
                BaseMaterialAction(write.Target, () =>
                {
                    switch (write)
                    {
                        case TextWrite.Fill fill:
                            Replacement(fill.Target, fill.State == TextState.Unsung ? MaterialSlot.UnsungFill : MaterialSlot.SungFill, fill.Material); break;
                        case TextWrite.Strokes strokes:
                            Replacement(strokes.Target, strokes.State == TextState.Unsung ? MaterialSlot.UnsungStroke : MaterialSlot.SungStroke,
                                strokes.Layers.IsEmpty ? null : strokes.Layers[0].Material); break;
                        case TextWrite.MaterialUniform uniform:
                            if (uniform.Target is not (TextTarget.Family or TextTarget.Base or TextTarget.Mark))
                                throw new EffectOutputException("MaterialUniformTargetPending", "uniform 只支持真实字族、基础字或标记。");
                            if (!definition.PackageDefinition!.Contents.Requires.Contains("material.uniform", StringComparer.Ordinal))
                                throw new EffectOutputException("UndeclaredCapability", "包未声明 material.uniform。");
                            budget.ChargeMaterialUniform(uniform.Material, uniform.NodeId, uniform.Name, uniform.Value, token);
                            ValidateMaterialExport(definition, uniform.Material, uniform.Slot, null);
                            var key = (uniform.Target, uniform.Slot);
                            if (!_scopes.TryGetValue(key, out var scope)) _scopes.Add(key, scope = []);
                            scope[(uniform.Material, uniform.NodeId, uniform.Name)] = new(++_sequence, active, uniform);
                            break;
                        default: throw new EffectOutputException("InvalidMaterialWrite", "未知材质事务写入。");
                    }
                    void Replacement(TextTarget replacementTarget, MaterialSlot slot, MaterialValue? material)
                    {
                        budget.ChargeMaterial(material, token);
                        if (material is MaterialValue.PackageMaterial package)
                        {
                            ValidateMaterialExport(definition, package.Handle, slot, package.Parameters);
                        }
                        Reset(replacementTarget, slot);
                    }
                });
            }
        }
        private void Reset(TextTarget target, MaterialSlot slot) => _scopes.Remove((target, slot));

        internal void Complete(Dictionary<TokenFamilyTarget, StyleUpdate> styles, Dictionary<TextMarkTarget, StyleUpdate> marks,
            Dictionary<BaseTokenTarget, StyleUpdate> bases, Dictionary<(BaseTokenTarget, MaterialSlot), ActiveEffect> baseMaterialOwners,
            BaselineStyles baseline, Dictionary<TokenFamilyTarget, ActiveEffect> styleOwners, Dictionary<TextMarkTarget, ActiveEffect> markOwners,
            Dictionary<(ImageEffectTarget, PreparedMaterialSlot), PreparedMaterialBinding> materials, long timeUs,
            RuntimeDiagnosticBuffer diagnostics, CancellationToken token)
        {
            // Resolve parent uniforms first, then Base leaf scopes against the final parent material.
            foreach (var (key, scope) in _scopes.OrderBy(pair => pair.Key.Target is TextTarget.Base ? 1 : 0))
                foreach (var pending in scope.Values.OrderBy(item => item.Order))
                {
                    token.ThrowIfCancellationRequested(); var active = pending.Owner; var write = pending.Write;
                    Invoke(active, "ComposeOutputs", timeUs, diagnostics, _ => BaseMaterialAction(key.Target, () =>
                    {
                        StyleUpdate update; StyleView source; ImageEffectTarget target;
                        if (key.Target is TextTarget.Family family)
                        { update = styles.GetValueOrDefault(family.Value) ?? new(); source = baseline.Get(family.Value); target = new ImageEffectTarget.Token(family.Value); }
                        else if (key.Target is TextTarget.Base basic)
                        {
                            var familyTarget = new TokenFamilyTarget(basic.Value.TokenId, basic.Value.BindingId);
                            source = (styles.GetValueOrDefault(familyTarget) ?? new()).ApplyMaterials(baseline.Get(familyTarget));
                            update = bases.GetValueOrDefault(basic.Value) ?? new(); target = null!;
                        }
                        else if (key.Target is TextTarget.Mark mark)
                        { update = marks.GetValueOrDefault(mark.Value) ?? new(); source = baseline.Get(mark.Value); target = new ImageEffectTarget.Mark(mark.Value); }
                        else throw new EffectOutputException("MaterialUniformTargetPending", "此 uniform 目标未接通。");
                        var resolved = update.ApplyMaterials(source);
                        var installed = resolved.Material(key.Slot);
                        if (installed is not MaterialValue.PackageMaterial material || material.Handle != write.Material)
                            throw new EffectOutputException("MaterialUniformIdentityMismatch", "最终安装材质与本作用域存活 uniform 的固定身份不匹配。");
                        var export = ValidateMaterialExport(active.Definition, material.Handle, key.Slot, material.Parameters);
                        MaterialUniformValidation.Validate(active.Definition.PackageDefinition!.ImageGraphs[export.GraphPath].Plan, write.NodeId, write.Name, write.Value);
                        var edit = StyleUpdate.Uniform(new(key.Slot, write.Material, write.NodeId, write.Name, write.Value));
                        update = update.Then(edit);
                        if (key.Target is TextTarget.Family tokenTarget) { styles[tokenTarget.Value] = update; styleOwners[tokenTarget.Value] = active; }
                        else if (key.Target is TextTarget.Mark markTarget) { marks[markTarget.Value] = update; markOwners[markTarget.Value] = active; }
                        if (key.Target is TextTarget.Base basicTarget)
                        {
                            bases[basicTarget.Value] = update;
                            baseMaterialOwners[(basicTarget.Value, key.Slot)] = active;
                        }
                        else TrackMaterials(target, edit, update.ApplyMaterials(source), active, materials);
                    }));
                }
        }
    }
}
