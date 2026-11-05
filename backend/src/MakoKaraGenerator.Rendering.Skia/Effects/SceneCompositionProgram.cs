using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

// Pipeline-owned fixed programs. No author graph, GraphHandle, Execute or JSON plan.
// The pipeline's owned device lifetime is leased by this owner and every returned image.
// For an externally borrowed device the existing caller-owned lifetime contract applies.
internal sealed partial class SceneCompositionProgram : IDisposable
{
    private readonly object _gate = new();
    private readonly ISkiaRenderDevice _device;
    private readonly SkiaRenderDeviceLifetime? _deviceLifetime;
    private readonly Action? _releaseDeviceLease;
    private readonly GraphExecutionBudget _budget;
    private readonly ImageSurfaceBudgetSession _surfaceBudget;
    private readonly CompositionPayloadPrograms _payloadPrograms;
    private readonly CompiledRuntimeShader _replace, _coverageUnion;
    private bool _disposed;

    internal SceneCompositionProgram(ISkiaRenderDevice device, GraphExecutionBudget budget,
        SkiaRenderDeviceLifetime? lifetime = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device); ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        _device = device; _deviceLifetime = lifetime; _budget = budget;
        CompositionPayloadPrograms? payload = null;
        CompiledRuntimeShader? replace = null, union = null;
        ImageSurfaceBudgetSession? surfaceBudget = null;
        Action? release = null;
        try
        {
            release = lifetime?.Acquire();
            surfaceBudget = new(budget);
            payload = new(cancellationToken);
            replace = CompiledRuntimeShader.Compile(new("host/scene-payload-apply.sksl",
                "uniform shader result; uniform shader below; uniform shader coverage; uniform float opacity; half4 main(float2 p) { return result.eval(p)*opacity + below.eval(p)*(1-coverage.eval(p).a*opacity); }",
                ShaderProgramDefinition.RasterProfile, [new("opacity", ShaderUniformKind.Float)],
                [new("result", ShaderSurfaceKind.Color), new("below", ShaderSurfaceKind.Color), new("coverage", ShaderSurfaceKind.Coverage)]), cancellationToken: cancellationToken);
            union = CompiledRuntimeShader.Compile(new("host/scene-coverage-max.sksl",
                "uniform shader a; uniform shader b; half4 main(float2 p) { return half4(0,0,0,max(a.eval(p).a,b.eval(p).a)); }",
                ShaderProgramDefinition.RasterProfile, [], [new("a", ShaderSurfaceKind.Coverage), new("b", ShaderSurfaceKind.Coverage)]), cancellationToken: cancellationToken);
            _surfaceBudget = surfaceBudget; _payloadPrograms = payload; _replace = replace; _coverageUnion = union;
            _releaseDeviceLease = release;
        }
        catch { union?.Dispose(); replace?.Dispose(); payload?.Dispose(); surfaceBudget?.Dispose(); release?.Invoke(); throw; }
    }

    // Same-parent wider capture inherits only that branch's persistent floor:
    // actual replacement masks plus its original child baseline. The last ordinary
    // output alpha is deliberately excluded; true child -> parent exports use full E.
    internal ImageCompositionPayload CapturePersistentFloor(IReadOnlyList<ImageGraphResult> history,
        ImageCompositionPayload? inherited, ImageSurfaceBudgetFrame resources, CancellationToken cancellationToken)
    {
        using var own = CaptureCompositionPayload(history, resources, cancellationToken: cancellationToken, includeFinalAlpha: false);
        return inherited is null ? ComposePayloads([own], resources, cancellationToken: cancellationToken)
            : InheritCompositionErasure(own, inherited, resources, cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _coverageUnion.Dispose(); _replace.Dispose(); _payloadPrograms.Dispose(); _surfaceBudget.Dispose(); }
            finally { _releaseDeviceLease?.Invoke(); }
        }
    }
}
