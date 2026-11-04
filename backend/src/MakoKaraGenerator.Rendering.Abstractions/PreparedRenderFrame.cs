using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>固定输入的中立会话工厂；实现与编译器由组合根注入。</summary>
public interface IEvaluatedFrameSourceFactory
{
    /// <summary>为固定快照创建独立会话；预览与导出不能共享可变实例。</summary>
    ValueTask<IEvaluatedFrameSource> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default);
}

/// <summary>独立串行求值会话；不能从绘制后端反向调用用户代码。</summary>
public interface IEvaluatedFrameSource : IAsyncDisposable
{
    /// <summary>完成同一时刻的求值，返回中立不可变计划；密度来自实际目标尺寸。</summary>
    ValueTask<PreparedRenderFrame> PrepareAsync(long timeUs, OutputMode outputMode, RenderTarget target, CancellationToken cancellationToken = default);
}

/// <summary>准备帧身份或几何非法；不能退回无效果原画。</summary>
public sealed class PreparedFrameException : Exception
{
    /// <summary>创建具名错误。</summary>
    public PreparedFrameException(string code, string message) : base(message) { Code = code; }
    /// <summary>稳定诊断代码。</summary>
    public string Code { get; }
    /// <summary>涉及单个真实装载时的身份，供宿主附加声明该装载寿命的实例归属。</summary>
    public BindingId? BindingId { get; init; }
    /// <summary>可归属到一个文字写入的具体目标；整体帧或源数据失败时为空。</summary>
    public MakoKaraGenerator.Scripting.V2.TextTarget? TextTarget { get; init; }
}

