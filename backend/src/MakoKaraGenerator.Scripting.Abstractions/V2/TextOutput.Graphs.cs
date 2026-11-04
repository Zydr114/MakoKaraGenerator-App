using System.Runtime.ExceptionServices;

namespace MakoKaraGenerator.Scripting.V2;

public sealed partial class TextOutput
{
    private const string ImageGraphCapability = "image.graph";
    private readonly GraphBindingBudget _graphBudget = new();
    private ExceptionDispatchInfo? _graphFailure;
    internal CancellationToken GraphCancellation { private get; init; }

    /// <summary>整体替换真实字族、整行或提示标记的 ColorOnly 图绑定；不合并上一绑定的覆盖。</summary>
    public void BindGraph(TextTarget target, GraphBindingValue binding)
    {
        RequireOpen();
        try
        {
            ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(binding);
            if (target is not (TextTarget.Family or TextTarget.Line or TextTarget.Mark)) throw Unsupported();
            Require(target, ImageGraphCapability);
            if (target is TextTarget.Family && _tokenGeometry.TryGetValue(target, out var geometry) && !geometry.CanTransformIndependently)
                throw new EffectOutputException(geometry.DiagnosticCode ?? "UnsupportedGlyphPartition", "共享字形不能独立绑定图效果。");
            if (!_resources.Contains(binding.Graph)) throw new EffectOutputException("InvalidGraphResource", "图导出不属于本实例固定包修订。");
            binding.Parameters.ValidateResources(_resources);
            _graphBudget.Charge(binding, GraphCancellation);
            _writes.Add(new TextWrite.GraphBinding(target, binding));
        }
        catch (Exception error)
        {
            // Authors may catch a write error. The host must still reject the
            // entire transaction, including any successful writes made earlier.
            _graphFailure = ExceptionDispatchInfo.Capture(error); throw;
        }
    }
    private void RequireGraphTransaction()
    { _graphFailure?.Throw(); GraphCancellation.ThrowIfCancellationRequested(); }
}

public abstract partial record TextWrite
{
    /// <summary>完整图绑定写入；保留被替换值以逐次验证和累计计费。</summary>
    public sealed record GraphBinding : TextWrite
    {
        internal GraphBinding(TextTarget target, GraphBindingValue value) : base(target) { Value = value; }
        /// <summary>本次完整固定图、参数和两类覆盖。</summary>
        public GraphBindingValue Value { get; }
    }
}
