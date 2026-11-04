using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Rendering;

// This wire contains source values only, never persistence paths or runtime handles.
internal static class SnapshotEffectSourceCodec
{
    private const int MaximumBytes = PackageProjectSnapshotCodec.MaximumBytes;
    private const int MaximumValues = PackageProjectSnapshotCodec.MaximumSourceValues;
    private static readonly EffectPackageContentBudget Budget = new()
    { MaximumBytes = MaximumBytes, MaximumFileBytes = MaximumBytes, MaximumFiles = MaximumValues };

    internal static int ValidateBudget(ProjectEffects effects)
    {
        long text = 0, binaryWire = 0;
        int values = 0;
        foreach (EffectPackageContents package in effects.Packages)
        {
            if (!EffectPackageProfiles.IsSupported(package.Profile))
                throw new SnapshotContractException("SnapshotPackageInvalid", "快照固定包能力版本不受支持。");
            Count(); Text(package.PackageId); Text(package.Revision); Binary(package.Manifest.Length);
            foreach (EffectPackageFile file in package.Files)
            { Count(); Text(file.Path); Binary(file.Content.Length); }
        }
        foreach (ProjectEffectInstance instance in effects.Instances)
        {
            Count(); Text(instance.Id); Text(instance.Name); Text(instance.PackageId); Text(instance.Revision); Text(instance.ExportId);
            if (instance.Binding is null || instance.Subscription is null || instance.Parameters is null) throw Invalid();
            Text(instance.Binding.Id); Text(instance.Subscription.Kind); Text(instance.Subscription.BranchId);
            foreach (string id in instance.Binding.Ids) { Count(); Text(id); }
            Text(instance.Parameters.Json);
            if (instance.TemplateOrigin is { } origin)
            { Text(origin.TemplateId); Text(origin.RecipeId); Text(origin.OwnerId); }
        }
        void Count()
        {
            if (++values > MaximumValues) throw TooLarge();
        }
        void Text(string? value)
        {
            text += value?.Length ?? 0;
            if (text + binaryWire > MaximumBytes) throw TooLarge();
        }
        void Binary(int length)
        {
            binaryWire += ((long)length + 2) / 3 * 4;
            if (text + binaryWire > MaximumBytes) throw TooLarge();
        }
        return values;
    }

    internal static int ValidateGraph(ProjectEffects effects, JsonSerializerOptions options)
    {
        ValidateBudget(effects);
        // Traverse the complete DTO shape with empty binary placeholders before any base64 copies.
        Effects source = new([.. effects.Packages.Select(package => new Package(package.PackageId, package.Revision, "", [.. package.Files.Select(file => new FileSource(file.Path, ""))]))],
            Instances(effects), Limits.From(effects.Budgets));
        return SnapshotJsonContract.ValidateSourceGraph(source, options, MaximumValues, MaximumBytes, packageMaterials: true);
    }

