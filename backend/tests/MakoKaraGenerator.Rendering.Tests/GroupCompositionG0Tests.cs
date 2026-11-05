using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

// The group contract inherits the complete selected child E.
// Copy beside ImageGraphCompositionChainTests.cs when root integrates these
// candidates. No production group renderer is exercised here.
public sealed partial class ImageGraphCompositionChainTests
{
    [Fact]
    public void G0_A1_独立孩子先顺序组合再经过父图仍保留96百分比擦除()
    {
        using var target = new Surface(SKColors.Transparent);
        using var mask = new Surface(new(0, 0, 0, 204), ShaderSurfaceKind.Coverage);
        using var below = new Surface(SKColors.Blue);
        using var childGraph = Graph(replacement: true);
        using var parentGraph = Graph();
        using var first = childGraph.Execute(Frame(), Inputs(target.Value, mask.Value));
        using var second = childGraph.Execute(Frame(), Inputs(target.Value, mask.Value));
        using var a = childGraph.CaptureCompositionPayload([first]);
        using var b = childGraph.CaptureCompositionPayload([second]);
        using var children = parentGraph.ComposePayloads([a, b]);
        using var parentResult = parentGraph.Execute(Frame(), Inputs(children.Color));
        using var own = parentGraph.CaptureCompositionPayload([parentResult]);
        using var inherited = parentGraph.InheritCompositionErasure(own, children);
        using var actual = parentGraph.CompositePayload(inherited, below.Value);

        // Independent reference executes both child operations on the actual Below.
        using var afterFirst = childGraph.CompositeChain([first], below.Value);
        using var reference = childGraph.CompositeChain([second], afterFirst);
        NearPixels(reference, actual, 2);
        G0AssertPixel(inherited.Erasure, [0, 0, 0, 245], tolerance: 1);
        G0AssertPixel(actual, [10, 0, 0, 10], tolerance: 1);

        // This is precisely the forbidden child-history flattening at a parent.
        using var flattened = childGraph.CaptureCompositionPayload([first, second]);
        using var wrong = parentGraph.CompositePayload(flattened, below.Value);
        G0AssertPixel(wrong, [51, 0, 0, 51]);
        Assert.NotEqual(Bytes(reference), Bytes(wrong));
    }

    [Fact]
    public void G0_A2_同目标历史两次80百分比替换保留max而不变为96百分比()
    {
        using var target = new Surface(SKColors.Transparent);
        using var mask = new Surface(new(0, 0, 0, 204), ShaderSurfaceKind.Coverage);
        using var below = new Surface(SKColors.Blue);
        using var graph = Graph(replacement: true);
        using var first = graph.Execute(Frame(), Inputs(target.Value, mask.Value));
        using var last = graph.Execute(Frame(), Inputs(first.Output, mask.Value));
        using var chain = graph.CaptureCompositionPayload([first, last]);
        using var actual = graph.CompositePayload(chain, below.Value);
        using var reference = graph.CompositeChain([first, last], below.Value);

        NearPixels(reference, actual);
        G0AssertPixel(chain.Erasure, [0, 0, 0, 204]);
        G0AssertPixel(actual, [51, 0, 0, 51]);

        // Treating this same-target history as independent children is also wrong.
        using var a = graph.CaptureCompositionPayload([first]);
        using var b = graph.CaptureCompositionPayload([last]);
        using var wrong = graph.ComposePayloads([a, b]);
        G0AssertPixel(wrong.Erasure, [0, 0, 0, 245], tolerance: 1);
        Assert.NotEqual(Pixel(chain.Erasure), Pixel(wrong.Erasure));
    }

