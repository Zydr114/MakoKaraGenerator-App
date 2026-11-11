using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

// 对象级渲染计划：主计划 §4.4 RenderExecutionPlan 限于图场景单个对象（一行或一个文字标记）的最小形态。
// 计划只含数据，先建后执行：建计划读取本帧准备结果与图声明，不分配表面、不绘制；执行器只照计划分配与绘制。
public sealed partial class SkiaRenderPipeline
{
    /// <summary>对象部件在计划中的处理方式。</summary>
    private enum ObjectPartKind
    {
        /// <summary>按原样捕获该色彩部件。</summary>
        Original,
        /// <summary>执行材质，产物作为该部件的层。</summary>
        Material,
        /// <summary>本帧必然没有像素（F3）：不捕获、不执行，只占共享空层。</summary>
        KnownEmpty,
    }

    /// <summary>对象读取「下层」的来源。</summary>
    private enum ObjectBelowSource
    {
        /// <summary>遗留调度：开始前对目标表面取一次快照，对象完成后以它为底合成并写回。</summary>
        DestinationSnapshot,
        /// <summary>前缀调度：不取快照，图与材质按属主从组合前缀惰性解析下层。</summary>
        CompositionPrefix,
    }

    /// <summary>一个部件层的来历：哪个成员的哪个色彩部件、层序，以及原样捕获、材质还是已知为空。</summary>
    /// <param name="Member">整行、标记或元素族。</param>
    /// <param name="Part">色彩部件。</param>
    /// <param name="Order">层序：部件序在外、成员序在内，与逐成员直绘的叠放一致。</param>
    /// <param name="Kind">处理方式。</param>
    /// <param name="Sung">填充分两态时为已唱／未唱；不分态的部件为 null。</param>
    /// <param name="Material">材质绑定及其程序；仅 <see cref="ObjectPartKind.Material" /> 有值。</param>
    /// <param name="MaterialProgram">材质程序。</param>
    private sealed record ObjectPartStep(ImageEffectTarget Member, SceneColorPart Part, int Order, ObjectPartKind Kind, bool? Sung,
        ObjectMaterialBinding? Material = null, SceneMaterialProgram? MaterialProgram = null);

    /// <summary>一次图执行：绑定、程序与本次执行的目标框（第 12 条）。</summary>
    private sealed record ObjectGraphStep(PreparedImageEffectBinding Binding, SceneImageGraphProgram Program, GraphRect TargetBounds);

    /// <summary>一个对象本帧的完整计划。</summary>
    /// <param name="Item">被绘制的行或文字标记。</param>
    /// <param name="Target">对象自身的目标（整行或标记）。</param>
    /// <param name="ExcludedTokens">有自己图或材质的元素族；对象成员的部件不含它们。</param>
    /// <param name="Parts">部件层，按建立顺序：对象成员在前，各元素族按行内顺序在后。</param>
    /// <param name="Graphs">图的执行序列：各元素族的图在前（按行内顺序），整行或标记的图在后，每个目标内保留工程实例顺序。</param>
    /// <param name="TargetRegion">材质执行所用的对象实际区域（O7）；null 时沿用程序框。</param>
    /// <param name="GraphTargets">各图绑定的目标框，与 <paramref name="Graphs" /> 中的相同。</param>
    /// <param name="WorkBounds">对象的工作框：部件捕获、下层与层组装的范围。</param>
    /// <param name="Below">下层来源。</param>
    /// <param name="ClosedExport">为真时不写目标表面，把闭合的层导出给组合调度器。</param>
    /// <param name="ExecutionOwner">执行开始时的失败属主（建计划期间为核对归属推进过的属主在此复位）。</param>
    private sealed record ObjectRenderPlan(SceneDrawItem Item, ImageEffectTarget Target, HashSet<string> ExcludedTokens,
        ImmutableArray<ObjectPartStep> Parts, ImmutableArray<ObjectGraphStep> Graphs, GraphRect? TargetRegion,
        IReadOnlyDictionary<PreparedImageEffectBinding, GraphRect> GraphTargets, GraphRect WorkBounds,
        ObjectBelowSource Below, bool ClosedExport, SceneEffectOwner ExecutionOwner);

