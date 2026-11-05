using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

public sealed partial class MediaFeatureSampler
{
    private void InitializeSolid(MediaFeatureInput.AvailableSolid solid, CancellationToken token)
    {
        // The existing kernel requires a complete quantized destination. Its fixed stack backing is admitted too.
        Span<QuantizedMediaMoments> quantized = stackalloc QuantizedMediaMoments[MediaTrackLayoutV1.Cells];
        _kernel.BuildSolidGrid(solid.Source.Color, quantized, token);
        QuantizedMediaMoments q = quantized[0];
        var raw = new MediaMomentValues(q.Rp / (double)MediaTrackLayoutV1.QuantMax, q.Gp / (double)MediaTrackLayoutV1.QuantMax,
            q.Bp / (double)MediaTrackLayoutV1.QuantMax, q.Alpha / (double)MediaTrackLayoutV1.QuantMax,
            q.YpSquared / (double)MediaTrackLayoutV1.QuantMax, q.AlphaYp / (double)MediaTrackLayoutV1.QuantMax,
            q.AlphaSquared / (double)MediaTrackLayoutV1.QuantMax);
        for (int i = 0; i < _grids.Length; i++) { token.ThrowIfCancellationRequested(); _grids[i] = raw; }
        _slots[0].Valid = true;
    }

    private ProjectMediaFeatureSample Integrate(ProjectMediaDependency dependency, MediaFeatureQueryRect rect,
        long timeUs, ReadOnlySpan<MediaMomentValues> grid, CancellationToken token)
    {
        if (dependency.Query.Space == EffectMediaCoordinateSpace.SourceNormalized)
        {
            MediaMomentValues raw = _kernel.IntegrateNormalizedRect(grid, new(rect.Left, rect.Top, rect.Right, rect.Bottom), token);
            MediaCompositedMoments result = MediaMomentComposer.Compose(raw, default);
            var source = new ProjectSourceMediaFeatureValues(result.MeanLinearPremulRgba, result.MeanPremulY,
                result.MeanSquaredPremulY, raw.AlphaYp, raw.AlphaSquared, result.PremulYVariance);
            return new(Input, dependency, rect, timeUs, sourceValues: source);
        }
        MediaMomentValues canvasRaw = _canvas.Integrate(grid, _placement!, new(rect.Left, rect.Top, rect.Right, rect.Bottom), token).RawMoments;
        MediaCompositedMoments composed = MediaMomentComposer.Compose(canvasRaw, Input.Context.Mapping.BackColor);
        var canvas = new ProjectCanvasMediaFeatureValues(composed.MeanLinearPremulRgba, composed.MeanPremulY,
            composed.MeanSquaredPremulY, composed.PremulYVariance);
        return new(Input, dependency, rect, timeUs, canvasValues: canvas);
    }

    private void ValidateRequest(ProjectMediaDependency dependency, MediaFeatureQueryRect rect, long timeUs)
    {
        if (timeUs < 0) throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Media feature engineering time must be nonnegative.");
        bool source = dependency.Query.Space == EffectMediaCoordinateSpace.SourceNormalized;
        if (!source && dependency.Query.Space != EffectMediaCoordinateSpace.CanvasPixels)
            throw new MediaAbException(MediaAbFailure.ProfileUnsupported, "Media query coordinate space is not recognized.");
        double width = source ? 1 : Input.Context.Mapping.CanvasWidth, height = source ? 1 : Input.Context.Mapping.CanvasHeight;
        if (!double.IsFinite(rect.Left) || !double.IsFinite(rect.Top) || !double.IsFinite(rect.Right) || !double.IsFinite(rect.Bottom)
            || rect.Left < 0 || rect.Top < 0 || rect.Right > width || rect.Bottom > height
            || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            throw new MediaAbException(MediaAbFailure.StatisticsInvalid, "Media query rectangle must be finite nonempty and wholly inside its declared coordinate space.");
    }

    private static MediaDisplayPlacement? Placement(MediaFeatureInput input)
    {
        MediaMapping map = input.Context.Mapping;
        MediaRasterGeometry geometry;
        if (input is MediaFeatureInput.AvailableFile file)
        {
            MediaFilePresentationDescriptor value = file.Source.Presentation;
            geometry = MediaRasterGeometry.Create(value.EncodedWidth, value.EncodedHeight, value.SarNumerator,
                value.SarDenominator, value.Rotation, value.MirrorHorizontallyBeforeRotation);
        }
        else if (input is MediaFeatureInput.AvailableSolid)
            geometry = MediaRasterGeometry.Create(map.CanvasWidth, map.CanvasHeight, 1, 1, MediaQuarterTurn.None, false);
        else return null;
        return MediaDisplayPlacement.Compute(geometry, map.CanvasWidth, map.CanvasHeight, map.Fit);
    }

    private static void ValidateReader(MediaFeatureInput input, IMediaFeatureTrackReader? reader)
    {
        if (input is not MediaFeatureInput.AvailableFile file)
        {
            if (reader is not null) throw new MediaAbException(MediaAbFailure.ProfileUnsupported, "Only an available file input may borrow a feature track reader.");
            return;
        }
        if (reader is null) throw new MediaAbException(MediaAbFailure.ArtifactInvalid, "Available file input requires its fully validated feature track reader.");
        MediaArtifactIdentity artifact = reader.Artifact;
        PresentationIndexSummary summary = reader.Summary;
        if (artifact != file.Artifact || summary.SourceKey != file.Source.Key || summary.ProfileDigest != file.Source.Profile.Digest
            || summary.SourceDescriptorDigest != file.Source.Presentation.Digest || summary.IndexDigest != file.Artifact.PresentationIndexDigest
            || summary.FrameCount != file.Artifact.FrameCount || summary.VideoDurationUs != file.Artifact.VideoDurationUs)
            throw new MediaAbException(MediaAbFailure.ArtifactInvalid, "Feature reader artifact or presentation summary does not match the complete fixed available input.");
    }
}
