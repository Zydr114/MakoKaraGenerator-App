using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>帧外固定的真实资源／native 图关联；借用 Graph，调用者拥有其生命周期。</summary>
public sealed class SceneImageGraphProgram
{
    private readonly SceneCaptureRecipe? _sharedRecipe;
    private readonly ImmutableDictionary<string, SceneCaptureRecipe>? _inputRecipes;
    internal bool HasPerInputRecipes => _inputRecipes is not null;
    /// <summary>指定真实资源和显式捕获配方／Canvas 框；不解析 JSON，不编译源码。</summary>
    public SceneImageGraphProgram(GraphHandle handle, CompiledImageGraph graph, SceneCaptureRecipe recipe, GraphRect bounds)
    {
        ArgumentNullException.ThrowIfNull(handle); ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(recipe);
        if (!bounds.IsValid || !Enum.IsDefined(recipe.Region)) throw new ArgumentException("捕获框和区域必须明确有效。");
        Handle = handle; Graph = graph; _sharedRecipe = recipe; Bounds = bounds;
    }
    /// <summary>使用包中逐输入配方；owned overlay／材质区域适配未实施时明确拒绝。</summary>
    public static SceneImageGraphProgram FromPackage(GraphHandle handle, CompiledImageGraph graph, EffectPackageImageGraph definition, GraphRect bounds)
        => new(definition, handle, graph, bounds);

    private SceneImageGraphProgram(EffectPackageImageGraph definition, GraphHandle handle, CompiledImageGraph graph, GraphRect bounds)
    {
        ArgumentNullException.ThrowIfNull(handle); ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(definition);
        if (!bounds.IsValid || !ReferenceEquals(graph.Plan, definition.Plan)) throw new ArgumentException("图计划及显式捕获框不匹配。");
        var recipes = ImmutableDictionary.CreateBuilder<string, SceneCaptureRecipe>(StringComparer.Ordinal);
        foreach (var pair in definition.Captures)
        {
            if (pair.Value.IncludeOwnedOverlay) throw new EffectPackageException("PackageOwnedOverlayCapturePending", "inputs." + pair.Key + ".capture.includeOwnedOverlay", "实际 owned overlay 捕获尚未接入。");
            var region = pair.Value.Region switch
            {
                EffectCaptureRegion.Fill => SceneCaptureRegion.Fill, EffectCaptureRegion.Stroke => SceneCaptureRegion.Stroke,
                EffectCaptureRegion.FillAndStroke => SceneCaptureRegion.FillAndStroke,
                _ => throw new EffectPackageException("PackageMaterialRegionBindingRequired", "inputs." + pair.Key + ".capture.region", "材质区域必须由实际材质绑定解析。"),
            };
            recipes.Add(pair.Key, new(pair.Value.IncludeRuby, pair.Value.IncludeStroke, pair.Value.IncludeShadow, pair.Value.IncludeDescendantGroups, region));
        }
        Handle = handle; Graph = graph; Bounds = bounds; _inputRecipes = recipes.ToImmutable();
    }
    /// <summary>固定包修订和导出。</summary>
    public GraphHandle Handle { get; }
    /// <summary>借用的固定编译图。</summary>
    public CompiledImageGraph Graph { get; }
    /// <summary>手工构造时明确共用的配方；逐输入程序必须使用 GetInputRecipe。</summary>
    public SceneCaptureRecipe Recipe => _sharedRecipe ?? throw new InvalidOperationException("此程序的配方按输入分别声明。");
    /// <summary>读取输入自己的完整配方，不回退到另一个输入。</summary>
    public SceneCaptureRecipe GetInputRecipe(string inputId) => _sharedRecipe ?? (_inputRecipes!.TryGetValue(inputId, out var recipe)
        ? recipe : throw new EffectPackageException("PackageGraphCaptureMissing", "inputs." + inputId + ".capture", "目标输入缺少自己的捕获配方。"));
    /// <summary>显式 Canvas 逻辑框，不在帧内猜默认整画布。</summary>
    public GraphRect Bounds { get; }
}

/// <summary>实例图产物及其自建输入的联合生命周期，不释放外部借用输入。</summary>
public sealed class SceneImageEffectResult : IDisposable
{
    private readonly ImageGraphResult _result;
    private readonly ImmutableArray<SkiaGraphSurface> _captures;
    private bool _disposed;
    internal SceneImageEffectResult(PreparedImageEffectBinding binding, ImageGraphResult result, ImmutableArray<SkiaGraphSurface> captures)
    { Binding = binding; _result = result; _captures = captures; }
    /// <summary>产物真实实例与编译代次。</summary>
    public PreparedImageEffectBinding Binding { get; }
    /// <summary>拥有的图结果；旁路时也保留真实 TargetColor 输入直到释放。</summary>
    public ImageGraphResult GraphResult { get { ObjectDisposedException.ThrowIf(_disposed, this); return _result; } }
    /// <summary>先释放图产物再释放自建捕获；幂等。</summary>
    public void Dispose()
    { if (_disposed) return; _disposed = true; _result.Dispose(); foreach (var capture in _captures) capture.Dispose(); }
}
