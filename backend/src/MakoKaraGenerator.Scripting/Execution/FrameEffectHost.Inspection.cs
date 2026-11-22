using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    /// <summary>描述全部效果实例的来源和绑定的源行，供只读检查投影使用。</summary>
    internal ImmutableArray<RenderEffectInstanceInfo> DescribeEffects()
    {
        var builder = ImmutableArray.CreateBuilder<RenderEffectInstanceInfo>(_definitions.Length);
        for (int index = 0; index < _definitions.Length; index++)
        {
            var definition = _definitions[index];
            var active = _instances.FirstOrDefault(item => item.Definition.Id == definition.Id);
            var scope = active?.Subscription ?? (index < _subscriptions.Length ? _subscriptions[index] : null);
            ImmutableArray<string> lines = scope is not null ? [.. scope.OutputScope.LineIds.Select(static id => id.Value)]
                : definition.Binding is EffectBinding.Lines bound ? [.. bound.Ids.Select(static id => id.Value)] : [];
            var package = definition.PackageIdentity;
            builder.Add(new(definition.Id.Value, package?.PackageId, package?.Revision, package?.ExportId, 2,
                BindingKind(definition.Binding), lines, active?.TelemetryIdentity));
        }
        return builder.MoveToImmutable();
    }

    private static string BindingKind(EffectBinding binding) => binding switch
    {
        EffectBinding.Project => "project",
        EffectBinding.Branch => "branch",
        EffectBinding.Lines => "lines",
        EffectBinding.Tokens => "tokens",
        EffectBinding.Clips => "clips",
        EffectBinding.Node => "node",
        _ => "unknown",
    };
}
