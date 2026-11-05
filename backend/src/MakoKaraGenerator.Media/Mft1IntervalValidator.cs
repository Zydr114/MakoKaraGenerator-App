using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

/// <summary>One-pass validation shared by the live callback and untrusted wire boundary.</summary>
internal sealed class Mft1IntervalValidator(MediaFeatureFileSource source, MediaAbFailure failure)
{
    private readonly MediaPresentationTimeMath _time = new();
    private RationalTimestamp? _origin;
    private PresentationInterval.Timed? _previous;
    internal long Count { get; private set; }
    internal long EndUs => _previous?.EndUs ?? 0;

    internal void Accept(PresentationInterval interval)
    {
        Require(interval.Id.Index == Count, "Presentation interval ordinal is not contiguous.");
        if (source.Kind == MediaSourceKind.Still)
        {
            Require(Count == 0 && interval is PresentationInterval.Timeless, "A still requires exactly one timeless interval.");
            Count++;
            return;
        }

        Require(interval is PresentationInterval.Timed, "A video requires timed presentation intervals.");
        var timed = (PresentationInterval.Timed)interval;
        try
        {
            Require(timed.RawStart.TimeBase == source.Presentation.StreamTimeBase,
                "Frame start time base differs from the accepted stream descriptor.");
            _origin ??= timed.RawStart;
            Require(timed.StartUs >= 0 && timed.EndUs > timed.StartUs && timed.EndUs <= MediaAbCapacityV1.MaximumVideoDurationUs,
                "Presentation interval has invalid canonical microsecond boundaries.");
            Require(_time.Compare(timed.RawStart, timed.RawEnd) < 0 &&
                _time.NormalizeBoundaryUs(timed.RawStart, _origin.Value) == timed.StartUs &&
                _time.NormalizeBoundaryUs(timed.RawEnd, _origin.Value) == timed.EndUs,
                "Presentation boundaries disagree with exact source timestamps.");
            Require(timed.EndAuthority is PresentationEndAuthority.NextPts or PresentationEndAuthority.FrameDuration or PresentationEndAuthority.StreamEndpoint,
                "Video interval endpoint authority is invalid.");
            if (_previous is null)
                Require(timed.StartUs == 0, "First presentation interval must start at zero.");
            else
                Require(_previous.EndAuthority == PresentationEndAuthority.NextPts && _previous.EndUs == timed.StartUs &&
                    _previous.RawEnd == timed.RawStart && _time.Compare(_previous.RawStart, timed.RawStart) < 0,
                    "Adjacent presentation intervals do not share the exact next timestamp.");
        }
        catch (MediaAbException ex) when (ex.Failure == MediaAbFailure.PresentationInvalid && failure != MediaAbFailure.PresentationInvalid)
        {
            throw new MediaAbException(failure, "Presentation rational arithmetic is not canonical.");
        }

        _previous = timed;
        Count = checked(Count + 1);
    }

    internal void Complete(long frameCount, long? durationUs)
    {
        Require(Count == frameCount && Count > 0, "Presentation count does not match the complete index.");
        if (source.Kind == MediaSourceKind.Still)
            Require(Count == 1 && durationUs is null, "Timeless presentation summary is invalid.");
        else
            Require(_previous is not null && _previous.EndUs == durationUs &&
                _previous.EndAuthority is PresentationEndAuthority.FrameDuration or PresentationEndAuthority.StreamEndpoint,
                "Final presentation endpoint is not authoritative or disagrees with duration.");
    }

    private void Require(bool condition, string detail)
    {
        if (!condition) throw new MediaAbException(failure, detail);
    }
}
