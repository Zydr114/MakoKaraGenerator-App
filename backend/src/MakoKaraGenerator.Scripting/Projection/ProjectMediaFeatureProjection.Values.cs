using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Scripting.V2;
using CoreInput = MakoKaraGenerator.Core.Media.MediaFeatureInput;
using CoreReason = MakoKaraGenerator.Core.Media.MediaNoMediaReason;

namespace MakoKaraGenerator.Scripting.Projection;

internal sealed partial class ProjectMediaFeatureProjection
{
    private static MediaFeatureInputInfo ProjectMediaFeatureValues(CoreInput input)
    {
        // Core and SDK state ordinals intentionally differ. Map the closed
        // algebra explicitly and expose only safe stable codes, never detail.
        (MediaInputState State, string? Source, string? Artifact, string? Diagnostic, string? Reason) values = input switch
        {
            CoreInput.Pending pending => (MediaInputState.Pending, pending.Source.Key.Digest.LowerHex, null, null, null),
            CoreInput.AvailableFile available => (MediaInputState.Available, available.Source.Key.Digest.LowerHex,
                available.Artifact.ContentHash.LowerHex, null, null),
            CoreInput.AvailableSolid solid => (MediaInputState.Available, solid.Source.Key.Digest.LowerHex, null, null, null),
            CoreInput.Unavailable unavailable => (MediaInputState.Unavailable, unavailable.Source.Key.Digest.LowerHex, null, unavailable.Failure.Code, null),
            CoreInput.NoMedia absent => (MediaInputState.NoMedia, null, null, null, absent.Reason switch
            {
                CoreReason.TransparentOutput => "TransparentOutput",
                CoreReason.NoBackgroundContribution => "NoBackgroundContribution",
                _ => throw Mismatch(),
            }),
            _ => throw Mismatch(),
        };
        return new(values.State, input.InputRevision.Digest.LowerHex, input.Context.Mapping.Key.Digest.LowerHex,
            input.Context.RequestedProfile.Digest.LowerHex, values.Source, values.Artifact, values.Diagnostic, values.Reason);
    }

    private static MediaCoordinateSpace Space(EffectMediaCoordinateSpace space) => space switch
    {
        EffectMediaCoordinateSpace.SourceNormalized => MediaCoordinateSpace.SourceNormalized,
        EffectMediaCoordinateSpace.CanvasPixels => MediaCoordinateSpace.CanvasPixels,
        _ => throw Mismatch(),
    };

    private static MediaFeatureSample DetachedSample(ProjectMediaFeatureSample result, MediaFeatureInputInfo input,
        MediaQueryProfile profile, Rect2 rect, long time)
    {
        bool available = input.State == MediaInputState.Available;
        bool source = available && profile.Space == MediaCoordinateSpace.SourceNormalized;
        bool canvas = available && profile.Space == MediaCoordinateSpace.CanvasPixels;
        if ((result.SourceValues is not null) != source || (result.CanvasValues is not null) != canvas) throw ResultMismatch();
        try
        {
            SourceMediaFeatureValues? sourceValues = result.SourceValues is { } originalSource
                ? new(new(originalSource.Mean.R, originalSource.Mean.G, originalSource.Mean.B, originalSource.Mean.A),
                    originalSource.MeanPremulY, originalSource.MeanSquaredPremulY, originalSource.MeanAlphaTimesPremulY,
                    originalSource.MeanSquaredAlpha, originalSource.PremulYVariance) : null;
            CanvasMediaFeatureValues? canvasValues = result.CanvasValues is { } originalCanvas
                ? new(new(originalCanvas.Mean.R, originalCanvas.Mean.G, originalCanvas.Mean.B, originalCanvas.Mean.A),
                    originalCanvas.MeanPremulY, originalCanvas.MeanSquaredPremulY, originalCanvas.PremulYVariance) : null;
            return new(input, profile, rect, time, sourceValues, canvasValues);
        }
        catch (ArgumentException) { throw ResultMismatch(); }
    }

    private static bool Same(double left, double right) => BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);
}