    /// <summary>当前失败应归属的效果或材质；建计划与执行逐步推进，出错时由对象入口读取。</summary>
    private sealed class ObjectOwnerCell(SceneEffectOwner value)
    {
        internal SceneEffectOwner Value { get; set; } = value;
    }

    /// <summary>为带图或材质、或处在组合导出中的对象建立计划。</summary>
    /// <remarks>
    /// 只读取本帧准备结果、几何与图声明（Warp 映射会记入本帧投影工作账），不分配表面。
    /// 目标框与工作框只由本帧几何和声明决定，不看调度器，组合导出时工作框沿用调度器给定的框。
    /// </remarks>
    private ObjectRenderPlan BuildObjectPlan(ImageSceneRun run, SceneDrawItem item, PreparedImageEffectBinding[] bindings,
        ObjectMaterialBinding[] materials, ImageObjectState? export, ObjectOwnerCell owner)
    {
        var request = run.Request; var resources = run.Resources; var prepared = request.PreparedFrame!;
        var programs = bindings.Select(binding => { owner.Value = new(binding); return _imageEffects!.GetProgram(binding.Binding.Graph); }).ToArray();
        var materialPrograms = materials.Select(binding => { owner.Value = new(binding.Owner, "RenderMaterials", binding.TextTarget); return _imageEffects!.GetMaterial(binding.Material.Handle); }).ToArray();
        var target = item.Line is { } line
            ? (ImageEffectTarget)new ImageEffectTarget.Line(new(new(line.Frame.Line!.Id), new(line.BindingId!)))
            : new ImageEffectTarget.Mark(new(new(item.Mark!.NodeId), new(item.Mark.CueId), new(item.Mark.Geometry.BindingId!)));
        var tokenTargets = bindings.Select(binding => binding.Binding.Target).Concat(materials.Select(binding => binding.Target)).OfType<ImageEffectTarget.Token>()
            .Distinct().OrderBy(token => Array.FindIndex(item.Line!.Frame.Line!.Tokens.ToArray(), source => source.Id == token.Target.TokenId.Value)).ToArray();
        HashSet<string> excluded = tokenTargets.Select(token => token.Target.TokenId.Value).ToHashSet(StringComparer.Ordinal);
        // 实际执行顺序：各元素族的图在前（按行内顺序），整行或标记的图在后；每个目标内保留工程实例顺序。
        var sequence = tokenTargets.SelectMany(token => bindings.Where(binding => binding.Binding.Target == token))
            .Concat(bindings.Where(binding => binding.Binding.Target == target)).ToArray();
        // O7：材质按本帧对象实际区域执行；第 12 条：图按绑定目标的实际区域执行。两者都只由本帧几何决定，两条调度路径一致；
        // 独立对象的工作框随之收紧，组合导出沿用调度器给定的框。
        var region = materials.Length > 0 ? ObjectTargetRegion(request, item, resources) : null;
        var graphTargets = GraphTargetBounds(request, item, target, sequence.Select(binding => (binding, programs[Array.IndexOf(bindings, binding)])), resources);
        bool tightened = region is not null || bindings.Select((binding, index) => graphTargets[binding] != programs[index].Bounds).Any(changed => changed);
        var bounds = export?.Bounds ?? bindings.Select((binding, index) => graphTargets[binding].Expand(programs[index].Graph.Plan.Definition.MaximumExpandPx))
            .Concat(materialPrograms.Select(program => MaterialWorkBounds(region, program))).Aggregate(Union);
        bool hasGlow = HasOriginalGlow(request, item);
        if (hasGlow) bounds = IncludeOriginalGlowBounds(request, item, bounds, resources);
        if (export is null && tightened) bounds = AlignToOutputPixels(request, bounds);
        var executionOwner = owner.Value;
        var knownEmpty = SkipKnownEmptyParts ? ObjectKnownEmptyParts(request, item) : default;
        var parts = ImmutableArray.CreateBuilder<ObjectPartStep>();
        AddParts(target, 0);
        for (int i = 0; i < tokenTargets.Length; i++) AddParts(tokenTargets[i], i + 1);
        return new(item, target, excluded, parts.ToImmutable(),
            [.. sequence.Select(binding => new ObjectGraphStep(binding, programs[Array.IndexOf(bindings, binding)], graphTargets[binding]))],
            region, graphTargets, bounds, run.Backdrop is null ? ObjectBelowSource.DestinationSnapshot : ObjectBelowSource.CompositionPrefix,
            export is not null, executionOwner);

        void AddParts(ImageEffectTarget member, int memberOrder)
        {
            for (int partOrder = 0; partOrder < ColorPartOrder.Length; partOrder++)
            {
                var part = ColorPartOrder[partOrder];
                if (!hasGlow && part is SceneColorPart.BaseGlow or SceneColorPart.RubyGlow) continue;
                if (member is ImageEffectTarget.Mark && IsRuby(part)) continue;
                // An explicit Base appearance shadows the parent's slot even when it is Solid/Gradient/null.
                var captureTarget = member is ImageEffectTarget.Token token && !IsRuby(part)
                    && prepared.BaseAppearances.ContainsKey(new(token.Target.TokenId, token.Target.BindingId))
                    ? (SceneCaptureTarget)new SceneCaptureTarget.Base(new(token.Target.TokenId, token.Target.BindingId)) : CaptureTarget(member);
                var slotBindings = materials.Where(binding => binding.CaptureTarget == captureTarget && (part is SceneColorPart.BaseStroke or SceneColorPart.RubyStroke
                    ? binding.Slot == PreparedMaterialSlot.Stroke : part is SceneColorPart.BaseFill or SceneColorPart.RubyFill
                        && binding.Slot != PreparedMaterialSlot.Stroke)).ToArray();
                int order = partOrder * (tokenTargets.Length + 1) + memberOrder;
                bool fill = part is SceneColorPart.BaseFill or SceneColorPart.RubyFill;
                // F3：本帧必然为空的部件先判空再分配，不捕获、不执行材质，只占共享空层；层数与层序不变。
                if (knownEmpty.Contains(part, slotBindings.Length > 0))
                {
                    if (slotBindings.Length > 0 && fill) { Add(ObjectPartKind.KnownEmpty, false); Add(ObjectPartKind.KnownEmpty, true); }
                    else Add(ObjectPartKind.KnownEmpty, null);
                    continue;
                }
                if (slotBindings.Length > 0)
                {
                    if (fill) { AddSide(false); AddSide(true); }
                    else AddMaterial(slotBindings[0], null);
                    continue;
                }
                Add(ObjectPartKind.Original, null);

                void Add(ObjectPartKind kind, bool? sung) => parts.Add(new(member, part, order, kind, sung));
                void AddSide(bool sung)
                {
                    var binding = slotBindings.SingleOrDefault(binding => binding.Slot == (sung ? PreparedMaterialSlot.SungFill : PreparedMaterialSlot.UnsungFill));
                    if (binding is null) Add(ObjectPartKind.Original, sung); else AddMaterial(binding, sung);
                }
                void AddMaterial(ObjectMaterialBinding binding, bool? sung)
                {
                    // 同一槽位重复绑定等计划期错误，归属与逐部件执行时相同：推进到最近一个材质部件的属主。
                    owner.Value = new(binding.Owner, "RenderMaterials", binding.TextTarget);
                    parts.Add(new(member, part, order, ObjectPartKind.Material, sung, binding, materialPrograms[Array.IndexOf(materials, binding)]));
                }
            }
        }
    }
}