/// <summary>源快照／时刻／密度／输出模式和已验证视觉变换的一次不可变闭包。</summary>
public sealed partial class PreparedRenderFrame
{
    private readonly ImmutableDictionary<TokenFamilyTarget, LyricStyle> _styles;
    private readonly ImmutableDictionary<TextMarkTarget, LyricStyle> _markStyles;
    /// <summary>构造仅含视觉变换的计划。</summary>
    public PreparedRenderFrame(ProjectSnapshot snapshot, long timeUs, OutputMode outputMode, RenderTarget target,
        SceneFrame scene, ImmutableArray<TokenFamilyTransform> transforms)
        : this(snapshot, timeUs, outputMode, target, scene, transforms, []) { }
    /// <summary>构造已完成的计划；有变化的目标必须真实存在且可独立绘制。</summary>
    public PreparedRenderFrame(ProjectSnapshot snapshot, long timeUs, OutputMode outputMode, RenderTarget target,
        SceneFrame scene, ImmutableArray<TokenFamilyTransform> transforms, ImmutableArray<TokenFamilyStyle> styles)
        : this(snapshot, timeUs, outputMode, target, scene, transforms, styles, []) { }
    /// <summary>构造带已处理阶段诊断的计划；失败诊断不能进入成功帧。</summary>
    public PreparedRenderFrame(ProjectSnapshot snapshot, long timeUs, OutputMode outputMode, RenderTarget target,
        SceneFrame scene, ImmutableArray<TokenFamilyTransform> transforms, ImmutableArray<TokenFamilyStyle> styles,
        ImmutableArray<RuntimeDiagnostic> diagnostics, ImmutableArray<TextMarkTransform> markTransforms = default,
        ImmutableArray<TextMarkStyle> markStyles = default, ImmutableArray<LineTransform> lineTransforms = default)
        : this(snapshot, timeUs, outputMode, target, scene, transforms, styles, diagnostics, markTransforms, markStyles, lineTransforms, []) { }
    /// <summary>构造包含真实组层次输出的完整计划，保留原有构造入口。</summary>
    public PreparedRenderFrame(ProjectSnapshot snapshot, long timeUs, OutputMode outputMode, RenderTarget target,
        SceneFrame scene, ImmutableArray<TokenFamilyTransform> transforms, ImmutableArray<TokenFamilyStyle> styles,
        ImmutableArray<RuntimeDiagnostic> diagnostics, ImmutableArray<TextMarkTransform> markTransforms,
        ImmutableArray<TextMarkStyle> markStyles, ImmutableArray<LineTransform> lineTransforms, ImmutableArray<GroupTransform> groupTransforms)
        : this(snapshot, timeUs, outputMode, target, scene, transforms, styles, diagnostics, markTransforms, markStyles, lineTransforms, groupTransforms, []) { }
    /// <summary>构造包含逐区域包材质归属的完整计划；几何种子不替代最终外观。</summary>
    public PreparedRenderFrame(ProjectSnapshot snapshot, long timeUs, OutputMode outputMode, RenderTarget target,
        SceneFrame scene, ImmutableArray<TokenFamilyTransform> transforms, ImmutableArray<TokenFamilyStyle> styles,
        ImmutableArray<RuntimeDiagnostic> diagnostics, ImmutableArray<TextMarkTransform> markTransforms,
        ImmutableArray<TextMarkStyle> markStyles, ImmutableArray<LineTransform> lineTransforms, ImmutableArray<GroupTransform> groupTransforms,
        ImmutableArray<PreparedMaterialBinding> materials, PreparedSourceMaterials? sourceMaterials = null)
        : this(snapshot, timeUs, outputMode, target, scene, transforms, styles, RuntimeDiagnosticSnapshot.FromArray(diagnostics),
            markTransforms, markStyles, lineTransforms, groupTransforms, materials, sourceMaterials) { }
    /// <summary>宿主共享已验证的稳定诊断快照，保留全部发生而不物化完整数组。</summary>
    public static PreparedRenderFrame CreateWithDiagnosticSnapshot(ProjectSnapshot snapshot, long timeUs, OutputMode outputMode, RenderTarget target,
        SceneFrame scene, ImmutableArray<TokenFamilyTransform> transforms, ImmutableArray<TokenFamilyStyle> styles,
        RuntimeDiagnosticSnapshot diagnostics, ImmutableArray<TextMarkTransform> markTransforms,
        ImmutableArray<TextMarkStyle> markStyles, ImmutableArray<LineTransform> lineTransforms, ImmutableArray<GroupTransform> groupTransforms,
        ImmutableArray<PreparedMaterialBinding> materials, PreparedSourceMaterials? sourceMaterials = null)
        => new(snapshot, timeUs, outputMode, target, scene, transforms, styles, diagnostics,
            markTransforms, markStyles, lineTransforms, groupTransforms, materials, sourceMaterials);
    private PreparedRenderFrame(ProjectSnapshot snapshot, long timeUs, OutputMode outputMode, RenderTarget target,
        SceneFrame scene, ImmutableArray<TokenFamilyTransform> transforms, ImmutableArray<TokenFamilyStyle> styles,
        RuntimeDiagnosticSnapshot diagnostics, ImmutableArray<TextMarkTransform> markTransforms,
        ImmutableArray<TextMarkStyle> markStyles, ImmutableArray<LineTransform> lineTransforms, ImmutableArray<GroupTransform> groupTransforms,
        ImmutableArray<PreparedMaterialBinding> materials, PreparedSourceMaterials? sourceMaterials)
    {
        Materials = materials; SourceMaterials = sourceMaterials;
        sourceMaterials?.ValidateSnapshot(snapshot);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (diagnostics.HasError) throw new ArgumentException("成功帧只能包含有效的非失败诊断。", nameof(diagnostics));
        DiagnosticSnapshot = diagnostics;
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(scene);
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        if (!Enum.IsDefined(outputMode) || target.PixelWidth <= 0 || target.PixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputMode));
        if (scene.TimeUs != timeUs) throw new PreparedFrameException("PreparedTimeMismatch", "准备帧与基础几何时刻不一致。");
        if (transforms.IsDefault) throw new ArgumentException("变换集合不能是 default。", nameof(transforms));
        LineTransforms = lineTransforms.IsDefault ? [] : lineTransforms;
        GroupTransforms = groupTransforms.IsDefault ? [] : groupTransforms;
        HierarchyScene = SceneAffineProjection.Apply(scene, LineTransforms, GroupTransforms);
        var geometries = HierarchyScene.TokenGeometries.ToDictionary(static item => item.WithRuby);
        HashSet<TokenFamilyTarget> seen = [];
        foreach (var transform in transforms)
        {
            if (transform is null || transform.Target is null || !seen.Add(transform.Target) || !geometries.TryGetValue(transform.Target, out var geometry))
                throw new PreparedFrameException("InvalidPreparedTarget", "准备帧目标重复或不存在。");
            if (!geometry.BaseGeometry.CanTransformIndependently)
                throw new PreparedFrameException("UnsupportedGlyphPartition", "准备帧不能变形共享字簇。");
            if (!transform.LocalMatrix.TryInverse(out _)) throw new PreparedFrameException("InvalidTransform", "准备帧矩阵不可逆。");
            // FinalGeometry 必须保持有限，先完成验证再创建任何像素。
            var world = geometry.BaseGeometry.BaseWorldMatrix.Compose(transform.LocalMatrix);
            _ = (geometry.FamilyGeometry with { BaseWorldMatrix = world }).WorldInkCorners;
        }
        if (styles.IsDefault) throw new ArgumentException("外观集合不能是 default。", nameof(styles));
        var sourceStyles = StaticStyleProjection.LoadedSourceStyles(snapshot, HierarchyScene);
        var resolved = ImmutableDictionary.CreateBuilder<TokenFamilyTarget, LyricStyle>();
        var effective = ImmutableArray.CreateBuilder<TokenFamilyStyle>();
        var appearances = ImmutableDictionary.CreateBuilder<TokenFamilyTarget, StyleView>();
        seen.Clear();
        foreach (var style in styles)
        {
            if (style is null || style.Target is null || style.Update is null || !seen.Add(style.Target) || !geometries.TryGetValue(style.Target, out var geometry)
                || !sourceStyles.TryGetValue(style.Target, out var basis))
                throw new PreparedFrameException("InvalidPreparedStyleTarget", "准备帧外观目标重复或不存在。");
            LyricStyle result;
            try { result = StaticStyleProjection.ApplyDrawingStyle(basis, style.Update, sourceMaterials); }
            catch (EffectOutputException exception) { throw new PreparedFrameException(exception.Code, exception.Message); }
            if (ReferenceEquals(result, basis) && !ContainsPackageMaterial(style.Update)) continue;
            if (!geometry.BaseGeometry.CanTransformIndependently)
                throw new PreparedFrameException("UnsupportedGlyphPartition", "准备帧不能独立改变共享字簇外观。");
            resolved.Add(style.Target, result); effective.Add(style);
            appearances.Add(style.Target, style.Update.Apply(StaticStyleProjection.View(basis, sourceMaterials)));
        }
        _styles = resolved.ToImmutable(); _appearances = appearances.ToImmutable(); TokenStyles = effective.ToImmutable();
        Snapshot = snapshot; TimeUs = timeUs; OutputMode = outputMode; Target = target;
        Scene = scene; TokenTransforms = transforms;
        var byTarget = transforms.ToDictionary(static item => item.Target, static item => item.LocalMatrix);
        FinalGeometry = [.. HierarchyScene.TokenGeometries.Select(item =>
        {
            if (!byTarget.TryGetValue(item.WithRuby, out var local)) return item;
            GeometrySample Final(GeometrySample basis) => basis with { BaseWorldMatrix = basis.BaseWorldMatrix.Compose(local) };
            return item with { BaseGeometry = Final(item.BaseGeometry), FamilyGeometry = Final(item.FamilyGeometry),
                Ruby = [.. item.Ruby.Select(ruby => ruby with { Geometry = Final(ruby.Geometry) })],
                Clusters = [.. item.Clusters.Select(cluster => cluster.WithGeometry(Final(cluster.Geometry)))] };
        })];
        FinalLineGeometry = HierarchyScene.LineGeometries;
        Playback = PlaybackProjection.Create(snapshot, HierarchyScene, FinalGeometry);
        (MarkTransforms, MarkStyles, FinalMarkGeometry, _markStyles, _markAppearances) = PrepareMarks(snapshot, HierarchyScene, markTransforms, markStyles, sourceMaterials);
        ValidateMaterials();
        FinalGroupGeometry = GroupInkProjection.Create(HierarchyScene, transforms.IsEmpty ? default : FinalGeometry, FinalMarkGeometry);
    }
    /// <summary>精确固定快照；引用身份和效果输入指纹均属于渲染门禁。</summary>
    public ProjectSnapshot Snapshot { get; }
    /// <summary>目标时刻。</summary>
    public long TimeUs { get; }
    /// <summary>背景输出语义。</summary>
    public OutputMode OutputMode { get; }
    /// <summary>本次求值的像素密度目标。</summary>
    public RenderTarget Target { get; }
    /// <summary>共享后端的基础几何，不含第二套时间调度。</summary>
    public SceneFrame Scene { get; }
    /// <summary>组与整行层次仿射后的绘制场景；元素族／标记独立输出由后端再各应用一次。</summary>
    public SceneFrame HierarchyScene { get; }
    /// <summary>已验证并按实例组合的整行局部仿射。</summary>
    public ImmutableArray<LineTransform> LineTransforms { get; }
    /// <summary>已验证并按实例组合的真实组局部仿射。</summary>
    public ImmutableArray<GroupTransform> GroupTransforms { get; }
    /// <summary>整行最终矩阵的固定几何，包括无逐字元素的行。</summary>
    public ImmutableArray<LineGeometrySample> FinalLineGeometry { get; private init; }
    /// <summary>真实组最终参考矩阵和全部最终文字／注音／标记墨迹，包含独立族输出。</summary>
    public ImmutableArray<GroupGeometrySample> FinalGroupGeometry { get; private init; }
    /// <summary>按实例顺序组合、已验证的元素族局部变换。</summary>
    public ImmutableArray<TokenFamilyTransform> TokenTransforms { get; }
    /// <summary>按实例顺序组合的有效元素族外观，无变化输出已移除。</summary>
    public ImmutableArray<TokenFamilyStyle> TokenStyles { get; }
    /// <summary>已处理阶段和本次外观求值的只读诊断。</summary>
    public ImmutableArray<RuntimeDiagnostic> Diagnostics => DiagnosticSnapshot.ToImmutableArray();
    /// <summary>完整稳定的发生日志；正式消费者按代次与顺序增量读取。</summary>
    public RuntimeDiagnosticSnapshot DiagnosticSnapshot { get; }
    /// <summary>取得已验证的最终帧外观；不返回可写的源数据。</summary>
    public LyricStyle GetTokenStyle(TokenFamilyTarget target) => HasMaterial(new ImageEffectTarget.Token(target))
        ? throw new PreparedFrameException("MaterialAppearanceRequiresCoordinator", "包材质最终颜色须由协调器处理，请读取真实Appearance。")
        : _styles.TryGetValue(target, out var style) ? style
        : throw new PreparedFrameException("MissingPreparedStyle", "该目标没有有效的准备外观。");
    /// <summary>与实际绘制相同的最终元素及注音世界几何。</summary>
    public ImmutableArray<TokenGeometrySample> FinalGeometry { get; private init; }
    /// <summary>按最终文字矩阵求出的完整当前播放头；消费时显式选择纵向锚点。</summary>
    public PlaybackFrame Playback { get; private init; }
    /// <summary>当前真实标记的合并局部仿射。</summary>
    public ImmutableArray<TextMarkTransform> MarkTransforms { get; }
    /// <summary>当前真实标记的有效两态外观。</summary>
    public ImmutableArray<TextMarkStyle> MarkStyles { get; }
    /// <summary>与生产标记绘制矩阵一致的最终固定字形几何。</summary>
    public ImmutableArray<TextMarkGeometrySample> FinalMarkGeometry { get; private init; }
    /// <summary>读取已验证的最终标记外观；无有效修改不伪造写入。</summary>
    public LyricStyle GetMarkStyle(TextMarkTarget target) => HasMaterial(new ImageEffectTarget.Mark(target))
        ? throw new PreparedFrameException("MaterialAppearanceRequiresCoordinator", "包材质标记不能将几何种子视为最终颜色。")
        : _markStyles.TryGetValue(target, out var style) ? style
        : throw new PreparedFrameException("MissingPreparedMarkStyle", "该标记没有有效准备外观。");
    /// <summary>消费前门禁；缓存命中也必须先检查。</summary>
    public static void ValidateRequest(RenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SourceMaterialCatalog.Collect(request.Project.LyricStyle, request.Project.Lyrics, request.Project.LyricPresentation, request.Project.PresetApplications).IsEmpty
            && request.PreparedFrame?.SourceMaterials is null)
            throw new PreparedFrameException("SourceMaterialResourcesRequired", "源包材质必须先准备真实固定资源与来源，不能绘制几何种子。");
        if (request.PreparedFrame is not { } prepared)
        {
            if (request.Project.EffectInputsFingerprint is not null)
                throw new PreparedFrameException("MissingPreparedFrame", "工程启用效果，必须先完成准备帧。");
            return;
        }
        if (!ReferenceEquals(prepared.Snapshot, request.Project)
            || prepared.TimeUs != request.TimeUs || prepared.Target != request.ResolveTarget()
            || prepared.OutputMode != (request.TransparentBackground ? OutputMode.Transparent : OutputMode.Opaque)
            || request.LayoutLineId is not null)
            throw new PreparedFrameException("PreparedInputsMismatch", "准备帧不属于此次快照、时刻、密度或输出模式。");
    }
}
