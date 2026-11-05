using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Core.Effects;

// Normative encoding only; no parser, query evaluation, source lookup or cache lives here.
internal static class MediaDependencyEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static IComparer<string> Utf8Comparer { get; } = Comparer<string>.Create(static (left, right) =>
        Utf8(left!).AsSpan().SequenceCompareTo(Utf8(right!)));

    internal static void ValidateOwner(ProjectMediaDependencyOwner owner)
    {
        _ = Utf8(owner.Path); _ = Utf8(owner.Region); _ = Utf8(owner.PackageId); _ = Utf8(owner.Revision); _ = Utf8(owner.ExportId);
    }

    internal static MediaDigest Parameters(ImmutableDictionary<string, EffectParameterValue> parameters, CancellationToken token)
    {
        using var writer = new Hasher("mako.media.owner-parameters.v1", token);
        writer.Count(parameters.Count);
        foreach (var (id, value) in parameters.OrderBy(pair => pair.Key, Utf8Comparer))
        {
            writer.Text(id);
            switch (value)
            {
                case EffectParameterValue.Number number: writer.UInt32(0); writer.Double(number.Value); break;
                case EffectParameterValue.Whole whole: writer.UInt32(1); writer.Int64(whole.Value); break;
                case EffectParameterValue.Toggle toggle: writer.UInt32(2); writer.Boolean(toggle.Value); break;
                case EffectParameterValue.Choice choice: writer.UInt32(3); writer.Text(choice.Value); break;
                case EffectParameterValue.Duration duration: writer.UInt32(4); writer.Int64(duration.Value); break;
                case EffectParameterValue.Text text: writer.UInt32(5); writer.Text(text.Value); break;
                case EffectParameterValue.Color color:
                    writer.UInt32(6); writer.Double(color.R); writer.Double(color.G); writer.Double(color.B); writer.Double(color.A); break;
                case EffectParameterValue.Curve curve:
                    writer.UInt32(7); writer.Text(curve.Value.Kind); writer.Boolean(curve.Value.Easing is not null);
                    if (curve.Value.Easing is { } easing) writer.Text(easing);
                    writer.Count(curve.Value.Points.Length);
                    foreach (var point in curve.Value.Points) { writer.Double(point.X); writer.Double(point.Y); }
                    break;
                case EffectParameterValue.Asset asset: writer.UInt32(8); writer.Text(asset.Path); break;
                default: throw new EffectPackageException("PackageMediaParameterTypeUnsupported", "parameters." + id, "媒体依赖参数类型不在封闭目录内。");
            }
        }
        return writer.Finish();
    }

    internal static MediaDigest QueryCatalog(ImmutableArray<ProjectMediaDependency> dependencies, CancellationToken token)
    {
        using var writer = new Hasher("mako.media.query-catalog.v1", token);
        writer.Text(MediaProfilesV1.Query); writer.Count(dependencies.Length);
        foreach (var dependency in dependencies)
        {
            writer.Owner(dependency.Owner); writer.Text(dependency.Query.Id);
            writer.UInt32((uint)dependency.Query.Space); writer.Int64(dependency.WindowUs);
        }
        return writer.Finish();
    }

    internal static MediaDigest DependencyPlan(ImmutableArray<ProjectMediaDependencyOwner> owners,
        ImmutableArray<ProjectMediaDependency> dependencies, MediaDigest queryDigest, CancellationToken token)
    {
        using var writer = new Hasher("mako.media.dependency-plan.v1", token);
        writer.Digest(queryDigest); writer.Count(owners.Length);
        foreach (var owner in owners)
        {
            writer.Owner(owner); writer.Digest(owner.ParametersDigest);
            var references = dependencies.Where(dependency => ReferenceEquals(dependency.Owner, owner)).ToArray();
            writer.Count(references.Length);
            foreach (var dependency in references)
            {
                writer.Text(dependency.Query.Id); writer.Fallback(dependency.Reference.Pending);
                writer.Fallback(dependency.Reference.Unavailable); writer.Fallback(dependency.Reference.NoMedia);
            }
        }
        return writer.Finish();
    }

    private static byte[] Utf8(string value)
    {
        try { return StrictUtf8.GetBytes(value); }
        catch (EncoderFallbackException error)
        { throw new EffectPackageException("PackageMediaIdentityTextInvalid", "mediaQueries.identity", "媒体依赖身份包含非法 UTF 十六文本。", error); }
    }

    private sealed class Hasher : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly CancellationToken _token;
        internal Hasher(string domain, CancellationToken token) { _token = token; Text(domain); }
        internal void Count(int count) => UInt32(checked((uint)count));
        internal void UInt32(uint value)
        {
            _token.ThrowIfCancellationRequested(); Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); _hash.AppendData(bytes);
        }
        internal void Int64(long value)
        {
            _token.ThrowIfCancellationRequested(); Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value); _hash.AppendData(bytes);
        }
        internal void Double(double value) => Int64(BitConverter.DoubleToInt64Bits(value));
        internal void Boolean(bool value)
        { _token.ThrowIfCancellationRequested(); Span<byte> bytes = stackalloc byte[1]; bytes[0] = value ? (byte)1 : (byte)0; _hash.AppendData(bytes); }
        internal void Text(string value) { byte[] bytes = Utf8(value); Count(bytes.Length); _hash.AppendData(bytes); }
        internal void Digest(MediaDigest digest) { _token.ThrowIfCancellationRequested(); _hash.AppendData(Convert.FromHexString(digest.LowerHex)); }
        internal void Owner(ProjectMediaDependencyOwner owner)
        {
            UInt32((uint)owner.Kind); Text(owner.Path); Text(owner.Region); Text(owner.PackageId); Text(owner.Revision); Text(owner.ExportId);
        }
        internal void Fallback(EffectMediaFallbackReference fallback)
        {
            UInt32(fallback is EffectMediaFallbackReference.AuthorBranch ? 0u : 1u); Count(fallback.OutputIds.Length);
            foreach (string output in fallback.OutputIds) Text(output);
        }
        internal MediaDigest Finish() { _token.ThrowIfCancellationRequested(); return new(Convert.ToHexStringLower(_hash.GetHashAndReset())); }
        public void Dispose() => _hash.Dispose();
    }
}
