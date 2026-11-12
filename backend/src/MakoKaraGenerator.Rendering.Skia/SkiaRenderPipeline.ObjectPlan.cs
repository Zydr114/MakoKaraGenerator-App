using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

// 对象级渲染计划：主计划 §4.4 RenderExecutionPlan 限于图场景单个对象（一行或一个文字标记）的最小形态。
// 计划只含数据，先建后执行：建计划读取本帧准备结果与图声明，不分配表面、不绘制；执行器只照计划分配与绘制。
//
// 建计划分几步，每一步只改计划：
//   基本计划（身份、程序、图的执行序列，区域一律取程序框）
//   → 区域收紧（O7 材质区域、第 12 条图目标框）→ 工作框定稿（辉光外扩、像素对齐）
//   → 部件清单（原样或材质）→ 判空（F3）→ 层组装方式（F4）。
// 三个优化各是一步，可由对应的内部开关单独关闭供回归对照。
public sealed partial class SkiaRenderPipeline
{
    /// <summary>对象部件在计划中的处理方式。</summary>
    internal enum ObjectPartKind
    {
        /// <summary>按原样捕获该色彩部件。</summary>
        Original,
        /// <summary>执行材质，产物作为该部件的层。</summary>
        Material,
        /// <summary>本帧必然没有像素（F3）：不捕获、不执行，只占共享空层。</summary>
        KnownEmpty,
    }

    /// <summary>对象读取「下层」的来源。</summary>
    internal enum ObjectBelowSource
    {
        /// <summary>遗留调度：开始前对目标表面取一次快照，对象完成后以它为底合成并写回。</summary>
        DestinationSnapshot,
        /// <summary>前缀调度：不取快照，图与材质按属主从组合前缀惰性解析下层。</summary>
        CompositionPrefix,
    }

    /// <summary>无历史的部件层怎样叠成一张表面。</summary>
    internal enum ObjectLayerAssembly
    {
        /// <summary>逐层复制再叠加：每层新建一张表面。</summary>
        PerLayer,
        /// <summary>相邻的无历史层在同一张表面上依次绘制，共享空层跳过，只在有图链历史的层处断开（F4）。</summary>
        MergeAdjacentSourceLayers,
    }

    /// <summary>一个部件层的来历：哪个成员的哪个色彩部件、层序，以及原样捕获、材质还是已知为空。</summary>
    /// <param name="Member">整行、标记或元素族。</param>
    /// <param name="Part">色彩部件。</param>
    /// <param name="Order">层序：部件序在外、成员序在内，与逐成员直绘的叠放一致。</param>
    /// <param name="Kind">处理方式。</param>
    /// <param name="Sung">填充分两态时为已唱／未唱；不分态的部件为 null。</param>
    /// <param name="SlotHasMaterial">该成员的这个部件槽上有材质绑定（填充两态中任一态有即为真）。</param>
    /// <param name="Material">材质绑定；仅 <see cref="ObjectPartKind.Material" /> 有值。</param>
    /// <param name="MaterialProgram">材质程序；仅 <see cref="ObjectPartKind.Material" /> 有值。</param>
    internal sealed record ObjectPartStep(ImageEffectTarget Member, SceneColorPart Part, int Order, ObjectPartKind Kind, bool? Sung,
        bool SlotHasMaterial, ObjectMaterialBinding? Material = null, SceneMaterialProgram? MaterialProgram = null);

    /// <summary>一次图执行：绑定、程序与本次执行的目标框（第 12 条）。</summary>
    internal sealed record ObjectGraphStep(PreparedImageEffectBinding Binding, SceneImageGraphProgram Program, GraphRect TargetBounds);

