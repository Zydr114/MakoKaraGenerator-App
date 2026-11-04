using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    // A distinct host activity: no author object, DLL, Declare or legacy callbacks.
    private sealed record HostDirectGraphRuntime(EffectGraphDefinition Export, GraphBindingValue Binding);

    private ActiveEffect LoadHostDirectGraph(CompiledFrameEffect definition)
    {
        if (!definition.IsV2 || definition.Artifact is not null || definition.EntryType is not null
            || definition.PackageDeclaration is not EffectGraphDefinition graph)
            throw new EffectOutputException("InvalidDirectGraphActivity", "宿主直接图须为无 DLL 的固定 v2 图导出。");
        var scope = ResolvedSourceSubscription.Create(_timeline ?? throw new EffectOutputException("PureSourceIndexRequired", "纯图需要固定源订阅。"), definition.Binding, definition.Subscription);
        var binding = new GraphBindingValue(definition.Resources.GetGraph(graph.Id), definition.Parameters, GraphUniformValues.Empty, GraphTransformValues.Empty);
        return new(definition, null, null!, new(definition.Seed, definition.Id), scope, null, HostGraph: new(graph, binding));
    }

    private static FrameOutput EvaluateHostDirectGraph(ActiveEffect active, FrameContext frame)
    {
        var graph = active.HostGraph ?? throw new EffectOutputException("InvalidDirectGraphActivity", "缺少宿主直接图活动。");
        var writes = ImmutableArray.CreateBuilder<TextWrite.GraphBinding>();
        Dictionary<ImageEffectTarget, ImageEffectBinding> bindings = [];
        GraphBindingBudget writerBudget = new();
        foreach (var declaration in graph.Export.Outputs)
        {
            frame.CancellationToken.ThrowIfCancellationRequested();
            switch (declaration.Target)
            {
                case "tokenFamily":
                    foreach (var token in frame.Presentations.TokensInBinding)
                    {
                        var geometry = frame.Geometry.FirstOrDefault(item => item.WithRuby == token.WithRuby);
                        if (geometry is null || !geometry.BaseGeometry.CanTransformIndependently)
                            throw new EffectOutputException(geometry?.BaseGeometry.DiagnosticCode ?? "UnsupportedGlyphPartition", "直接图须绑定真实可独立字族。");
                        Add(new TextTarget.Family(token.WithRuby));
                    }
                    break;
                case "line":
                    if (frame.Binding is EffectBinding.Tokens)
                        throw new EffectOutputException("PackageBindingNotAllowed", "token 绑定不能扩大到整行图输出。");
                    foreach (var line in frame.Presentations.LinesInBinding)
                        if (frame.LineGeometry.Any(item => item.Target == line.Target)) Add(new TextTarget.Line(line.Target));
                    break;
                case "textMark":
                    foreach (var mark in frame.Presentations.MarksInBinding)
                        if (frame.MarkGeometry.Any(item => item.Target == mark.Target)) Add(new TextTarget.Mark(mark.Target));
                    break;
                default: throw new EffectOutputException("PackageWriterPending", "此直接图目标的执行器未接通。");
            }
        }
        return new() { GraphWrites = writes.ToImmutable(), ImageEffects = [.. bindings.Values] };
        void Add(TextTarget target)
        {
            frame.CancellationToken.ThrowIfCancellationRequested(); writerBudget.Charge(graph.Binding, frame.CancellationToken);
            writes.Add(new(target, graph.Binding)); var imageTarget = GraphTarget(target); bindings[imageTarget] = new(imageTarget, graph.Binding);
        }
    }
}
