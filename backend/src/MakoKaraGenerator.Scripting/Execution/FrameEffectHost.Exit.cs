using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private PresentationIndex? _presentationIndex;
    private FrozenExitGeometry? _frozenExits;
    private sealed record ExitLifetime(long Hide, long Duration, CompiledFrameEffect Owner);
    private ImmutableDictionary<BindingId, ExitLifetime> _exitDurations = ImmutableDictionary<BindingId, ExitLifetime>.Empty;
    private long _maximumExitDurationUs;

    private void InitializeExitReplicas(TimelineProjection? timeline, CancellationToken token)
    {
        var declarations = _definitions.Where(static definition => definition.IsV2
            && definition.PackageDefinition!.Contents.LineExitDurationUs > 0).ToArray();
        if (declarations.Length == 0) return;
        try
        {
            if (timeline is null) throw new PreparedFrameException("PureSourceIndexRequired", "退场副本需要固定装载索引。");
            _presentationIndex = new(timeline, token);
            _frozenExits = new(_snapshot, _geometry);
            var scopes = declarations.Select(definition => (Scope: ResolvedSourceSubscription.Create(timeline,
                definition.Binding, new SourceSubscription.BoundSources()), Duration: definition.PackageDefinition!.Contents.LineExitDurationUs,
                Owner: definition)).ToArray();
            _exitDurations = timeline.Events.OfType<BindingLeft>().Select(exit =>
            {
                token.ThrowIfCancellationRequested();
                var owners = scopes.Where(scope => scope.Scope.ContainsOutputBinding(exit.Binding)).OrderByDescending(static scope => scope.Duration).ToArray();
                return new KeyValuePair<BindingId, ExitLifetime?>(exit.Binding.BindingId, owners.Length == 0 ? null
                    : new(exit.TimeUs, owners[0].Duration, owners[0].Owner));
            })
                .Where(static pair => pair.Value is not null).ToImmutableDictionary(static pair => pair.Key, static pair => pair.Value!);
            _maximumExitDurationUs = _exitDurations.Values.Select(static value => value.Duration).DefaultIfEmpty().Max();
        }
        catch (Exception error) when (error is not OperationCanceledException and not EffectFailureException)
        {
            // Index construction covers the whole fixed source. The first active
            // lifetime declaration owns initialization; per-load failures below
            // instead belong to that load's longest eligible declaration.
            throw Failure(declarations[0], "InitializeExitReplica", 0, error, Diagnostics.Snapshot());
        }
    }

    private SceneFrame ExtendExitReplicas(SceneFrame scene, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_presentationIndex is null || _maximumExitDurationUs == 0) return scene;
        // 每个装载只由实际作用于它的声明延寿；其他分支的长退场不能延长本装载。
        try
        {
            var exits = _presentationIndex.Query(scene.TimeUs, _maximumExitDurationUs,
                line => _exitDurations.TryGetValue(line.BindingId, out var value) && scene.TimeUs - value.Hide < value.Duration, token);
            return _frozenExits!.Extend(scene, exits, token);
        }
        catch (PreparedFrameException error)
        {
            var owner = error.BindingId is { } binding && _exitDurations.TryGetValue(binding, out var lifetime) ? lifetime.Owner
                : _definitions.First(definition => definition.IsV2 && definition.PackageDefinition!.Contents.LineExitDurationUs > 0);
            throw Failure(owner, "PrepareExitReplica", scene.TimeUs, error, Diagnostics.Snapshot());
        }
    }
}
