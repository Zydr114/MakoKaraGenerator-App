using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MakoKaraGenerator.Core.Media;

/// <summary>Common normative domain-separated little-endian identity encoding.</summary>
/// <remarks>Digest fields belonging to the value itself are excluded. Hashing structurally valid values never grants source, tool or tag authority.</remarks>
public interface IMediaAbIdentityWriter
{
    /// <summary>Hashes the native color interpretation.</summary>
    /// <param name="value">The structurally valid interpretation, whose own digest is ignored.</param>
    /// <returns>The exact domain-separated SHA-256 identity.</returns>
    /// <exception cref="MediaAbException">A field is invalid.</exception>
    MediaDigest ComputeColorDigest(MediaColorInterpretation value);

    /// <summary>Hashes the tool closure and normalized pipeline digests.</summary>
    /// <param name="value">The structurally valid tool, whose own digest is ignored.</param>
    /// <returns>The exact domain-separated SHA-256 identity.</returns>
    /// <exception cref="MediaAbException">A field is invalid.</exception>
    MediaDigest ComputeToolDigest(MediaToolIdentity value);

    /// <summary>Hashes the complete versioned profile bundle.</summary>
    /// <param name="value">The supported bundle, whose own digest is ignored.</param>
    /// <returns>The exact domain-separated SHA-256 identity.</returns>
    /// <exception cref="MediaAbException">A field is invalid or a profile is unsupported.</exception>
    MediaDigest ComputeProfileDigest(MediaProfileBundle value);

    /// <summary>Hashes the raster, exact SAR, stream time base and color binding.</summary>
    /// <param name="value">The structurally valid descriptor, whose own digest is ignored.</param>
    /// <returns>The exact domain-separated SHA-256 identity.</returns>
    /// <exception cref="MediaAbException">A field is invalid.</exception>
    MediaDigest ComputeDescriptorDigest(MediaFilePresentationDescriptor value);

    /// <summary>Hashes the immutable file source without its own key.</summary>
    /// <param name="value">The structurally valid file source.</param>
    /// <returns>The exact domain-separated file-source key.</returns>
    /// <exception cref="MediaAbException">A field is invalid.</exception>
    MediaSourceKey ComputeSourceKey(MediaFeatureFileSource value);

    /// <summary>Hashes a bounded executable manifest in canonical UTF-8 ordinal order.</summary>
    /// <param name="entries">Logical executable identifiers, lengths and content hashes.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>The executable-domain digest without installation paths.</returns>
    MediaDigest ComputeExecutableManifestDigest(IReadOnlyList<MediaToolManifestEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>Hashes a bounded dependency manifest in its separate identity domain.</summary>
    /// <param name="entries">Logical dependency identifiers, lengths and content hashes.</param>
    /// <param name="cancellationToken">Cancellation propagated unchanged.</param>
    /// <returns>The dependency-domain digest without installation paths.</returns>
    MediaDigest ComputeDependencyManifestDigest(IReadOnlyList<MediaToolManifestEntry> entries, CancellationToken cancellationToken = default);
}

/// <summary>Bounded incremental SHA-256 encoding with strict UTF-8 and no JSON or host-path input.</summary>
/// <remarks>Tool manifests obey media-ab-capacity-v1 entry and text ceilings before sorting or encoding.</remarks>
public sealed class MediaAbIdentityWriter : IMediaAbIdentityWriter
{
    /// <inheritdoc/>
    public MediaDigest ComputeColorDigest(MediaColorInterpretation value)
    {
        MediaAbRules.ValidateColorShape(value);
        using var writer = new CanonicalHasher("mako.media.color-interpretation.v1");
        writer.UInt32((uint)value.Transfer);
        writer.UInt32((uint)value.Matrix);
        writer.UInt32((uint)value.Range);
        writer.UInt32((uint)value.Alpha);
        writer.UInt32((uint)value.ChromaLocation);
        writer.UInt32((uint)value.NativePixelFormat);
        writer.Boolean(value.FromTrustedMissingTagOverride);
        writer.Digest(value.AuthorityDigest);
        return writer.Finish();
    }

    /// <inheritdoc/>
    public MediaDigest ComputeToolDigest(MediaToolIdentity value)
    {
        MediaAbRules.ValidateToolShape(value);
        using var writer = new CanonicalHasher("mako.media.tool.v1");
        writer.Digest(value.Executables);
        writer.Digest(value.Dependencies);
        writer.Digest(value.NormalizedPipeline);
        return writer.Finish();
    }