    /// <summary>一个对象本帧的完整计划。</summary>
    /// <param name="Item">被绘制的行或文字标记。</param>
    /// <param name="Target">对象自身的目标（整行或标记）。</param>
    /// <param name="TokenTargets">有自己图或材质的元素族，按行内顺序。</param>
    /// <param name="ExcludedTokens">上述元素族的元素 ID；对象成员的部件不含它们。</param>
    /// <param name="Parts">部件层，按建立顺序：对象成员在前，各元素族按行内顺序在后。</param>
    /// <param name="Graphs">图的执行序列：各元素族的图在前（按行内顺序），整行或标记的图在后，每个目标内保留工程实例顺序。</param>
    /// <param name="Materials">本对象的材质绑定及其程序。</param>
    /// <param name="TargetRegion">材质执行所用的对象实际区域（O7）；null 时沿用程序框。</param>
    /// <param name="GraphTargets">各图绑定的目标框，与 <paramref name="Graphs" /> 中的相同。</param>
    /// <param name="WorkBounds">对象的工作框：部件捕获、下层与层组装的范围。</param>
    /// <param name="HasGlow">对象有外发光；没有时不列辉光部件。</param>
    /// <param name="Below">下层来源。</param>
    /// <param name="ClosedExport">为真时不写目标表面，把闭合的层导出给组合调度器；工作框不超出调度器给定的框。</param>
    /// <param name="Assembly">无历史层的组装方式。</param>
    /// <param name="ExecutionOwner">执行开始时的失败属主（建计划期间为核对归属推进过的属主在此复位）。</param>
    internal sealed record ObjectRenderPlan(SceneDrawItem Item, ImageEffectTarget Target, ImmutableArray<ImageEffectTarget.Token> TokenTargets,
        HashSet<string> ExcludedTokens, ImmutableArray<ObjectPartStep> Parts, ImmutableArray<ObjectGraphStep> Graphs,
        ImmutableArray<(ObjectMaterialBinding Binding, SceneMaterialProgram Program)> Materials, GraphRect? TargetRegion,
        IReadOnlyDictionary<PreparedImageEffectBinding, GraphRect> GraphTargets, GraphRect WorkBounds, bool HasGlow,
        ObjectBelowSource Below, bool ClosedExport, ObjectLayerAssembly Assembly, SceneEffectOwner ExecutionOwner);

    /// <summary>每建成一个对象计划后调用；仅测试用来核对计划内容。</summary>
    internal Action<ObjectRenderPlan>? ObjectPlanBuilt { get; set; }

    /// <summary>当前失败应归属的效果或材质；建计划与执行逐步推进，出错时由对象入口读取。</summary>
    private sealed class ObjectOwnerCell(SceneEffectOwner value)
    {
        internal SceneEffectOwner Value { get; set; } = value;
    }

    /// <summary>为带图或材质、或处在组合导出中的对象建立计划。</summary>
    /// <remarks>
    /// 只读取本帧准备结果、几何与图声明（Warp 映射会记入本帧投影工作账），不分配表面。
    /// 目标框只由本帧几何和声明决定，不看调度器；组合导出时工作框以调度器给定的框为上限。
    /// </remarks>
    private ObjectRenderPlan BuildObjectPlan(ImageSceneRun run, SceneDrawItem item, PreparedImageEffectBinding[] bindings,
        ObjectMaterialBinding[] materials, ImageObjectState? export, ObjectOwnerCell owner)
    {
        var plan = PlanObjectBase(run, item, bindings, materials, export, owner);
        if (TightObjectRegions) plan = TightenObjectRegions(run, plan, export);
        plan = FinishObjectWorkBounds(run, plan, export);
        plan = PlanObjectParts(run.Request, plan, owner);
        owner.Value = plan.ExecutionOwner;
        if (SkipKnownEmptyParts) plan = MarkKnownEmptyParts(run.Request, plan);
        if (SinglePassLayerAssembly) plan = plan with { Assembly = ObjectLayerAssembly.MergeAdjacentSourceLayers };
        ObjectPlanBuilt?.Invoke(plan);
        return plan;
    }