    [Fact]
    public void G0_A3_普通孩子经过父淡出仍按组合同继承完整孩子擦除()
    {
        using var childColor = new Surface(new(255, 0, 0, 128));
        using var below = new Surface(SKColors.Blue);
        using var parentGraph = Graph(amount: .5);
        using var child = parentGraph.CaptureCompositionPayload(childColor.Value, Frame());
        using var parentResult = parentGraph.Execute(Frame(), Inputs(child.Color));
        using var own = parentGraph.CaptureCompositionPayload([parentResult]);

        // Group contract section 3 intentionally retains the complete child E,
        // including ordinary child alpha. Parent fade changes C, not that floor.
        using var inherited = parentGraph.InheritCompositionErasure(own, child);
        using var actual = parentGraph.CompositePayload(inherited, below.Value);
        using var reference = parentGraph.Composite(parentResult, below.Value);

        G0AssertPixel(child.Color, [0, 0, 128, 128]);
        G0AssertPixel(child.Erasure, [0, 0, 0, 128]);
        G0AssertPixel(reference, [191, 0, 64, 255]);
        G0AssertPixel(inherited.Erasure, [0, 0, 0, 128]);
        G0AssertPixel(actual, [127, 0, 64, 191]);
        Assert.NotEqual(Bytes(reference), Bytes(actual));
    }

    [Fact]
    public void G0_A4_普通与替换来源具有相同ColorErasure时父边界行为相同()
    {
        using var ordinaryColor = new Surface(new(255, 0, 0, 128));
        using var opaqueRed = new Surface(SKColors.Red);
        using var mask = new Surface(new(0, 0, 0, 128), ShaderSurfaceKind.Coverage);
        using var below = new Surface(SKColors.Blue);
        using var replace = Graph(replacement: true);
        using var parentGraph = Graph(amount: .5);
        using var ordinary = parentGraph.CaptureCompositionPayload(ordinaryColor.Value, Frame());
        using var childResult = replace.Execute(Frame(), Inputs(opaqueRed.Value, mask.Value));
        using var child = replace.CaptureCompositionPayload([childResult]);

        // The existing parent contract operates on C/E, so these two inputs have
        // identical subsequent behavior. It does not inspect replacement origin.
        NearPixels(ordinary.Color, child.Color);
        NearPixels(ordinary.Erasure, child.Erasure);
        using var parentResult = parentGraph.Execute(Frame(), Inputs(child.Color));
        using var own = parentGraph.CaptureCompositionPayload([parentResult]);
        using var inherited = parentGraph.InheritCompositionErasure(own, child);
        using var actual = parentGraph.CompositePayload(inherited, below.Value);

        // Same-target history supplies an independent existing reference for
        // this single replacement descendant; never generalize it to A1.
        using var reference = parentGraph.CompositeChain([childResult, parentResult], below.Value);
        NearPixels(reference, actual);
        G0AssertPixel(inherited.Erasure, [0, 0, 0, 128]);
        G0AssertPixel(actual, [127, 0, 64, 191]);
        using var ordinaryParentResult = parentGraph.Execute(Frame(), Inputs(ordinary.Color));
        using var ordinaryOwn = parentGraph.CaptureCompositionPayload([ordinaryParentResult]);
        using var ordinaryInherited = parentGraph.InheritCompositionErasure(ordinaryOwn, ordinary);
        using var ordinaryActual = parentGraph.CompositePayload(ordinaryInherited, below.Value);
        NearPixels(ordinaryActual, actual);
        using var lostReplacement = parentGraph.Composite(parentResult, below.Value);
        G0AssertPixel(lostReplacement, [191, 0, 64, 255]);
        Assert.NotEqual(Bytes(actual), Bytes(lostReplacement));
    }