    /// <inheritdoc/>
    public MediaDigest ComputeProfileDigest(MediaProfileBundle value)
    {
        MediaAbRules.ValidateProfileShape(value);
        using var writer = new CanonicalHasher("mako.media.profile-bundle.v1");
        writer.String(value.Presentation);
        writer.String(value.DecoderPipeline);
        writer.String(value.Color);
        writer.String(value.Statistics);
        writer.String(value.Quantization);
        writer.String(value.Codec);
        writer.String(value.Query);
        writer.String(value.Gpu);
        writer.Digest(value.DecoderIdentity);
        writer.Digest(value.ColorInterpretation);
        return writer.Finish();
    }

    /// <inheritdoc/>
    public MediaDigest ComputeDescriptorDigest(MediaFilePresentationDescriptor value)
    {
        MediaAbRules.ValidateDescriptorShape(value);
        using var writer = new CanonicalHasher("mako.media.presentation-descriptor.v1");
        writer.Int32(value.StreamIndex);
        writer.Int32(value.EncodedWidth);
        writer.Int32(value.EncodedHeight);
        writer.Int32(value.CanonicalWidth);
        writer.Int32(value.CanonicalHeight);
        writer.Boolean(value.StreamTimeBase.HasValue);
        if (value.StreamTimeBase is { } timeBase)
        {
            writer.Int32(timeBase.Numerator);
            writer.Int32(timeBase.Denominator);
        }

        writer.Int32(value.SarNumerator);
        writer.Int32(value.SarDenominator);
        writer.UInt32((uint)value.Rotation);
        writer.Boolean(value.MirrorHorizontallyBeforeRotation);
        writer.Digest(value.Color.Digest);
        return writer.Finish();
    }

    /// <inheritdoc/>
    public MediaSourceKey ComputeSourceKey(MediaFeatureFileSource value)
    {
        MediaAbRules.ValidateSourceShape(value);
        using var writer = new CanonicalHasher("mako.media.file-source.v1");
        writer.UInt32((uint)value.Kind);
        writer.Digest(value.Content.Sha256);
        writer.Int64(value.Content.ByteLength);
        writer.Digest(value.Presentation.Digest);
        writer.Digest(value.Tool.Digest);
        writer.Digest(value.Profile.Digest);
        return new MediaSourceKey(writer.Finish());
    }

    /// <summary>Hashes a logical executable manifest sorted by exact UTF-8 byte order.</summary>
    /// <param name="entries">Stable read-only logical entry input; installation paths are never accepted.</param>
    /// <param name="cancellationToken">Cancellation checked during copying, sorting and incremental encoding.</param>
    /// <returns>The canonical executable manifest digest.</returns>
    /// <exception cref="MediaAbException">An entry is invalid or logical identifiers repeat.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested; it is never wrapped.</exception>
    public MediaDigest ComputeExecutableManifestDigest(IReadOnlyList<MediaToolManifestEntry> entries, CancellationToken cancellationToken = default) =>
        ComputeManifestDigest(entries, "mako.media.tool-executables.v1", cancellationToken);

    /// <summary>Hashes a logical dependency and LUT manifest sorted by exact UTF-8 byte order.</summary>
    /// <param name="entries">Stable read-only logical entry input covering the actual loaded closure.</param>
    /// <param name="cancellationToken">Cancellation checked during copying, sorting and incremental encoding.</param>
    /// <returns>The canonical dependency manifest digest.</returns>
    /// <exception cref="MediaAbException">An entry is invalid or logical identifiers repeat.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested; it is never wrapped.</exception>
    public MediaDigest ComputeDependencyManifestDigest(IReadOnlyList<MediaToolManifestEntry> entries, CancellationToken cancellationToken = default) =>
        ComputeManifestDigest(entries, "mako.media.tool-dependencies.v1", cancellationToken);

    private static MediaDigest ComputeManifestDigest(IReadOnlyList<MediaToolManifestEntry> entries, string domain, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entries);
        int count = entries.Count;
        if (count < 0 || count > MediaAbCapacityV1.MaximumToolManifestEntries)
        {
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Tool manifest exceeds the fixed entry limit.");
        }

        // The snapshot and sort scratch together have at most 2 * 1024 reference slots.
        // The owning inspector also charges their actual backing and supplied text to its working reservation.
        var ordered = new MediaToolManifestEntry[count];
        int totalUtf8Bytes = 0;
        for (int index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaToolManifestEntry entry = entries[index];
            ArgumentNullException.ThrowIfNull(entry);
            int utf8Bytes = MediaAbRules.ValidateLogicalId(entry.LogicalId, cancellationToken);
            if (utf8Bytes > MediaAbCapacityV1.MaximumToolManifestTextUtf8Bytes - totalUtf8Bytes)
            {
                throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Tool manifest exceeds the fixed total UTF eight text limit.");
            }

            totalUtf8Bytes += utf8Bytes;
            MediaAbRules.ValidateDigest(entry.ContentSha256, MediaAbFailure.ProfileUnsupported);
            if (entry.ByteLength < 0)
            {
                throw new MediaAbException(MediaAbFailure.ProfileUnsupported, "Tool manifest content length must be nonnegative.");
            }

            ordered[index] = entry;
        }

