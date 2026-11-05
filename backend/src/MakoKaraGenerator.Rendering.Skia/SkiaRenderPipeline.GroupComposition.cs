using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private void DrawGroupCompositionScene(SKSurface main, RenderRequest request, ImageSurfaceBudgetFrame resources, SceneBackdropFrame backdropFrame,
        Action<SKSurface, SceneDrawItem, ImageObjectState?, SceneCompositionPrefix?> drawObject,
        HashSet<PreparedImageEffectBinding> consumed, ImmutableArray<RenderImageEffectDiagnostic>.Builder graphDiagnostics,
        ImmutableArray<RuntimeDiagnostic>.Builder diagnostics)
    {
        try { DrawGroupCompositionSceneCore(main, request, resources, backdropFrame, drawObject, consumed, graphDiagnostics, diagnostics); }
        catch (OperationCanceledException) { throw; }
        catch (EffectFailureException) { throw; }
        catch (SourceMaterialFailureException) { throw; }
        catch (Exception error)
        {
            // Owned Below can schedule a frame without any group binding.
            throw SceneOwnerFailure(backdropFrame.Owner, backdropFrame.Owner.Stage, request.TimeUs, SceneBackdropCode(error), error);
        }
    }
    private void DrawGroupCompositionSceneCore(SKSurface main, RenderRequest request, ImageSurfaceBudgetFrame resources, SceneBackdropFrame backdropFrame,
        Action<SKSurface, SceneDrawItem, ImageObjectState?, SceneCompositionPrefix?> drawObject,
        HashSet<PreparedImageEffectBinding> consumed, ImmutableArray<RenderImageEffectDiagnostic>.Builder graphDiagnostics,
        ImmutableArray<RuntimeDiagnostic>.Builder diagnostics)
    {
        var prepared = request.PreparedFrame!;
        var frame = prepared.HierarchyScene; var items = DrawItems(frame).ToArray();
        var nodes = frame.Elements.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var limits = backdropFrame.Work;
        var groups = prepared.ImageEffects.Where(binding => binding.Binding.Target is ImageEffectTarget.Group)
            .GroupBy(binding => ((ImageEffectTarget.Group)binding.Binding.Target).Target.NodeId.Value)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray(), StringComparer.Ordinal);
        var empty = new HashSet<string>(StringComparer.Ordinal);
        // This occurs before tree construction and before checking geometric emptiness.
        foreach (var pair in groups)
            foreach (var binding in pair.Value)
            {
                try
                {
                    limits.Binding(); request.CancellationToken.ThrowIfCancellationRequested();
                    var target = (ImageEffectTarget.Group)binding.Binding.Target;
                    if (!nodes.TryGetValue(pair.Key, out var node) || node.Kind != SceneElementKind.Group || node.SourceNodeId is not null
                        || !prepared.FinalGroupGeometry.Any(value => value.Target == target.Target))
                        throw new PreparedFrameException("InvalidPreparedImageEffectTarget", "组图只能绑定本帧真实 source group，不能绑定冻结 alias。");
                    HashSet<string> ancestors = new(StringComparer.Ordinal); string? current = pair.Key;
                    while (current is not null)
                    {
                        if (ancestors.Count >= GroupCompositionBudget.MaximumAncestry)
                            throw new PreparedFrameException("GroupCompositionBudgetExceeded", "真实组运行父树超过固定深度。");
                        if (!ancestors.Add(current) || !nodes.TryGetValue(current, out var ancestor))
                            throw new PreparedFrameException("InvalidSceneIsolationTree", "真实组运行父树含环或缺节点。");
                        current = ancestor.ParentId;
                    }
                    var program = _imageEffects!.GetProgram(binding.Binding.Graph);
                    var context = CompositionContext(request, program.Bounds) with { Parameters = binding.Binding.Parameters, Uniforms = binding.Binding.Uniforms, Transforms = binding.Binding.Transforms };
                    program.Graph.ValidateScenePreflight(context, _imageEffects.GetImages(program.Handle), program.GetInputRecipe, request.CancellationToken);
                    foreach (var declaration in program.Graph.Plan.Definition.Inputs)
                    {
                        var source = declaration.ChoiceParameter is { } choice ? declaration.Choices[binding.Binding.Parameters.GetEnum(choice)] : declaration.Source;
                        if (SceneCaptureChannels.IsSupported(source))
                        {
                            var recipe = program.GetInputRecipe(declaration.Id);
                            if (!Enum.IsDefined(recipe.Region)) throw new PreparedFrameException("UnsupportedCaptureChannel", "组配方区域无效。");
                            _ = SelectCaptureItems(frame, new SceneCaptureTarget.Group(target.Target), recipe);
                        }
                    }
                    consumed.Add(binding);
                    if (prepared.FinalGroupGeometry.Single(value => value.Target == target.Target).WorldInkCorners.IsEmpty)
                    {
                        empty.Add(pair.Key);
                        diagnostics.Add(new(binding.InstanceId, binding.ArtifactHash, "RenderImageEffects", request.TimeUs,
                            new(DiagnosticSeverity.Info, "GroupImageEffectNoVisibleChildren", "组 " + pair.Key + " 无实际可见孩子；绑定已验证并消费，未执行图。"))
                            { PackageIdentity = binding.PackageIdentity });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (EffectFailureException) { throw; }
                catch (Exception error) { throw GroupCompositionFailure(binding, request, error); }
            }
        var opacity = prepared.GroupOpacities.Where(value => nodes[value.Opacity.Target.NodeId.Value].SourceNodeId is null)
            .ToDictionary(value => value.Opacity.Target.NodeId.Value, StringComparer.Ordinal);
        var selectedGroups = opacity.Keys.Concat(groups.Keys.Where(id => !empty.Contains(id))).ToHashSet(StringComparer.Ordinal);
        IsolationUnit root = new(); Dictionary<string, IsolationUnit> units = new(StringComparer.Ordinal);
        for (int index = 0; index < items.Length; index++)
        {
            List<string> chain = []; HashSet<string> seen = new(StringComparer.Ordinal);
            string? current = items[index].NodeId;
            while (current is not null)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                if (seen.Count >= GroupCompositionBudget.MaximumAncestry)
                    throw new PreparedFrameException("GroupCompositionBudgetExceeded", "运行父树深度超过固定预算。");
                if (!seen.Add(current)) throw new PreparedFrameException("InvalidSceneIsolationTree", "隔离运行父树含环。");
                if (!nodes.TryGetValue(current, out var node)) throw new PreparedFrameException("InvalidSceneIsolationTree", "隔离运行父树缺少节点。");
                string source = node.SourceNodeId ?? current;
                if (selectedGroups.Contains(source) && !chain.Contains(source, StringComparer.Ordinal)) chain.Add(source);
                current = node.ParentId;
            }
            GroupCompositionBudget.Depth(chain.Count);
            IsolationUnit unit = root;
            for (int depth = chain.Count - 1; depth >= 0; depth--)
            {
                string id = chain[depth];
                if (!units.TryGetValue(id, out var group))
                {
                    limits.Unit(); group = new(opacity.GetValueOrDefault(id)) { GroupId = id,
                        GroupBindings = empty.Contains(id) ? [] : groups.GetValueOrDefault(id, []) };
                    units.Add(id, group); unit.Children.Add(group);
                }
                else if (!unit.Children.Contains(group))
                    throw new PreparedFrameException("InvalidSceneIsolationTree", "同一 canonical 组出现不同隔离父级。");
                group.Indices.Add(index); unit = group;
            }
            limits.Unit(); IsolationUnit leaf = new(item: items[index]); leaf.Indices.Add(index); unit.Children.Add(leaf);
        }
        foreach (var unit in units.Values)
            if (unit.Indices[^1] - unit.Indices[0] + 1 != unit.Indices.Count)
            {
                var owner = CompositionOwner(unit);
                diagnostics.Add(new(owner.InstanceId, owner.ArtifactHash, "RenderGroupIsolation", request.TimeUs,
                    new(DiagnosticSeverity.Warning, "GroupIsolationChangesOrder", "组 " + unit.GroupId + " 的后代原本与外部内容交错；组隔离后作为连续单位在首项位置合成喵。"))
                    { PackageIdentity = owner.PackageIdentity });
            }
        var bounds = GroupCompositionBounds(request, items, resources);
        using var rootPrefix = new SceneCompositionPrefix(_compositionProgram, resources, CompositionContext(request, bounds), request.CancellationToken);
        foreach (var child in root.Children.OrderBy(unit => unit.Order))
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var owner = child.Item is null ? GroupSceneOwner(child) : backdropFrame.Owner;
            try
            {
                using var closed = child.Item is { } item
                    ? RenderLeaf(item, rootPrefix, owner)
                    : RenderGroup(child, rootPrefix, 1);
                using var payload = ComposeClosedLayers(request, closed, closed.Layers);
                // Final screen application always retains ALL real BehindText.
                // Owner filtering applies only to Below inputs inside graphs.
                using var actualBelow = SnapshotSceneBelow(main, request, bounds, closed);
                using var final = _compositionProgram.CompositePayload(payload, actualBelow, resources, cancellationToken: request.CancellationToken);
                DrawGraphSurface(main.Canvas, final, SKBlendMode.Src);
                // Includes plain root leaves and complete independent erasure.
                rootPrefix.Append(payload);
            }
            catch (OperationCanceledException) { throw; }
            catch (EffectFailureException) { throw; }
            catch (SourceMaterialFailureException) { throw; }
            catch (Exception error) { throw SceneOwnerFailure(owner, owner.Stage, request.TimeUs, SceneBackdropCode(error), error); }
        }

        ImageObjectState RenderLeaf(SceneDrawItem item, SceneCompositionPrefix enterPrefix, SceneEffectOwner owner)
        {
            var state = new ImageObjectState(bounds, _surfaceBudget.Limits, resources)
            { ScopeBudget = limits, Backdrop = backdropFrame, OutsidePrefix = enterPrefix, CompositionOwner = owner };
            try { drawObject(main, item, state, enterPrefix); return state; }
            catch { state.Dispose(); throw; }
        }

        ImageObjectState RenderGroup(IsolationUnit unit, SceneCompositionPrefix enterPrefix, int depth)
        {
            var owner = GroupSceneOwner(unit);
            ImageObjectState? state = null;
            try
            {
                GroupCompositionBudget.Depth(depth); request.CancellationToken.ThrowIfCancellationRequested();
                state = new(bounds, _surfaceBudget.Limits, resources)
                { ScopeBudget = limits, Backdrop = backdropFrame, OutsidePrefix = enterPrefix, CompositionOwner = owner };
                using var prefix = enterPrefix.Fork();
                foreach (var child in unit.Children.OrderBy(value => value.Order))
                {
                    request.CancellationToken.ThrowIfCancellationRequested();
                    int first = state.Layers.Count;
                    if (child.Item is { } item) drawObject(main, item, state, prefix);
                    else
                    {
                        using var completed = RenderGroup(child, prefix, depth + 1);
                        AppendClosedGroupLayers(request, completed, state);
                    }
                    state.SetLayerBudget(state.Layers.Count);
                    if (state.Layers.Count > first)
                    {
                        using var addition = ComposeClosedLayers(request, state, state.Layers.Skip(first));
                        prefix.Append(addition);
                    }
                }
                // Parent samples the immutable entering prefix, excluding every
                // descendant, while each child saw earlier completed siblings.
                foreach (var binding in unit.GroupBindings)
                {
                    try { ApplyGroupImageBinding(request, binding, _imageEffects!.GetProgram(binding.Binding.Graph), state, graphDiagnostics, diagnostics); }
                    catch (OperationCanceledException) { throw; }
                    catch (EffectFailureException) { throw; }
                    catch (Exception error) { throw GroupCompositionFailure(binding, request, error); }
                }
                CloseGroupOpacity(request, state, unit.Opacity?.Opacity.Factor ?? 1);
                var result = state; state = null; return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (EffectFailureException) { throw; }
            catch (SourceMaterialFailureException) { throw; }
            catch (Exception error) { throw SceneOwnerFailure(owner, owner.Stage, request.TimeUs, SceneBackdropCode(error), error); }
            finally { state?.Dispose(); }
        }
    }
    private static SceneEffectOwner GroupSceneOwner(IsolationUnit unit)
    {
        var owner = CompositionOwner(unit);
        return new(new PreparedMaterialOwner.Script(owner.InstanceId, owner.ArtifactHash) { PackageIdentity = owner.PackageIdentity }, "RenderGroupIsolation");
    }

    private sealed record GroupCompositionOwner(EffectInstanceId InstanceId, string ArtifactHash, EffectPackageIdentity? PackageIdentity);
    private static GroupCompositionOwner CompositionOwner(IsolationUnit unit)
        => !unit.GroupBindings.IsEmpty
            ? new(unit.GroupBindings[0].InstanceId, unit.GroupBindings[0].ArtifactHash, unit.GroupBindings[0].PackageIdentity)
            : new(unit.Opacity!.InstanceId, unit.Opacity.ArtifactHash, unit.Opacity.PackageIdentity);

    private static EffectFailureException GroupCompositionFailure(PreparedImageEffectBinding binding, RenderRequest request, Exception error)
        => GroupCompositionFailure(new GroupCompositionOwner(binding.InstanceId, binding.ArtifactHash, binding.PackageIdentity), request, error);
    private static EffectFailureException GroupCompositionFailure(GroupCompositionOwner binding, RenderRequest request, Exception error)
    {
        string code = error switch { PreparedFrameException value => value.Code, ImageGraphException value => value.Code,
            ImageSurfaceBudgetException value => value.Code, EffectPackageException value => value.Code,
            ShaderProgramException value => value.Code, EffectOutputException value => value.Code, _ => "GroupCompositionFailed" };
        return new(binding.InstanceId, binding.ArtifactHash, "RenderImageEffects", request.TimeUs, code, error, binding.PackageIdentity);
    }
    private GraphRect GroupCompositionBounds(RenderRequest request, SceneDrawItem[] items, ImageSurfaceBudgetFrame resources)
    {
        var composition = request.Project.Composition;
        GraphRect bounds = new(0, 0, composition.CanvasWidth, composition.CanvasHeight);
        foreach (var binding in request.PreparedFrame!.ImageEffects)
        {
            try
            {
                var program = _imageEffects!.GetProgram(binding.Binding.Graph);
                bounds = Union(bounds, program.Bounds.Expand(program.Graph.Plan.Definition.MaximumExpandPx));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw GroupCompositionFailure(binding, request, error); }
        }
        foreach (var material in request.PreparedFrame.Materials.Select(ObjectMaterialBinding.From)
            .Concat(request.PreparedFrame.BaseMaterials.Select(ObjectMaterialBinding.From)))
        {
            try { bounds = Union(bounds, _imageEffects!.GetMaterial(material.Material.Handle).Bounds); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw material.Owner.Failure("RenderMaterials", request.TimeUs,
                error is PreparedFrameException prepared ? prepared.Code : "MaterialBoundsUnavailable",
                MaterialTargetError(error, material.TextTarget, error is PreparedFrameException failure ? failure.Code : "MaterialBoundsUnavailable")); }
        }
        foreach (var item in items) if (HasOriginalGlow(request, item)) bounds = IncludeOriginalGlowBounds(request, item, bounds, resources);
        return bounds;
    }
}