    [Fact]
    public void G0_A5_独立孩子经过父图后两级组alpha各对整组ColorErasure乘一次()
    {
        using var opaqueRed = new Surface(SKColors.Red);
        using var mask = new Surface(new(0, 0, 0, 204), ShaderSurfaceKind.Coverage);
        using var below = new Surface(SKColors.Blue);
        using var childGraph = Graph(replacement: true);
        using var parentGraph = Graph();
        using var first = childGraph.Execute(Frame(), Inputs(opaqueRed.Value, mask.Value));
        using var second = childGraph.Execute(Frame(), Inputs(opaqueRed.Value, mask.Value));
        using var a = childGraph.CaptureCompositionPayload([first]);
        using var b = childGraph.CaptureCompositionPayload([second]);
        using var children = parentGraph.ComposePayloads([a, b]);
        using var parentResult = parentGraph.Execute(Frame(), Inputs(children.Color));
        using var own = parentGraph.CaptureCompositionPayload([parentResult]);
        using var inherited = parentGraph.InheritCompositionErasure(own, children);
        using var inner = parentGraph.ComposePayloads([inherited], opacity: .5);
        using var outer = parentGraph.ComposePayloads([inner], opacity: .5);
        using var actual = parentGraph.CompositePayload(outer, below.Value);
        using var referencePayload = parentGraph.ComposePayloads([inherited], opacity: .25);
        using var reference = parentGraph.CompositePayload(referencePayload, below.Value);

        NearPixels(reference, actual, 2);
        G0AssertPixel(outer.Color, [0, 0, 61, 61], tolerance: 2);
        G0AssertPixel(outer.Erasure, [0, 0, 0, 61], tolerance: 2);
        G0AssertPixel(actual, [194, 0, 61, 255], tolerance: 2);

        // Applying the inner group alpha to each child before their ordered
        // composition gives .4+.4*(1-.4)=.64, then .5 => .32, not .96*.25=.24.
        using var wrongA = parentGraph.ComposePayloads([a], opacity: .5);
        using var wrongB = parentGraph.ComposePayloads([b], opacity: .5);
        using var wrong = parentGraph.ComposePayloads([wrongA, wrongB], opacity: .5);
        G0AssertPixel(wrong.Erasure, [0, 0, 0, 82], tolerance: 2);
        Assert.NotEqual(Pixel(outer.Erasure), Pixel(wrong.Erasure));
    }

    [Fact]
    public void G0_A6_共同alpha分解保留局部来源且不复活被后层完全遮挡的早层()
    {
        using var red = new Surface(new(255, 0, 0, 128));
        using var green = new Surface(SKColors.Transparent, draw: canvas =>
        {
            using var paint = new SKPaint { Color = new SKColor(0, 255, 0, 128) };
            canvas.DrawRect(0, 0, 8, 16, paint);
            paint.Color = new SKColor(0, 255, 0, 255);
            canvas.DrawRect(8, 0, 8, 16, paint);
        });
        using var below = new Surface(SKColors.Blue);
        using var graph = Graph();
        using var first = graph.CaptureCompositionPayload(red.Value, Frame());
        using var second = graph.CaptureCompositionPayload(green.Value, Frame());
        var resolved = graph.ResolveCompositionOpacityLayers([first, second], opacity: .5);
        try
        {
            Assert.Equal(2, resolved.Length); // Caller retains its corresponding source identities.
            using var combined = graph.ComposePayloads(resolved);
            using var actual = graph.CompositePayload(combined, below.Value);
            using var referencePayload = graph.ComposePayloads([first, second], opacity: .5);
            using var reference = graph.CompositePayload(referencePayload, below.Value);
            NearPixels(reference, actual, 2);

            // At x=4 the later layer is half opaque. Resolving the shared alpha
            // makes the first effective alpha about 1/6, rather than raw .5*.5.
            G0AssertPixel(resolved[0].Color, [0, 0, 43, 43], x: 4, tolerance: 2);
            G0AssertPixel(resolved[0].Erasure, [0, 0, 0, 43], x: 4, tolerance: 2);
            G0AssertPixel(actual, [159, 64, 32, 255], x: 4, tolerance: 2);

            // At x=12 the later layer was fully opaque before the group alpha;
            // selecting only the first resolved source must remain empty there.
            G0AssertPixel(resolved[0].Color, [0, 0, 0, 0], x: 12);
            G0AssertPixel(resolved[0].Erasure, [0, 0, 0, 0], x: 12);
            using var firstOnly = graph.CompositePayload(resolved[0], below.Value);
            G0AssertPixel(firstOnly, [255, 0, 0, 255], x: 12);

            using var naiveFirst = graph.ComposePayloads([first], opacity: .5);
            G0AssertPixel(naiveFirst.Color, [0, 0, 64, 64], x: 12);
            Assert.NotEqual(Pixel(resolved[0].Color, 12), Pixel(naiveFirst.Color, 12));
        }
        finally { foreach (var layer in resolved) layer.Dispose(); }
    }

    private static void G0AssertPixel(SkiaGraphSurface surface, byte[] expected, int x = 8, int tolerance = 1)
    {
        byte[] actual = Pixel(surface, x);
        Assert.Equal(expected.Length, actual.Length);
        for (int channel = 0; channel < expected.Length; channel++)
            Assert.InRange((int)actual[channel], Math.Max(0, expected[channel] - tolerance), Math.Min(255, expected[channel] + tolerance));
    }
}