    /// <summary>基本计划：对象身份、程序与图的执行序列；区域一律取程序框，部件未列。</summary>
    private ObjectRenderPlan PlanObjectBase(ImageSceneRun run, SceneDrawItem item, PreparedImageEffectBinding[] bindings,
        ObjectMaterialBinding[] materials, ImageObjectState? export, ObjectOwnerCell owner)
    {
        var programs = bindings.Select(binding => { owner.Value = new(binding); return _imageEffects!.GetProgram(binding.Binding.Graph); }).ToArray();
        var materialPrograms = materials.Select(binding => { owner.Value = new(binding.Owner, "RenderMaterials", binding.TextTarget); return _imageEffects!.GetMaterial(binding.Material.Handle); }).ToArray();
        var target = item.Line is { } line
            ? (ImageEffectTarget)new ImageEffectTarget.Line(new(new(line.Frame.Line!.Id), new(line.BindingId!)))
            : new ImageEffectTarget.Mark(new(new(item.Mark!.NodeId), new(item.Mark.CueId), new(item.Mark.Geometry.BindingId!)));
        var tokenTargets = bindings.Select(binding => binding.Binding.Target).Concat(materials.Select(binding => binding.Target)).OfType<ImageEffectTarget.Token>()
            .Distinct().OrderBy(token => Array.FindIndex(item.Line!.Frame.Line!.Tokens.ToArray(), source => source.Id == token.Target.TokenId.Value)).ToImmutableArray();
        HashSet<string> excluded = tokenTargets.Select(token => token.Target.TokenId.Value).ToHashSet(StringComparer.Ordinal);
        // 实际执行顺序：各元素族的图在前（按行内顺序），整行或标记的图在后；每个目标内保留工程实例顺序。
        var sequence = tokenTargets.SelectMany(token => bindings.Where(binding => binding.Binding.Target == token))
            .Concat(bindings.Where(binding => binding.Binding.Target == target))
            .Select(binding => { var program = programs[Array.IndexOf(bindings, binding)]; return new ObjectGraphStep(binding, program, program.Bounds); })
            .ToImmutableArray();
        var materialSet = materials.Select((binding, index) => (binding, materialPrograms[index])).ToImmutableArray();
        return new(item, target, tokenTargets, excluded, [], sequence, materialSet, null,
            sequence.ToDictionary(step => step.Binding, step => step.TargetBounds), ObjectWorkBounds(sequence, materialSet, null, export),
            false, run.Backdrop is null ? ObjectBelowSource.DestinationSnapshot : ObjectBelowSource.CompositionPrefix,
            export is not null, ObjectLayerAssembly.PerLayer, owner.Value);
    }

    /// <summary>收紧前的工作框：各图程序框加声明外扩、各材质程序框的并集；组合导出取调度器给定的框。收紧见 <see cref="TightenObjectRegions" />。</summary>
    private static GraphRect ObjectWorkBounds(ImmutableArray<ObjectGraphStep> graphs,
        ImmutableArray<(ObjectMaterialBinding Binding, SceneMaterialProgram Program)> materials, GraphRect? region, ImageObjectState? export)
        => export?.Bounds ?? graphs.Select(step => step.TargetBounds.Expand(step.Program.Graph.Plan.Definition.MaximumExpandPx))
            .Concat(materials.Select(material => MaterialWorkBounds(region, material.Program))).Aggregate(Union);

    /// <summary>
    /// 收紧后各图与材质要求的工作范围：图目标框加声明外扩与取样距离，材质工作框同样并入取样距离。
    /// 节点可在声明的取样偏移内读取目标框之外的输入（下层、背景），工作框须包住这段距离，否则读到的是框外的透明或边缘像素。
    /// </summary>
    private static IEnumerable<GraphRect> SampledWorkBounds(ImmutableArray<ObjectGraphStep> graphs,
        ImmutableArray<(ObjectMaterialBinding Binding, SceneMaterialProgram Program)> materials, GraphRect? region)
        => graphs.Select(step => step.TargetBounds.Expand(step.Program.Graph.Plan.Definition.MaximumExpandPx + GraphSampleReachPx(step.Program.Graph)))
            .Concat(materials.Select(material => MaterialWorkBounds(region, material.Program, GraphSampleReachPx(material.Program.Graph))));

