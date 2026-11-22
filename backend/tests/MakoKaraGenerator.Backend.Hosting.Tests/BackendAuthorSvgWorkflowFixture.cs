using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

// Own real package fixture: no references to another test assembly or capability bypass.
internal sealed class BackendAuthorSvgWorkflowFixture : IAsyncDisposable
{
    internal const string SvgPath = "assets/main.svg";
    internal const string DescriptorPath = "resources/drawing.json";
    internal const string BinaryPath = "assets/keep.bin";
    internal const string GoodSvg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><rect x='10' y='10' width='80' height='80' fill='#2060e0'/></svg>";
    internal const string BadSvg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><use href='#missing'/></svg>";
    internal static readonly byte[] BinaryBytes = [0, 128, 255, 10, 0, 42];
    private readonly TemporaryDirectory _directory;
    private readonly string _draftId;
    internal ProjectSessionRegistry Registry { get; }

    private BackendAuthorSvgWorkflowFixture(TemporaryDirectory directory, ProjectSessionRegistry registry, string draftId)
    { _directory = directory; Registry = registry; _draftId = draftId; }

    internal async Task<AuthorDraftResponse> ReadAsync()
        => Accepted(await Registry.ReadAuthorDraftAsync("client", _draftId));

    internal RequestMetadataDto Metadata()
        => new(Guid.NewGuid().ToString("N"), Registry.HostInstanceId, Registry.ServerTimeUtc.AddMinutes(1));

    internal async Task<AuthorPackageFilesResponse> ReadFilesAsync(string? sourceRevision = null)
    {
        AuthorDraftResponse draft = await ReadAsync();
        return Accepted(await Registry.ReadAuthorPackageFilesAsync("client", _draftId, draft.DraftRevision,
            sourceRevision ?? draft.CurrentSourceRevision));
    }

    internal Task<BackendOperationResult<AuthorDraftResponse>> SaveFilesAsync(AuthorPackageFilesResponse files,
        Func<AuthorPackageFileDto, string> transform, string? omitPath = null)
        => Registry.SaveAuthorPackageFilesAsync("client", _draftId,
            new(files.DraftRevision, files.ManifestJson,
                [.. files.Files.Where(file => file.Editable && file.Path != omitPath)
                    .Select(file => new AuthorPackageTextFileDto(file.Path, transform(file)))], Metadata: Metadata()));

    internal async Task<EffectPackageContents> DownloadAsync()
    {
        AuthorDraftResponse draft = await ReadAsync();
        using EffectPackageDownloadLease lease = Accepted(await Registry.OpenAuthorPackageAsync("client", _draftId,
            draft.DraftRevision, draft.CurrentSourceRevision));
        string path = _directory.FilePath("download-" + Guid.NewGuid().ToString("N") + ".mkfx");
        await using (FileStream output = File.Create(path)) await lease.Stream.CopyToAsync(output);
        return await EffectPackageTransfer.ReadArchiveAsync(path, EffectPackageProfiles.V2);
    }

    internal static T Accepted<T>(BackendOperationResult<T> result)
    {
        Assert.True(result.Accepted, result.Error?.ToString());
        return result.Response!;
    }

