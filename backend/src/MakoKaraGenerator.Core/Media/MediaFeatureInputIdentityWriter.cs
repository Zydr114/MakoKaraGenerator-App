using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MakoKaraGenerator.Core.Media;

/// <summary>Normative domain-separated little-endian mapping, solid-source and fixed-input identity encoding.</summary>
/// <remarks>Own key fields and human failure detail are excluded. All nested identities are revalidated before use.</remarks>
public static class MediaFeatureInputIdentityWriter
{
    /// <summary>Hashes mapping fields in their fixed documented binary order, ignoring its own key.</summary>
    /// <param name="value">The structurally valid mapping.</param>
    /// <returns>The canonical mapping-domain key.</returns>
    public static MediaMappingKey ComputeMappingKey(MediaMapping value)
    {
        MediaFeatureInputRules.ValidateMappingShape(value);
        using var writer = new Hasher("mako.media.mapping.v1");
        writer.Int32(value.CanvasWidth); writer.Int32(value.CanvasHeight);
        writer.UInt32((uint)value.Fit); writer.UInt32((uint)value.Playback);
        writer.Color(value.BackColor); writer.UInt32((uint)value.OutputMode);
        writer.String(value.GeometryProfile); writer.String(value.CompositeProfile); writer.String(value.SamplingProfile);
        return new(writer.Finish());
    }

    /// <summary>Hashes the exact canonical solid color and complete profile without an artifact or decoder operation.</summary>
    /// <param name="value">The structurally valid analytic solid; its own key is ignored.</param>
    /// <returns>The separate solid-domain source key.</returns>
    public static MediaSourceKey ComputeSolidSourceKey(MediaFeatureSolidSource value)
    {
        MediaFeatureInputRules.ValidateSolidShape(value);
        using var writer = new Hasher("mako.media.solid-source.v1");
        writer.UInt32((uint)MediaSourceKind.Solid);
        writer.Color(value.Color); writer.Digest(value.Profile.Digest);
        return new(writer.Finish());
    }

    /// <summary>Hashes the complete fixed state with its context; available identities never include a preparation attempt.</summary>
    /// <param name="value">A recognized fixed input with every nested binding valid; its own revision is ignored.</param>
    /// <returns>The canonical fixed-input revision.</returns>
    public static MediaInputRevision ComputeInputRevision(MediaFeatureInput value)
    {
        MediaFeatureInputRules.ValidateInputShape(value);
        using var writer = new Hasher("mako.media.input.v1");
        // Five branch tags preserve the distinction between two Available representations.
        writer.UInt32(value switch
        {
            MediaFeatureInput.Pending => 0u,
            MediaFeatureInput.AvailableFile => 1u,
            MediaFeatureInput.AvailableSolid => 2u,
            MediaFeatureInput.Unavailable => 3u,
            MediaFeatureInput.NoMedia => 4u,
            _ => throw new MediaAbException(MediaAbFailure.ProfileUnsupported, "Media input identity has an unknown branch."),
        });
        MediaInputContext context = value.Context;
        writer.Digest(context.RequestedProfile.Digest);
        writer.Digest(context.Mapping.Key.Digest);
        writer.Digest(context.QueryCatalogDigest);
        writer.Digest(context.FixedDependencyDigest);
        switch (value)
        {
            case MediaFeatureInput.Pending pending:
                writer.Digest(pending.Source.Key.Digest); writer.Digest(pending.Request.Digest);
                break;
            case MediaFeatureInput.AvailableFile file:
                writer.Digest(file.Source.Key.Digest); writer.Artifact(file.Artifact);
                break;
            case MediaFeatureInput.AvailableSolid solid:
                writer.Digest(solid.Source.Key.Digest);
                break;
            case MediaFeatureInput.Unavailable failed:
                writer.Digest(failed.Source.Key.Digest); writer.Digest(failed.Request.Digest);
                writer.Digest(failed.Failure.Id.Digest); writer.String(failed.Failure.Code);
                break;
            case MediaFeatureInput.NoMedia absent:
                writer.UInt32((uint)absent.Reason); writer.Digest(absent.CapturedBackgroundDeclaration);
                break;
        }
        return new(writer.Finish());
    }

    private sealed class Hasher : IDisposable
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        internal Hasher(string domain) => String(domain);
        internal void UInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); _hash.AppendData(bytes);
        }
        internal void Int32(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value); _hash.AppendData(bytes);
        }
        internal void Int64(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value); _hash.AppendData(bytes);
        }
        internal void Double(double value) => Int64(BitConverter.DoubleToInt64Bits(value));
        internal void Boolean(bool value)
        {
            Span<byte> bytes = stackalloc byte[1]; bytes[0] = value ? (byte)1 : (byte)0; _hash.AppendData(bytes);
        }
        internal void Digest(MediaDigest value) => _hash.AppendData(Convert.FromHexString(value.LowerHex));
        internal void String(string value)
        {
            MediaAbRules.ValidateCanonicalText(value, MediaAbFailure.ProfileUnsupported);
            byte[] bytes = StrictUtf8.GetBytes(value); // Domains, fixed profiles or at most 64 byte failure code.
            UInt32(checked((uint)bytes.Length)); _hash.AppendData(bytes);
        }
        internal void Color(LinearPremulRgba value)
        { Double(value.R); Double(value.G); Double(value.B); Double(value.A); }
        internal void Artifact(MediaArtifactIdentity value)
        {
            Digest(value.ContentHash); Int64(value.ByteLength); String(value.Format);
            Digest(value.SourceKey.Digest); Digest(value.ProfileDigest); Digest(value.SourceDescriptorDigest);
            Digest(value.PresentationIndexDigest); Int64(value.FrameCount);
            Boolean(value.VideoDurationUs.HasValue);
            if (value.VideoDurationUs is { } duration) Int64(duration);
        }
        internal MediaDigest Finish() => new(Convert.ToHexStringLower(_hash.GetHashAndReset()));
        public void Dispose() => _hash.Dispose();
    }
}