    /// <summary>
    /// 区域收紧：材质按本帧对象实际区域执行（O7），图按绑定目标的实际区域执行（第 12 条）。
    /// 两者都只由本帧几何决定，两条调度路径一致；工作框随之收紧。
    /// </summary>
    /// <remarks>
    /// 收紧前，输入在整框（独立对象为程序框加外扩，组合导出为调度器的框）内捕获，图与材质按声明的取样偏移读到目标框之外的下层或背景；
    /// 收紧后两条路径都把声明的取样距离并入工作框（<see cref="SampledWorkBounds" />），这类读取照旧成立。
    /// 工作框不超出收紧前的整框：整框之外本来就没有捕获的像素，越界读取在两种框下都落到同一条边上，像素与整框一致。
    /// 组合导出的对象没有图或材质时也要捕获全部部件，工作框另并入对象自身的范围；这个范围按调度器给定的框裁剪而不是按画布
    /// （组图可能把画布外的像素移进画面），并且只在逐元素几何能代表整个对象时使用，否则沿用调度器的框。
    /// 没有材质的对象若带网格映射（Warp），求范围要动用本帧共享的投影工作账，为不新增预算失败，同样沿用调度器的框。
    /// </remarks>
    private ObjectRenderPlan TightenObjectRegions(ImageSceneRun run, ObjectRenderPlan plan, ImageObjectState? export)
    {
        var request = run.Request; var composition = request.Project.Composition;
        bool hasMaterials = plan.Materials.Length > 0;
        bool wholeObject = export is not null && ObjectInkCoversWholeObject(request, plan.Item);
        // 对象的墨迹范围只算一次：材质区域按画布裁剪，组合导出的对象范围按调度器的框裁剪。
        var ink = hasMaterials || wholeObject ? ObjectInkRegion(request, plan.Item, run.Resources, projectWarp: hasMaterials) : null;
        var region = hasMaterials ? ClampObjectRegion(ink, new(0, 0, composition.CanvasWidth, composition.CanvasHeight)) : null;
        var targets = GraphTargetBounds(request, plan.Item, plan.Target, plan.Graphs.Select(step => (step.Binding, step.Program)), run.Resources);
        var graphs = plan.Graphs.Select(step => step with { TargetBounds = targets[step.Binding] }).ToImmutableArray();
        var whole = plan.WorkBounds;
        plan = plan with { TargetRegion = region, GraphTargets = targets, Graphs = graphs };
        if (export is null)
        {
            // 独立对象至少有一个图或材质；收紧后的目标框都在程序框内，相交结果不会为空。
            var sampled = Intersect(SampledWorkBounds(graphs, plan.Materials, region).Aggregate(Union), whole);
            return sampled.IsValid ? plan with { WorkBounds = sampled } : plan;
        }
        if (!wholeObject || ClampObjectRegion(ink, export.Bounds) is not { } own) return plan;
        var clipped = Intersect(SampledWorkBounds(graphs, plan.Materials, region).Aggregate(own, Union), export.Bounds);
        return clipped.IsValid ? plan with { WorkBounds = clipped } : plan;
    }

    /// <summary>工作框定稿：并入原样辉光的外扩；框一旦被收紧，按输出像素对齐（组合导出时仍不超出调度器的框）。</summary>
    private ObjectRenderPlan FinishObjectWorkBounds(ImageSceneRun run, ObjectRenderPlan plan, ImageObjectState? export)
    {
        var request = run.Request; var bounds = plan.WorkBounds;
        bool tightened = export is null
            ? plan.TargetRegion is not null || plan.Graphs.Any(step => step.TargetBounds != step.Program.Bounds)
            : bounds != export.Bounds;
        bool hasGlow = HasOriginalGlow(request, plan.Item);
        if (hasGlow) bounds = IncludeOriginalGlowBounds(request, plan.Item, bounds, run.Resources);
        if (tightened)
        {
            bounds = AlignToOutputPixels(request, bounds);
            if (export is not null)
            {
                var clipped = Intersect(bounds, export.Bounds);
                bounds = clipped.IsValid ? clipped : export.Bounds;
            }
        }
        return plan with { WorkBounds = bounds, HasGlow = hasGlow };
    }