    internal static async Task<BackendAuthorSvgWorkflowFixture> CreateAsync(bool atlas)
    {
        TemporaryDirectory directory = new();
        ProjectSessionRegistry? registry = null;
        try
        {
            EffectPackageContents package = Package(atlas);
            string archive = directory.FilePath("author-svg.mkfx");
            await EffectPackageTransfer.WriteArchiveAsync(archive, package);
            ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
            await using FileStream stream = File.OpenRead(archive);
            StoredAsset asset = await resources.UploadAsync("client", "author-svg.mkfx", "application/octet-stream", stream);
            registry = new("author-svg-workflow", resources, NewProjectPreparationService.CreateDefault());
            registry.ConfigurePersistence(new() { StateRoot = directory.FilePath("state") });
            registry.ConfigureAuthorWorkflows(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
            RequestMetadataDto metadata = new(Guid.NewGuid().ToString("N"), registry.HostInstanceId, registry.ServerTimeUtc.AddMinutes(1));
            AuthorDraftResponse draft = Accepted(await registry.CreateAuthorDraftAsync("client", new(asset.AssetId, "svg", "SVG author", metadata)));
            return new(directory, registry, draft.DraftId);
        }
        catch
        {
            if (registry is not null) await registry.DisposeAsync();
            directory.Dispose();
            throw;
        }
    }

    private static EffectPackageContents Package(bool atlas)
    {
        string[] capabilities = atlas ? ["visuals.sprites", "resources.svg.atlas"] : ["visuals.paths", "resources.svg.vector"];
        JsonObject descriptor = new()
        {
            ["svgResourceVersion"] = 1, ["id"] = "drawing", ["source"] = SvgPath,
            ["profile"] = EffectPackageSvgResources.StaticProfile, ["mode"] = atlas ? "atlas" : "vector",
            ["viewport"] = new JsonObject { ["width"] = 100, ["height"] = 100 },
            ["fonts"] = new JsonArray(), ["images"] = new JsonArray(),
        };
        if (atlas) descriptor["atlas"] = new JsonObject
        {
            ["variants"] = new JsonArray(new JsonObject { ["id"] = "fixed", ["width"] = 100, ["height"] = 100 }),
            ["paddingPx"] = 4, ["mipmaps"] = true,
        };
        Dictionary<string, (string Role, byte[] Bytes)> files = new(StringComparer.Ordinal)
        {
            ["src/SvgEffect.cs"] = ("csharp", Encoding.UTF8.GetBytes(Source(atlas))),
            [SvgPath] = ("svg", Encoding.UTF8.GetBytes(GoodSvg)),
            [DescriptorPath] = ("svgResource", Encoding.UTF8.GetBytes(descriptor.ToJsonString())),
            [BinaryPath] = ("asset", BinaryBytes.ToArray()),
        };
        JsonArray entries = [];
        foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.Ordinal)) entries.Add(new JsonObject
        {
            ["path"] = pair.Key, ["role"] = pair.Value.Role,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(pair.Value.Bytes)),
        });
        JsonObject manifest = new()
        {
            ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion, ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion,
            ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile, ["packageId"] = "mako.svg.author-workflow",
            ["name"] = "SVG author workflow", ["version"] = "2.0.0", ["requires"] = Strings(capabilities),
            ["lineExit"] = new JsonObject { ["durationUs"] = 0 }, ["emitters"] = new JsonArray(),
            ["files"] = entries, ["parameters"] = new JsonArray(),
            ["exports"] = new JsonArray(new JsonObject
            {
                ["id"] = "svg", ["kind"] = "controller", ["name"] = "SVG author", ["entryType"] = "SvgEffect",
                ["parameters"] = new JsonArray(), ["bindingKinds"] = new JsonArray("project"),
                ["outputs"] = new JsonArray(new JsonObject { ["id"] = "drawing", ["kind"] = "Overlay", ["target"] = "canvas",
                    ["requires"] = Strings(capabilities), ["dependsOn"] = new JsonArray() }),
            }),
        };
        return EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            files.Select(pair => KeyValuePair.Create(pair.Key, (ReadOnlyMemory<byte>)pair.Value.Bytes)));
    }

    private static JsonArray Strings(IEnumerable<string> values)
        => new([.. values.Select(value => (JsonNode?)JsonValue.Create(value))]);

    private static string Source(bool atlas) => """
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed record SvgData(RESOURCE_TYPE Resource) : PreparedEffect;
        public sealed class SvgEffect : IPureKaraokeEffect, IPureVisualEffect
        {
            public PreparedEffect Declare(DeclareContext source) => new SvgData(LOOKUP)
            {
                VisualOutputs = [new(new("drawing"), PRIMITIVE, OverlayLayer.AboveText, RESOURCE_KIND)]
            };
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
            {
                var d = (SvgData)data;
                var matrix = new Affine2(100, 0, 0, 100, 30, 20);
                DRAW;
            }
        }
        """.Replace("RESOURCE_TYPE", atlas ? "SpriteHandle" : "VectorHandle", StringComparison.Ordinal)
            .Replace("LOOKUP", atlas ? "source.Resources.GetSprite(\"drawing\", \"fixed\")" : "source.Resources.GetVector(\"drawing\")", StringComparison.Ordinal)
            .Replace("PRIMITIVE", atlas ? "VisualPrimitiveKind.Sprites" : "VisualPrimitiveKind.Paths", StringComparison.Ordinal)
            .Replace("RESOURCE_KIND", atlas ? "VisualResourceKind.SvgAtlas" : "VisualResourceKind.SvgVector", StringComparison.Ordinal)
            .Replace("DRAW", atlas ? "output.DrawAtlasSprites(new(\"drawing\"), [new(d.Resource, matrix, new(1, 1, 1, 1))], OverlayLayer.AboveText)"
                : "output.DrawPaths(new(\"drawing\"), [new(d.Resource, matrix, new(1, 1, 1, 1))], OverlayLayer.AboveText)", StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        await Registry.DisposeAsync();
        _directory.Dispose();
    }
}
