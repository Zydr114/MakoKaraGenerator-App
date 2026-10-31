using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Execution;

/// <summary>宿主专用的最终端点解析；每次独立重放，不提供脚本递归完整帧入口。</summary>
public sealed class SemanticAnchorQuery
{
    private readonly ProjectSnapshot _snapshot;
    private readonly ContentCatalog _content;
    private readonly TimelineFrameSourceFactory _factory;
    private readonly AudioFeatures? _audio;
    /// <summary>冻结与主会话相同的输入；后端工厂每次须产生独立实例。</summary>
    public SemanticAnchorQuery(ProjectSnapshot snapshot, ImmutableArray<CompiledFrameEffect> effects, string inputsFingerprint,
        Func<ILineGeometryProvider> geometryFactory, AudioFeatures? audio = null, PreparedSourceMaterials? sourceMaterials = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!StringComparer.Ordinal.Equals(snapshot.EffectInputsFingerprint, inputsFingerprint))
            throw new PreparedFrameException("EffectInputsMismatch", "端点查询必须冻结相同效果输入。");
        _snapshot = snapshot; _content = ContentProjection.Create(snapshot); _audio = audio;
        _factory = new(effects, inputsFingerprint, geometryFactory, ReplayReason.AnchorQuery, audio, sourceMaterials: sourceMaterials);
    }
    /// <summary>独立重放共享语义及文字求值，不绘制背景／粒子／图像，也不写主会话历史。</summary>
    public async ValueTask<CursorEndpoint> ResolveAsync(CursorIdentity identity, CursorEndpointKind kind, CursorAnchorMode anchorMode,
        OutputMode outputMode, RenderTarget target, CancellationToken cancellationToken = default)
    {
        var frame = await PrepareEndpointAsync(identity, kind, anchorMode, outputMode, target, cancellationToken).ConfigureAwait(false);
        return new(identity, kind, frame.TimeUs, anchorMode, Cursor(frame, identity, anchorMode), frame.Diagnostics);
    }
    internal async ValueTask<PlaybackCursor?> ResolveCursorAsync(CursorIdentity identity, CursorEndpointKind kind, CursorAnchorMode anchorMode,
        OutputMode outputMode, RenderTarget target, CancellationToken cancellationToken)
        => Cursor(await PrepareEndpointAsync(identity, kind, anchorMode, outputMode, target, cancellationToken).ConfigureAwait(false), identity, anchorMode);
    private static PlaybackCursor? Cursor(PreparedRenderFrame frame, CursorIdentity identity, CursorAnchorMode anchorMode)
        => frame.Playback.GetCursors(anchorMode).SingleOrDefault(item => item.Identity == identity);
    private async ValueTask<PreparedRenderFrame> PrepareEndpointAsync(CursorIdentity identity, CursorEndpointKind kind, CursorAnchorMode anchorMode,
        OutputMode outputMode, RenderTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(target);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(anchorMode)) throw new ArgumentOutOfRangeException(nameof(anchorMode));
        ValidateSource(identity); cancellationToken.ThrowIfCancellationRequested();
        long timeUs = kind == CursorEndpointKind.NextStart ? identity.TimeRange.StartUs : identity.TimeRange.EndUs - 1;
        await using var session = await _factory.CreateAsync(_snapshot, cancellationToken).ConfigureAwait(false);
        return await session.PrepareAsync(timeUs, outputMode, target, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>按真实输出范围返回全部合法后继及独立最终端点；不从源订阅或第一条边猜唯一光标。</summary>
    public async ValueTask<ImmutableArray<CursorTransitionEndpoints>> ResolveSuccessorsAsync(CursorIdentity from, EffectBinding binding,
        CursorAnchorMode anchorMode, OutputMode outputMode, RenderTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(from); ArgumentNullException.ThrowIfNull(target); ValidateSource(from); cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(anchorMode) || !Enum.IsDefined(outputMode) || target.PixelWidth <= 0 || target.PixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(anchorMode));
        var timeline = TimelineProjection.Create(_snapshot, ReplayReason.AnchorQuery, _audio, cancellationToken);
        var edges = timeline.GetCursorSuccessors(from, binding);
        if (edges.IsEmpty) return [];
        var previous = await ResolveAsync(from, CursorEndpointKind.PreviousEndLimit, anchorMode, outputMode, target, cancellationToken).ConfigureAwait(false);
        List<CursorTransitionEndpoints> results = [];
        foreach (var edge in edges)
        {
            var next = await ResolveAsync(edge.To, CursorEndpointKind.NextStart, anchorMode, outputMode, target, cancellationToken).ConfigureAwait(false);
            results.Add(new(edge, previous, next));
        }
        return [.. results];
    }
    private void ValidateSource(CursorIdentity identity)
    {
        var line = _content.GetLine(identity.LineId);
        bool valid = line.BranchId == identity.BranchId;
        if (identity.TokenId is { } tokenId)
        {
            var token = _content.GetToken(tokenId);
            valid &= token.LineId == line.Id && token.BranchId == line.BranchId && token.TimeRange == identity.TimeRange
                && identity.OccurrenceId == StableIdentities.Occurrence(line.BranchId, line.Id, tokenId, identity.TimeRange);
        }
        else valid &= line.TimeRange == identity.TimeRange && !line.TokenIds.Any(id => _content.GetToken(id).TimeRange is not null);
        if (!valid) throw new EffectOutputException("InvalidCursorSource", "端点查询身份不属于固定源演唱区间。");
    }
}

/// <summary>宿主最终后继端点；不可用端点保留，作者错误／取消直接传播。</summary>
public sealed record CursorTransitionEndpoints(CursorTransitionEvent Transition, CursorEndpoint PreviousEnd, CursorEndpoint NextStart)
{
    /// <summary>两端都有实际最终几何；不表示多个后继已有唯一桥接选择。</summary>
    public bool IsAvailable => PreviousEnd.IsAvailable && NextStart.IsAvailable;
}
