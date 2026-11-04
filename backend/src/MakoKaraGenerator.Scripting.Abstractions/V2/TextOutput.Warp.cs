namespace MakoKaraGenerator.Scripting.V2;

public sealed partial class TextOutput
{
    /// <summary>整体替换目标局部网格；null 明确关闭，未写继承。同层总是先网格后仿射。</summary>
    public void SetWarp(TextTarget target, GridWarp? value)
    {
        RequireOpen(); ArgumentNullException.ThrowIfNull(target);
        string capability = target switch
        {
            TextTarget.Family or TextTarget.Base => "token.warp",
            TextTarget.Cluster => "cluster.warp",
            TextTarget.Mark => "mark.warp",
            _ => throw Unsupported(),
        };
        Require(target, capability);
        if (!_tokenGeometry.TryGetValue(target, out var geometry))
            throw new EffectOutputException("InvalidWarpTarget", "网格需要当前帧的真实文字几何。");
        if (!geometry.CanTransformIndependently)
            throw new EffectOutputException(geometry.DiagnosticCode ?? "UnsupportedGlyphPartition", "共享字形不能独立设置网格。");
        // Every write remains in the transaction, including a later null. The
        // host charges the complete sequence before publishing the last values.
        _writes.Add(new TextWrite.Warp(target, value));
    }
}

public abstract partial record TextWrite
{
    /// <summary>整体替换文字局部网格；保留每次调用，不能通过覆盖绕过累计验证预算。</summary>
    public sealed record Warp : TextWrite
    {
        internal Warp(TextTarget target, GridWarp? value) : base(target) { Value = value; }
        /// <summary>本次完整规范网格，或明确关闭的 null。</summary>
        public GridWarp? Value { get; }
    }
}
