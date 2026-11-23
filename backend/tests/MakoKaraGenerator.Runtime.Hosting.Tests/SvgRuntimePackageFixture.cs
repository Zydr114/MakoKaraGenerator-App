using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

// Real package creation uses the production capability gate; no test-only admission bypass.
internal static class SvgRuntimePackageFixture
{
    internal const string InstanceId = "svg-runtime";
    internal const string ExportId = "svg";
    internal const long FailureTimeUs = 750_000;
    internal static bool GpuRequested => string.Equals(Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE"), "1", StringComparison.Ordinal);

    internal static Affine2 Placement(long timeUs)
        => new(100, 20, 25, 80, 40 + timeUs % 400_000 / 25_000d, 20);

    internal static RenderRequest Request(ProjectSnapshot snapshot, long timeUs)
        => RenderRequest.At(snapshot, timeUs) with { TransparentBackground = true };

    internal static ProjectSnapshot Snapshot(EffectPackageContents package, object? parameters = null,
        long surfaceMemoryBytes = 536_870_912, bool enabled = true)
    {
        var effects = new ProjectEffects([package], [new()
        {
            Id = InstanceId, Name = "真实 SVG 控制器", PackageId = package.PackageId, Revision = package.Revision,
            ExportId = ExportId, Seed = 19, Enabled = enabled, Subscription = new("project"),
            Parameters = EffectParameterOverrides.Parse(JsonSerializer.Serialize(parameters ?? new { })),
        }], new() { SurfaceMemoryBytes = surfaceMemoryBytes });
        return ProjectSnapshot.FromProject(Project.Create("真实 SVG Runtime", Composition.Create(320, 180, FrameRate.Fps30,
            outputDurationUs: 603_000_000), LyricsDocument.Create([])) with { Effects = effects });
    }

    internal static EffectPackageContents Package(string kind, bool atlas, string? bodyOverride = null)
    {
        string[] capabilities = atlas ? ["visuals.sprites", "resources.svg.atlas"] : ["visuals.paths", "resources.svg.vector"];
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            ["src/SvgEffect.cs"] = Encoding.UTF8.GetBytes(Source(atlas)),
            ["assets/main.svg"] = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>"
                + (bodyOverride ?? Body(kind)) + "</svg>"),
        };
        if (kind == "image") files.Add("images/role.png", Png(new(240, 120, 40, 255)));
        JsonObject descriptor = new()
        {
            ["svgResourceVersion"] = 1, ["id"] = "drawing", ["source"] = "assets/main.svg",
            ["profile"] = EffectPackageSvgResources.StaticProfile, ["mode"] = atlas ? "atlas" : "vector",
            ["viewport"] = new JsonObject { ["width"] = 100, ["height"] = 100 }, ["fonts"] = new JsonArray(),
            ["images"] = kind == "image" ? new JsonArray("images/role.png") : new JsonArray(),
        };
        if (atlas) descriptor["atlas"] = new JsonObject
        {
            ["variants"] = new JsonArray(new JsonObject { ["id"] = "fixed", ["width"] = 100, ["height"] = 100 }),
            ["paddingPx"] = 4, ["mipmaps"] = true,
        };
        files.Add("resources/drawing.json", Encoding.UTF8.GetBytes(descriptor.ToJsonString()));
        JsonArray entries = [];
        foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.Ordinal)) entries.Add(new JsonObject
        {
            ["path"] = pair.Key, ["role"] = pair.Key.EndsWith(".cs", StringComparison.Ordinal) ? "csharp"
                : pair.Key.EndsWith(".svg", StringComparison.Ordinal) ? "svg"
                : pair.Key.EndsWith(".json", StringComparison.Ordinal) ? "svgResource" : "image",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
        });
        JsonArray parameters = [];
        foreach (var parameter in new[] { ("alpha", 1d, 0d, 1d), ("red", 1d, 0d, 1d), ("failAtUs", -1d, -1d, 602_000_000d) })
            parameters.Add(new JsonObject { ["id"] = parameter.Item1, ["type"] = "number", ["label"] = parameter.Item1,
                ["default"] = parameter.Item2, ["min"] = parameter.Item3, ["max"] = parameter.Item4 });
        JsonObject manifest = new()
        {
            ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion, ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion,
            ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile, ["packageId"] = "mako.svg.runtime", ["name"] = "SVG runtime",
            ["version"] = "2.0.0", ["requires"] = Strings(capabilities), ["lineExit"] = new JsonObject { ["durationUs"] = 0 },
            ["emitters"] = new JsonArray(), ["files"] = entries, ["parameters"] = parameters,
            ["exports"] = new JsonArray(new JsonObject
            {
                ["id"] = ExportId, ["kind"] = "controller", ["name"] = "SVG runtime", ["entryType"] = "SvgEffect",
                ["parameters"] = new JsonArray("alpha", "red", "failAtUs"), ["bindingKinds"] = new JsonArray("project"),
                ["outputs"] = new JsonArray(new JsonObject { ["id"] = "drawing", ["kind"] = "Overlay", ["target"] = "canvas",
                    ["requires"] = Strings(capabilities), ["dependsOn"] = new JsonArray() }),
            }),
        };
        return EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            files.Select(pair => KeyValuePair.Create(pair.Key, (ReadOnlyMemory<byte>)pair.Value)));
    }

    private static JsonArray Strings(IEnumerable<string> values)
        => new([.. values.Select(value => (JsonNode?)JsonValue.Create(value))]);

    private static string Body(string kind) => kind switch
    {
        "path" => "<path d='M10 10 H90 V90 H10 Z' fill='#e04020' stroke='#2040e0' stroke-width='2'/>",
        "overlap" => "<rect width='70' height='100' fill='#c86432'/><rect x='30' width='70' height='100' fill='#c86432'/>",
        "gradient-clip-use" => """
            <style>.ink { fill: url(#paint) }</style><defs>
            <linearGradient id="paint" gradientUnits="objectBoundingBox"><stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient>
            <clipPath id="clip" clipPathUnits="userSpaceOnUse"><circle cx="50" cy="50" r="35"/></clipPath>
            <symbol id="tile" viewBox="0 0 10 10"><rect class="ink" width="10" height="10"/></symbol>
            </defs><g clip-path="url(#clip)" opacity="0.75"><use href="#tile" x="10" y="10" width="80" height="80"/></g>
            """,
        "image" => "<image href='images/role.png' x='10' y='10' width='40' height='80' preserveAspectRatio='none'/>"
            + "<image href='data:image/png;base64," + Convert.ToBase64String(Png(new(20, 180, 80, 255)))
            + "' x='50' y='10' width='40' height='80' preserveAspectRatio='none'/>",
        "filter" => """
            <defs><filter id="f" filterUnits="userSpaceOnUse" primitiveUnits="userSpaceOnUse" x="0" y="0" width="100" height="100">
            <feGaussianBlur in="SourceGraphic" stdDeviation="3" result="blur"/><feMerge><feMergeNode in="blur"/><feMergeNode in="SourceGraphic"/></feMerge>
            </filter></defs><rect x="25" y="25" width="50" height="50" fill="#2070e0" filter="url(#f)"/>
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static byte[] Png(SKColor color)
    {
        using var bitmap = new SKBitmap(2, 2); bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }

    private static string Source(bool atlas) => """
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed record SvgData(RESOURCE_TYPE Resource, double FailAtUs, ColorRgba Tint) : PreparedEffect;
        public sealed class SvgEffect : IPureKaraokeEffect, IPureVisualEffect
        {
            public PreparedEffect Declare(DeclareContext source) => new SvgData(LOOKUP,
                source.Parameters.GetNumber("failAtUs"), new ColorRgba(source.Parameters.GetNumber("red"), 1, 1, source.Parameters.GetNumber("alpha")))
            {
                VisualOutputs = [new(new("drawing"), PRIMITIVE, OverlayLayer.AboveText, RESOURCE_KIND)]
            };
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
            {
                var d = (SvgData)data;
                double width = frame.TimeUs == d.FailAtUs ? 1e100 : 100;
                var matrix = new Affine2(width, 20, 25, 80, 40 + frame.TimeUs % 400000 / 25000.0, 20);
                DRAW;
            }
        }
        """.Replace("RESOURCE_TYPE", atlas ? "SpriteHandle" : "VectorHandle", StringComparison.Ordinal)
            .Replace("LOOKUP", atlas ? "source.Resources.GetSprite(\"drawing\", \"fixed\")" : "source.Resources.GetVector(\"drawing\")", StringComparison.Ordinal)
            .Replace("PRIMITIVE", atlas ? "VisualPrimitiveKind.Sprites" : "VisualPrimitiveKind.Paths", StringComparison.Ordinal)
            .Replace("RESOURCE_KIND", atlas ? "VisualResourceKind.SvgAtlas" : "VisualResourceKind.SvgVector", StringComparison.Ordinal)
            .Replace("DRAW", atlas ? "output.DrawAtlasSprites(new(\"drawing\"), [new(d.Resource, matrix, d.Tint)], OverlayLayer.AboveText)"
                : "output.DrawPaths(new(\"drawing\"), [new(d.Resource, matrix, d.Tint)], OverlayLayer.AboveText)", StringComparison.Ordinal);
}
