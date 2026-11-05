using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Tests.Effects;

// Independent buffered BinaryWriter oracle. It never calls the production dependency encoder.
internal static class MediaDependencyOracle
{
    internal static string Parameters(ProjectMediaDependencyOwner owner) => Hash(writer =>
    {
        Text(writer, "mako.media.owner-parameters.v1"); writer.Write((uint)owner.Parameters.Count);
        foreach (var (id, value) in owner.Parameters.OrderBy(pair => Convert.ToHexString(Encoding.UTF8.GetBytes(pair.Key)), StringComparer.Ordinal))
        {
            Text(writer, id);
            switch (value)
            {
                case EffectParameterValue.Number n: writer.Write(0u); writer.Write(n.Value); break;
                case EffectParameterValue.Whole n: writer.Write(1u); writer.Write(n.Value); break;
                case EffectParameterValue.Toggle n: writer.Write(2u); writer.Write(n.Value); break;
                case EffectParameterValue.Choice n: writer.Write(3u); Text(writer, n.Value); break;
                case EffectParameterValue.Duration n: writer.Write(4u); writer.Write(n.Value); break;
                case EffectParameterValue.Text n: writer.Write(5u); Text(writer, n.Value); break;
                case EffectParameterValue.Color n: writer.Write(6u); writer.Write(n.R); writer.Write(n.G); writer.Write(n.B); writer.Write(n.A); break;
                case EffectParameterValue.Curve n:
                    writer.Write(7u); Text(writer, n.Value.Kind); writer.Write(n.Value.Easing is not null);
                    if (n.Value.Easing is { } easing) Text(writer, easing);
                    writer.Write((uint)n.Value.Points.Length); foreach (var point in n.Value.Points) { writer.Write(point.X); writer.Write(point.Y); }
                    break;
                case EffectParameterValue.Asset n: writer.Write(8u); Text(writer, n.Path); break;
                default: throw new InvalidOperationException("Unknown independent parameter type.");
            }
        }
    });
    internal static string Catalog(ProjectMediaDependencyPlan plan) => Hash(writer =>
    {
        Text(writer, "mako.media.query-catalog.v1"); Text(writer, "media-grid-area-binary64-v1"); writer.Write((uint)plan.Dependencies.Length);
        foreach (var dependency in plan.Dependencies)
        { Owner(writer, dependency.Owner); Text(writer, dependency.Query.Id); writer.Write((uint)dependency.Query.Space); writer.Write(dependency.WindowUs); }
    });
    internal static string Plan(ProjectMediaDependencyPlan plan) => Hash(writer =>
    {
        Text(writer, "mako.media.dependency-plan.v1"); writer.Write(Convert.FromHexString(plan.QueryCatalogDigest.LowerHex)); writer.Write((uint)plan.Owners.Length);
        foreach (var owner in plan.Owners)
        {
            Owner(writer, owner); writer.Write(Convert.FromHexString(owner.ParametersDigest.LowerHex));
            var dependencies = plan.Dependencies.Where(dependency => ReferenceEquals(dependency.Owner, owner)).ToArray();
            writer.Write((uint)dependencies.Length);
            foreach (var dependency in dependencies)
            { Text(writer, dependency.Query.Id); Fallback(writer, dependency.Reference.Pending); Fallback(writer, dependency.Reference.Unavailable); Fallback(writer, dependency.Reference.NoMedia); }
        }
    });
    private static void Owner(BinaryWriter writer, ProjectMediaDependencyOwner owner)
    {
        writer.Write((uint)owner.Kind); Text(writer, owner.Path); Text(writer, owner.Region); Text(writer, owner.PackageId); Text(writer, owner.Revision); Text(writer, owner.ExportId);
    }
    private static void Fallback(BinaryWriter writer, EffectMediaFallbackReference value)
    {
        writer.Write(value is EffectMediaFallbackReference.AuthorBranch ? 0u : 1u); writer.Write((uint)value.OutputIds.Length);
        foreach (string id in value.OutputIds) Text(writer, id);
    }
    private static void Text(BinaryWriter writer, string value)
    { byte[] bytes = new UTF8Encoding(false, true).GetBytes(value); writer.Write((uint)bytes.Length); writer.Write(bytes); }
    private static string Hash(Action<BinaryWriter> encode)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true), leaveOpen: true)) encode(writer);
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }
}