        SortUtf8(ordered, cancellationToken);
        using var writer = new CanonicalHasher(domain);
        writer.UInt32((uint)ordered.Length);
        for (int index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaToolManifestEntry entry = ordered[index];
            if (index != 0 && string.Equals(ordered[index - 1].LogicalId, entry.LogicalId, StringComparison.Ordinal))
            {
                throw new MediaAbException(MediaAbFailure.ProfileUnsupported, "Tool manifest logical identifiers must be unique.");
            }

            writer.String(entry.LogicalId, cancellationToken);
            writer.Int64(entry.ByteLength);
            writer.Digest(entry.ContentSha256);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return writer.Finish();
    }

    private static void SortUtf8(MediaToolManifestEntry[] values, CancellationToken cancellationToken)
    {
        var scratch = new MediaToolManifestEntry[values.Length];
        // Iterative merge sort keeps cancellation outside comparer exception wrappers.
        for (long width = 1; width < values.Length; width *= 2)
        {
            for (long start = 0; start < values.Length; start += 2 * width)
            {
                int left = (int)start;
                int middle = (int)Math.Min(start + width, values.Length);
                int right = middle;
                int end = (int)Math.Min(start + 2 * width, values.Length);
                for (int output = left; output < end; output++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool takeLeft = left < middle && (right >= end || CompareUtf8(values[left].LogicalId, values[right].LogicalId, cancellationToken) <= 0);
                    scratch[output] = takeLeft ? values[left++] : values[right++];
                }
            }

            Array.Copy(scratch, values, values.Length);
        }
    }

    private static int CompareUtf8(string left, string right, CancellationToken cancellationToken)
    {
        // For valid Unicode, UTF-8 byte ordinal order equals Unicode scalar order, unlike UTF-16 ordinal order.
        int leftIndex = 0;
        int rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int leftScalar = char.ConvertToUtf32(left, leftIndex);
            int rightScalar = char.ConvertToUtf32(right, rightIndex);
            int comparison = leftScalar.CompareTo(rightScalar);
            if (comparison != 0)
            {
                return comparison;
            }

            leftIndex += char.IsHighSurrogate(left[leftIndex]) ? 2 : 1;
            rightIndex += char.IsHighSurrogate(right[rightIndex]) ? 2 : 1;
        }

        return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
    }

    private sealed class CanonicalHasher : IDisposable
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        internal CanonicalHasher(string domain)
        {
            // All call sites use literal ASCII domains. String uses the same length-prefixed byte encoding.
            String(domain);
        }

        internal void UInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            _hash.AppendData(bytes);
        }

        internal void Int32(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            _hash.AppendData(bytes);
        }

        internal void Int64(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            _hash.AppendData(bytes);
        }

        internal void Boolean(bool value)
        {
            Span<byte> bytes = stackalloc byte[1];
            bytes[0] = value ? (byte)1 : (byte)0;
            _hash.AppendData(bytes);
        }

        internal void Digest(MediaDigest digest) => _hash.AppendData(Convert.FromHexString(digest.LowerHex));

        internal void String(string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaAbRules.ValidateCanonicalText(value, MediaAbFailure.ProfileUnsupported, cancellationToken);
            long byteCount = 0;
            for (int index = 0; index < value.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                char character = value[index];
                if (char.IsHighSurrogate(character))
                {
                    byteCount += 4;
                    index++;
                }
                else
                {
                    byteCount += character <= 0x7F ? 1 : character <= 0x7FF ? 2 : 3;
                }
            }

            if (byteCount > uint.MaxValue)
            {
                throw new MediaAbException(MediaAbFailure.ProfileUnsupported, "Canonical UTF eight text exceeds the unsigned length range.");
            }

            UInt32((uint)byteCount);
            Encoder encoder = StrictUtf8.GetEncoder();
            ReadOnlySpan<char> remaining = value.AsSpan();
            Span<byte> buffer = stackalloc byte[512];
            bool completed;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                encoder.Convert(remaining, buffer, true, out int charsUsed, out int bytesUsed, out completed);
                _hash.AppendData(buffer[..bytesUsed]);
                remaining = remaining[charsUsed..];
            }
            while (!completed);
        }

        internal MediaDigest Finish() => new(Convert.ToHexStringLower(_hash.GetHashAndReset()));

        public void Dispose() => _hash.Dispose();
    }
}