    internal static JsonElement Encode(ProjectEffects effects, JsonSerializerOptions options)
    {
        ValidateBudget(effects);
        var source = new Effects([.. effects.Packages.Select(package => new Package(package.PackageId, package.Revision,
            Convert.ToBase64String(package.Manifest.AsSpan()), [.. package.Files.Select(file =>
                new FileSource(file.Path, Convert.ToBase64String(file.Content.AsSpan())))]))], Instances(effects), Limits.From(effects.Budgets));
        using var stream = new StaticProjectSnapshotCodec.BoundedSnapshotStream(MaximumBytes);
        JsonSerializer.Serialize(stream, source, options);
        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    internal static JsonElement EncodeExternal(ProjectEffects effects, JsonSerializerOptions options)
        => JsonSerializer.SerializeToElement(new ExternalEffects([.. effects.Packages.Select(package => new PackageReference(package.PackageId, package.Revision))],
            Instances(effects), Limits.From(effects.Budgets)), options);

    internal static ProjectEffects DecodeExternal(JsonElement? value, JsonSerializerOptions options,
        IReadOnlyDictionary<(string, string), EffectPackageContents> packages)
    {
        if (value is null) throw Invalid();
        var source = value.Value.Deserialize<ExternalEffects>(options) ?? throw Invalid();
        if (source.Packages.Length != packages.Count) throw Invalid();
        return new(source.Packages.Select(reference => packages.TryGetValue((reference.PackageId, reference.Revision), out var package)
            ? package : throw Invalid()), source.Instances.Select(instance => instance.ToDomain()), source.Budgets.ToDomain());
    }

    internal static ProjectEffects Decode(JsonElement? value, JsonSerializerOptions options)
    {
        try
        {
            if (value is null) throw Invalid();
            Effects source = value.Value.Deserialize<Effects>(options) ?? throw Invalid();
            long bytes = 0;
            int files = 0;
            // Validate every encoded length and cumulative budget before allocating binary buffers.
            foreach (Package package in source.Packages)
            {
                if (package is null || package.Files is null) throw Invalid();
                Claim(package.Manifest);
                foreach (FileSource file in package.Files)
                { if (file is null) throw Invalid(); Claim(file.Content); }
            }
            List<EffectPackageContents> packages = [];
            foreach (Package package in source.Packages)
            {
                byte[] manifest = Convert.FromBase64String(package.Manifest);
                EffectPackageContents contents = EffectPackageContents.CreateSupported(manifest,
                    package.Files.Select(file => new KeyValuePair<string, ReadOnlyMemory<byte>>(file.Path, Convert.FromBase64String(file.Content))),
                    Budget);
                if (contents.PackageId != package.PackageId || contents.Revision != package.Revision
                    || !manifest.AsSpan().SequenceEqual(contents.Manifest.AsSpan())) throw Invalid();
                packages.Add(contents);
            }
            return new(packages, source.Instances.Select(instance => instance.ToDomain()), source.Budgets.ToDomain());

            void Claim(string encoded)
            {
                int length = DecodedLength(encoded);
                bytes += length;
                if (++files > MaximumValues || bytes > MaximumBytes) throw TooLarge();
            }
        }
        catch (SnapshotContractException) { throw; }
        catch (Exception error) when (error is EffectPackageException or JsonException or ArgumentException
            or InvalidOperationException or NullReferenceException or KeyNotFoundException or OverflowException)
        { throw Invalid(error); }
    }

    private static Instance[] Instances(ProjectEffects effects) => [.. effects.Instances.Select(instance => new Instance(
        instance.Id, instance.Name, instance.Enabled, instance.PackageId, instance.Revision, instance.ExportId,
        new(instance.Binding.Kind, instance.Binding.Id, [.. instance.Binding.Ids], instance.Binding.IncludeDescendants),
        new(instance.Subscription.Kind, instance.Subscription.BranchId), instance.Seed, Parameters(instance.Parameters),
        instance.TemplateOrigin is { } origin ? new(origin.TemplateId, origin.RecipeId, origin.OwnerKind, origin.OwnerId) : null,
        instance.OverlayLayer))];

    private static JsonElement Parameters(EffectParameterOverrides parameters)
    {
        using JsonDocument document = JsonDocument.Parse(parameters.Json);
        return document.RootElement.Clone();
    }

    private static int DecodedLength(string encoded)
    {
        if (encoded is null || encoded.Length % 4 != 0) throw Invalid();
        if (encoded.Length == 0) return 0;
        int padding = encoded[^1] == '=' ? encoded[^2] == '=' ? 2 : 1 : 0;
        int contentLength = encoded.Length - padding;
        for (int index = 0; index < contentLength; index++)
            if (Digit(encoded[index]) < 0) throw Invalid();
        for (int index = contentLength; index < encoded.Length; index++)
            if (encoded[index] != '=') throw Invalid();
        if (padding == 2 && (Digit(encoded[contentLength - 1]) & 15) != 0
            || padding == 1 && (Digit(encoded[contentLength - 1]) & 3) != 0) throw Invalid();
        long length = (long)encoded.Length / 4 * 3 - padding;
        if (length > MaximumBytes) throw TooLarge();
        return (int)length;
    }

    private static int Digit(char value) => value switch
    { >= 'A' and <= 'Z' => value - 'A', >= 'a' and <= 'z' => value - 'a' + 26, >= '0' and <= '9' => value - '0' + 52, '+' => 62, '/' => 63, _ => -1 };
    private static SnapshotContractException Invalid(Exception? error = null) => new("SnapshotPackageInvalid", "快照固定包闭包、身份或实例结构无效。", error);
    private static SnapshotContractException TooLarge() => new("SnapshotSizeExceeded", "快照包源超过固定预算。");

    private sealed record Effects(Package[] Packages, Instance[] Instances, Limits Budgets);
    private sealed record ExternalEffects(PackageReference[] Packages, Instance[] Instances, Limits Budgets);
    private sealed record PackageReference(string PackageId, string Revision);
    private sealed record Package(string PackageId, string Revision, string Manifest, FileSource[] Files);
    private sealed record FileSource(string Path, string Content);
    private sealed record Binding(ProjectEffectBindingKind Kind, string? Id, string[] Ids, bool IncludeDescendants)
    { internal ProjectEffectBinding ToDomain() => new(Kind, Id, Ids, IncludeDescendants); }
    private sealed record Subscription(string Kind, string? BranchId)
    { internal ProjectEffectSubscription ToDomain() => new(Kind, BranchId); }
    private sealed record Origin(string TemplateId, string RecipeId, StyleTemplateKind OwnerKind, string OwnerId)
    { internal ProjectEffectTemplateOrigin ToDomain() => new(TemplateId, RecipeId, OwnerKind, OwnerId); }
    private sealed record Instance(string Id, string Name, bool Enabled, string PackageId, string Revision, string ExportId,
        Binding Binding, Subscription Subscription, ulong Seed, JsonElement Parameters, Origin? TemplateOrigin, EffectOverlayLayer OverlayLayer)
    {
        internal ProjectEffectInstance ToDomain() => new()
        {
            Id = Id, Name = Name, Enabled = Enabled, PackageId = PackageId, Revision = Revision, ExportId = ExportId,
            Binding = Binding.ToDomain(), Subscription = Subscription.ToDomain(), Seed = Seed,
            Parameters = EffectParameterOverrides.Parse(Parameters.GetRawText()), TemplateOrigin = TemplateOrigin?.ToDomain(), OverlayLayer = OverlayLayer,
        };
    }
    private sealed record Limits(long SurfaceMemoryBytes, int LiveParticles, int TrailSamples, int EmissionRecords, int DrawCommands)
    {
        internal static Limits From(ProjectEffectBudgets value) => new(value.SurfaceMemoryBytes, value.LiveParticles, value.TrailSamples, value.EmissionRecords, value.DrawCommands);
        internal ProjectEffectBudgets ToDomain() => new()
        { SurfaceMemoryBytes = SurfaceMemoryBytes, LiveParticles = LiveParticles, TrailSamples = TrailSamples, EmissionRecords = EmissionRecords, DrawCommands = DrawCommands };
    }
}
