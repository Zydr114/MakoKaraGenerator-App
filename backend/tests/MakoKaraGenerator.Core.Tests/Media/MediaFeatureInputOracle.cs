using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Core.Tests.Media;

/// <summary>Independent buffered byte encoder; deliberately never invokes a production identity writer.</summary>
internal static class MediaFeatureInputOracle
{
    internal static string Mapping(MediaMapping value) => Hash(writer =>
    {
        Text(writer, "mako.media.mapping.v1");
        writer.Write(value.CanvasWidth); writer.Write(value.CanvasHeight);
        writer.Write((uint)value.Fit); writer.Write((uint)value.Playback);
        Color(writer, value.BackColor); writer.Write((uint)value.OutputMode);
        Text(writer, value.GeometryProfile); Text(writer, value.CompositeProfile); Text(writer, value.SamplingProfile);
    });

    internal static string Solid(MediaFeatureSolidSource value) => Hash(writer =>
    {
        Text(writer, "mako.media.solid-source.v1"); writer.Write(3u);
        Color(writer, value.Color); Digest(writer, value.Profile.Digest);
    });

    internal static string Profile(MediaProfileBundle value) => Hash(writer =>
    {
        Text(writer, "mako.media.profile-bundle.v1");
        foreach (string profile in new[] { value.Presentation, value.DecoderPipeline, value.Color, value.Statistics,
            value.Quantization, value.Codec, value.Query, value.Gpu }) Text(writer, profile);
        Digest(writer, value.DecoderIdentity); Digest(writer, value.ColorInterpretation);
    });

    internal static string Input(MediaFeatureInput value) => Hash(writer =>
    {
        Text(writer, "mako.media.input.v1");
        uint tag = value switch { MediaFeatureInput.Pending => 0, MediaFeatureInput.AvailableFile => 1,
            MediaFeatureInput.AvailableSolid => 2, MediaFeatureInput.Unavailable => 3, MediaFeatureInput.NoMedia => 4,
            _ => throw new ArgumentException("Unknown oracle input.") };
        writer.Write(tag);
        Digest(writer, value.Context.RequestedProfile.Digest); Digest(writer, value.Context.Mapping.Key.Digest);
        Digest(writer, value.Context.QueryCatalogDigest); Digest(writer, value.Context.FixedDependencyDigest);
        switch (value)
        {
            case MediaFeatureInput.Pending p: Digest(writer, p.Source.Key.Digest); Digest(writer, p.Request.Digest); break;
            case MediaFeatureInput.AvailableFile f:
                Digest(writer, f.Source.Key.Digest); Digest(writer, f.Artifact.ContentHash); writer.Write(f.Artifact.ByteLength);
                Text(writer, f.Artifact.Format); Digest(writer, f.Artifact.SourceKey.Digest); Digest(writer, f.Artifact.ProfileDigest);
                Digest(writer, f.Artifact.SourceDescriptorDigest); Digest(writer, f.Artifact.PresentationIndexDigest);
                writer.Write(f.Artifact.FrameCount); writer.Write(f.Artifact.VideoDurationUs.HasValue);
                if (f.Artifact.VideoDurationUs is { } duration) writer.Write(duration);
                break;
            case MediaFeatureInput.AvailableSolid s: Digest(writer, s.Source.Key.Digest); break;
            case MediaFeatureInput.Unavailable u:
                Digest(writer, u.Source.Key.Digest); Digest(writer, u.Request.Digest);
                Digest(writer, u.Failure.Id.Digest); Text(writer, u.Failure.Code); break;
            case MediaFeatureInput.NoMedia n:
                writer.Write((uint)n.Reason); Digest(writer, n.CapturedBackgroundDeclaration); break;
        }
    });

    private static string Hash(Action<BinaryWriter> encode)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true), leaveOpen: true)) encode(writer);
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }
    private static void Text(BinaryWriter writer, string value)
    {
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(value);
        writer.Write(checked((uint)bytes.Length)); writer.Write(bytes);
    }
    private static void Digest(BinaryWriter writer, MediaDigest value) => writer.Write(Convert.FromHexString(value.LowerHex));
    private static void Color(BinaryWriter writer, LinearPremulRgba value)
    { writer.Write(value.R); writer.Write(value.G); writer.Write(value.B); writer.Write(value.A); }
}
