using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    // The effective members retain partial-capture identity. The independent full
    // range was composed BEFORE the common opacity was quantized into each member.
    // It contains foreground C and complete E only, never an actual screen/Below.
    private sealed record ClosedLayerRange(ImmutableArray<ColorLayer> Members, ImageCompositionPayload Payload);

    private static IEnumerable<ImageCompositionPayload> ClosedLayerPayloads(RenderRequest request, ImageObjectState state,
        ColorLayer[] ordered, Func<ColorLayer, ImageCompositionPayload>? payloadForLayer)
    {
        for (int index = 0; index < ordered.Length;)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            ClosedLayerRange? selected = null;
            foreach (var range in state.ExactRanges)
            {
                if (range.Members.IsEmpty || range.Members.Length > ordered.Length - index
                    || selected is not null && range.Members.Length <= selected.Members.Length) continue;
                bool matches = true;
                for (int offset = 0; offset < range.Members.Length; offset++)
                    if (!ReferenceEquals(range.Members[offset], ordered[index + offset])) { matches = false; break; }
                if (matches) selected = range;
            }
            if (selected is not null)
            {
                yield return selected.Payload;
                index += selected.Members.Length;
            }
            else
            {
                var layer = ordered[index++];
                yield return payloadForLayer is not null ? payloadForLayer(layer) : layer.ClosedPayload
                    ?? throw new PreparedFrameException("GroupCompositionUnclosedChild", "孩子必须先关闭独立链；不能借用含 Below 的截图。");
            }
        }
    }

    private static void InvalidateExactLayerRanges(ImageObjectState state, ColorLayer[] changed)
        => state.ExactRanges.RemoveAll(range => range.Members.Any(member => changed.Any(layer => ReferenceEquals(member, layer))));

    // A true child -> parent boundary copies both partial members and the complete
    // independent payload. Neither child History nor parent same-target History
    // crosses this boundary. References are remapped to the new owned member list.
    private void AppendClosedGroupLayers(RenderRequest request, ImageObjectState child, ImageObjectState parent)
    {
        var ordered = child.Layers.OrderBy(layer => layer.Order).ToArray();
        parent.SetLayerBudget(checked(parent.Layers.Count + ordered.Length));
        Dictionary<ColorLayer, ColorLayer> copiedLayers = new(ReferenceEqualityComparer.Instance);
        foreach (var layer in ordered)
        {
            var payload = parent.Own(_compositionProgram.ComposePayloads([layer.ClosedPayload!], parent.Resources,
                cancellationToken: request.CancellationToken));
            var copy = ClosedColorLayer(parent.Layers.Count, layer.Sources, payload, layer.Owner);
            parent.Layers.Add(copy); copiedLayers.Add(layer, copy);
        }
        foreach (var range in child.ExactRanges)
        {
            if (range.Members.IsEmpty || range.Members.Any(member => !copiedLayers.ContainsKey(member))) continue;
            var payload = parent.Own(_compositionProgram.ComposePayloads([range.Payload], parent.Resources,
                cancellationToken: request.CancellationToken));
            parent.ExactRanges.Add(new([.. range.Members.Select(member => copiedLayers[member])], payload));
        }
        parent.SetLayerBudget(parent.Layers.Count);
    }
}