    /// <summary>列出部件层：每个成员按色彩部件顺序，有材质绑定的槽执行材质，其余原样捕获。</summary>
    private static ObjectRenderPlan PlanObjectParts(RenderRequest request, ObjectRenderPlan plan, ObjectOwnerCell owner)
    {
        var prepared = request.PreparedFrame!;
        var parts = ImmutableArray.CreateBuilder<ObjectPartStep>();
        AddParts(plan.Target, 0);
        for (int i = 0; i < plan.TokenTargets.Length; i++) AddParts(plan.TokenTargets[i], i + 1);
        return plan with { Parts = parts.ToImmutable() };

        void AddParts(ImageEffectTarget member, int memberOrder)
        {
            for (int partOrder = 0; partOrder < ColorPartOrder.Length; partOrder++)
            {
                var part = ColorPartOrder[partOrder];
                if (!plan.HasGlow && part is SceneColorPart.BaseGlow or SceneColorPart.RubyGlow) continue;
                if (member is ImageEffectTarget.Mark && IsRuby(part)) continue;
                // An explicit Base appearance shadows the parent's slot even when it is Solid/Gradient/null.
                var captureTarget = member is ImageEffectTarget.Token token && !IsRuby(part)
                    && prepared.BaseAppearances.ContainsKey(new(token.Target.TokenId, token.Target.BindingId))
                    ? (SceneCaptureTarget)new SceneCaptureTarget.Base(new(token.Target.TokenId, token.Target.BindingId)) : CaptureTarget(member);
                var slot = plan.Materials.Where(material => material.Binding.CaptureTarget == captureTarget && (IsStrokePart(part)
                    ? material.Binding.Slot == PreparedMaterialSlot.Stroke : part is SceneColorPart.BaseFill or SceneColorPart.RubyFill
                        && material.Binding.Slot != PreparedMaterialSlot.Stroke)).ToArray();
                int order = partOrder * (plan.TokenTargets.Length + 1) + memberOrder;
                if (part is SceneColorPart.BaseOuterStroke or SceneColorPart.RubyOuterStroke)
                {
                    // 描边材质的区域只是最内层一圈；外圈只在这时单独原样捕获，其余情况由原样描边部件一并画出，层数不变（P3 设计 §6.3）。
                    if (slot.Length > 0) parts.Add(new(member, part, order, ObjectPartKind.Original, null, false));
                    continue;
                }
                if (slot.Length == 0) { parts.Add(new(member, part, order, ObjectPartKind.Original, null, false)); continue; }
                if (part is SceneColorPart.BaseFill or SceneColorPart.RubyFill) { AddSide(false); AddSide(true); }
                else AddMaterial(slot[0], null);

                void AddSide(bool sung)
                {
                    var side = sung ? PreparedMaterialSlot.SungFill : PreparedMaterialSlot.UnsungFill;
                    // 准备帧保证同一目标同一槽位至多一个绑定；没有绑定的一态原样捕获。
                    var match = slot.SingleOrDefault(material => material.Binding.Slot == side);
                    if (match.Binding is null) parts.Add(new(member, part, order, ObjectPartKind.Original, sung, true)); else AddMaterial(match, sung);
                }
                void AddMaterial((ObjectMaterialBinding Binding, SceneMaterialProgram Program) material, bool? sung)
                {
                    // 计划期错误的归属与逐部件执行时相同：推进到最近一个材质部件的属主。
                    owner.Value = new(material.Binding.Owner, "RenderMaterials", material.Binding.TextTarget);
                    parts.Add(new(member, part, order, ObjectPartKind.Material, sung, true, material.Binding, material.Program));
                }
            }
        }
    }

    /// <summary>
    /// 判空（F3）：本帧必然没有像素的部件改为已知为空，不捕获、不执行材质，只占共享空层；层数与层序不变。
    /// 注音部件（含注音材质）在无注音行上一律为空；描边与阴影只在所有可能参与绘制的样式都未启用时为空，描边材质仍执行。
    /// </summary>
    private static ObjectRenderPlan MarkKnownEmptyParts(RenderRequest request, ObjectRenderPlan plan)
    {
        var knownEmpty = ObjectKnownEmptyParts(request, plan.Item);
        return plan with { Parts = [.. plan.Parts.Select(step => knownEmpty.Contains(step.Part, step.SlotHasMaterial)
            ? step with { Kind = ObjectPartKind.KnownEmpty, Material = null, MaterialProgram = null } : step)] };
    }
}
