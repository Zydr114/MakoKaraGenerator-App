using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;
using static MakoKaraGenerator.Rendering.Skia.SkiaSceneGeometry;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private static bool HasTextPartitions(PreparedRenderFrame? prepared, string binding, string? token = null)
    {
        return prepared is not null && (prepared.BaseTransforms.Any(item => Matches(item.Target.TokenId, item.Target.BindingId))
            || prepared.BaseStyles.Any(item => Matches(item.Target.TokenId, item.Target.BindingId))
            || prepared.ClusterTransforms.Any(item => Matches(item.Target.TokenId, item.Target.BindingId)));
        bool Matches(Fx.TokenId source, Fx.BindingId load) => load.Value == binding && (token is null || source.Value == token);
    }

    private static bool HasOnlyRubyPartitions(PreparedRenderFrame? prepared, string binding, string token)
    {
        if (prepared is null || prepared.BaseTransforms.Any(item => item.Target.BindingId.Value == binding && item.Target.TokenId.Value == token)
            || prepared.BaseStyles.Any(item => item.Target.BindingId.Value == binding && item.Target.TokenId.Value == token)) return false;
        var clusters = prepared.ClusterTransforms.Where(item => item.Target.BindingId.Value == binding && item.Target.TokenId.Value == token).ToArray();
        return clusters.Length > 0 && clusters.All(static item => item.Target.RubyId is not null);
    }

    private bool DrawTextPartitions(SKCanvas canvas, PreparedRenderFrame? prepared, SceneLineSample scene,
        ShapedLine shaped, TextLayoutOptions options, IReadOnlyList<RubyLayout> ruby, LyricFrameSample tokenSample,
        int tokenIndex, LyricStyle familyStyle, LyricStyle lineStyle, Composition composition,
        float origin, float baseline, bool antialias, ImmutableArray<int> units, CapturePaint? capture, bool rubyOnly = false)
    {
        string tokenId = scene.Frame.Line!.Tokens[tokenIndex].Id;
        if (!HasTextPartitions(prepared, scene.BindingId!, tokenId)) return false;
        Fx.BaseTokenTarget target = new(new(tokenId), new(scene.BindingId!));
        var basic = prepared!.BaseTransforms.FirstOrDefault(item => item.Target == target)?.LocalMatrix ?? Fx.Affine2.Identity;
        var style = prepared.BaseStyles.Any(item => item.Target == target) ? prepared.GetBaseDrawingStyle(target) : familyStyle;
        var transforms = prepared.ClusterTransforms.Where(item => item.Target.TokenId.Value == tokenId && item.Target.BindingId.Value == scene.BindingId).ToArray();
        var baseClusters = transforms.Where(static item => item.Target.RubyId is null).ToDictionary(static item => item.Target.UnitIndex);
        if (!rubyOnly)
        {
            canvas.Save(); Concat(basic);
            DrawBase([.. units.Where(index => !baseClusters.ContainsKey(index))]);
            foreach (int index in units.Where(baseClusters.ContainsKey))
            {
                canvas.Save(); Concat(baseClusters[index].LocalMatrix); DrawBase([index]); canvas.Restore();
            }
            canvas.Restore();
        }
        foreach (var annotation in ruby.Where(item => item.TokenIndex == tokenIndex))
        {
            var selected = transforms.Where(item => item.Target.RubyId?.Value == annotation.Id).ToDictionary(static item => item.Target.UnitIndex);
            DrawRuby(canvas, [annotation], scene.Frame, scene.Frame.Line!, lineStyle, composition, origin, baseline, antialias,
                familyStyle, capture, [.. Enumerable.Range(0, annotation.Shaped.DrawUnits.Length).Where(index => !selected.ContainsKey(index))]);
            foreach (var (index, transform) in selected.OrderBy(static item => item.Key))
            {
                canvas.Save(); Concat(transform.LocalMatrix);
                DrawRuby(canvas, [annotation], scene.Frame, scene.Frame.Line!, lineStyle, composition, origin, baseline, antialias,
                    familyStyle, capture, [index]);
                canvas.Restore();
            }
        }
        return true;

        void DrawBase(ImmutableArray<int> indices)
        {
            if (indices.IsEmpty) return;
            DrawLineGlow(canvas, shaped, options, origin, baseline, style, antialias, indices, capture);
            using WipePartition wipe = BuildWipe(tokenSample, shaped, origin, canvas.LocalClipBounds, style.DecorationsDifferByState,
                _ => style.Wipe.FeatherRatio, options.FontSizePx);
            DrawWiped(canvas, wipe, () => WipeStatesDiffer(style, capture), capture, sung =>
            {
                DrawLineGlow(canvas, shaped, options, origin, baseline, style, antialias, indices, capture, state: sung);
                DrawStyledLine(canvas, shaped, options, origin, baseline,
                    sung ? style.SungColor : style.UnsungColor, sung ? style.SungFill.Gradient : style.UnsungFill.Gradient,
                    style, composition, antialias, indices, capture, sung);
            });
        }
        void Concat(Fx.Affine2 matrix) => canvas.Concat(new SKMatrix((float)matrix.A, (float)matrix.C, (float)matrix.Tx,
            (float)matrix.B, (float)matrix.D, (float)matrix.Ty, 0, 0, 1));
    }
}
