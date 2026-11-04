using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting.Authoring;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Runtime.Hosting.Tests;

public sealed class PackageAuthorResourceValidatorTests
{
    [Fact]
    public async Task 作者SVG静态门禁验证没有任何控制器引用的完整包并可重复执行()
    {
        var package = Package();
        Assert.Equal(2, EffectPackageSvgResources.Read(package).ById.Count);
        await PackageAuthorResourceValidator.ValidateSvgAsync(package);
        await PackageAuthorResourceValidator.ValidateSvgAsync(package);
    }

    [Fact]
    public async Task 未引用坏SVG使作者门禁失败且后续有效包恢复()
    {
        var broken = Package(unusedBody: "<defs><path d='M0 0 Lbad'/></defs>");
        var failure = await Assert.ThrowsAsync<EffectPackageException>(() => PackageAuthorResourceValidator.ValidateSvgAsync(broken));
        Assert.Equal("PackageSvgSyntaxInvalid", failure.Code); Assert.Equal("assets/unused.svg", failure.Path);
        await PackageAuthorResourceValidator.ValidateSvgAsync(Package());
    }

    [Fact]
    public async Task 白名单中未使用的坏图片也须完整解码且失败后不保留坏状态()
    {
        var broken = Package(unusedImage: [1, 2, 3]);
        var failure = await Assert.ThrowsAsync<EffectPackageException>(() => PackageAuthorResourceValidator.ValidateSvgAsync(broken));
        Assert.Equal("PackageSvgImageDecodeFailed", failure.Code); Assert.Equal("assets/unused.svg", failure.Path);
        await PackageAuthorResourceValidator.ValidateSvgAsync(Package());
    }

    [Fact]
    public async Task 无SVG包不调用SVG门禁且保持旧shader在执行边界验证()
    {
        const string badShader = "half4 main(float2 p) { return missing; }";
        var package = EffectPackageShaderFixture.Contents(source: badShader);
        Assert.NotEmpty(EffectPackageDefinition.Read(package).ShaderPrograms);
        var validator = new Probe((_, _, _) => throw new InvalidOperationException("No SVG validation expected."));
        await PackageAuthorResourceValidator.ValidateSvgAsync(package, validator);
        await PackageAuthorResourceValidator.ValidateSvgAsync(package);
        Assert.Equal(0, validator.Calls);
        Assert.Equal(badShader, package.Files.Single(file => file.Path == EffectPackageShaderFixture.SourcePath).ReadUtf8());
    }

    [Fact]
    public async Task 预取消在解析和验证器调用前传播且新请求能恢复()
    {
        var package = Package();
        var validator = new Probe((_, _, _) => throw new InvalidOperationException("Cancelled validation must not start."));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PackageAuthorResourceValidator.ValidateSvgAsync(package, validator, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken); Assert.Equal(0, validator.Calls);
        await PackageAuthorResourceValidator.ValidateSvgAsync(package);
    }

    [Fact]
    public async Task 验证器返回时的取消先于inspection发布且随后有效请求恢复()
    {
        var package = Package();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var validator = new Probe((actual, resources, token) =>
        {
            Assert.Same(package, actual); Assert.Equal(2, resources.ById.Count); Assert.Equal(cancellation.Token, token);
            entered.TrySetResult(true);
            if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test cancellation was not delivered.");
            return null!;
        });
        var pending = PackageAuthorResourceValidator.ValidateSvgAsync(package, validator, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(cancellation.Token, failure.CancellationToken); Assert.Equal(1, validator.Calls);
        }
        finally { cancellation.Cancel(); }
        await PackageAuthorResourceValidator.ValidateSvgAsync(package);
    }

    private static EffectPackageContents Package(string unusedBody = "<circle cx='10' cy='10' r='2'/>", byte[]? unusedImage = null)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        if (unusedImage is not null) files.Add("assets/unused.bin", unusedImage);
        foreach (string id in new[] { "main", "unused" })
        {
            string body = id == "main" ? "<rect width='10' height='10'/>" : unusedBody;
            files.Add("assets/" + id + ".svg", Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" + body + "</svg>"));
            JsonObject descriptor = new()
            {
                ["svgResourceVersion"] = 1, ["id"] = id, ["source"] = "assets/" + id + ".svg",
                ["profile"] = EffectPackageSvgResources.StaticProfile, ["mode"] = "vector",
                ["viewport"] = new JsonObject { ["width"] = 100, ["height"] = 100 }, ["fonts"] = new JsonArray(),
                ["images"] = id == "unused" && unusedImage is not null ? new JsonArray("assets/unused.bin") : new JsonArray(),
            };
            files.Add("resources/" + id + ".json", Encoding.UTF8.GetBytes(descriptor.ToJsonString()));
        }
        JsonArray entries = [];
        foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.Ordinal)) entries.Add(new JsonObject
        {
            ["path"] = pair.Key, ["role"] = pair.Key.EndsWith(".svg", StringComparison.Ordinal) ? "svg"
                : pair.Key.EndsWith(".json", StringComparison.Ordinal) ? "svgResource" : "image",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
        });
        JsonObject manifest = new()
        {
            ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion, ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion,
            ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile, ["packageId"] = "svg.author.tests", ["name"] = "Author SVG", ["version"] = "1.0.0",
            ["requires"] = new JsonArray(EffectPackageSvgResources.VectorCapability), ["lineExit"] = new JsonObject { ["durationUs"] = 0 },
            ["emitters"] = new JsonArray(), ["files"] = entries, ["parameters"] = new JsonArray(), ["exports"] = new JsonArray(),
        };
        return EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            files.Select(pair => KeyValuePair.Create(pair.Key, (ReadOnlyMemory<byte>)pair.Value)));
    }

    private sealed class Probe(Func<EffectPackageContents, EffectPackageSvgResources, CancellationToken, PackageSvgInspection> validate) : IPackageSvgValidator
    {
        internal int Calls { get; private set; }
        public PackageSvgInspection Validate(EffectPackageContents contents, EffectPackageSvgResources resources, CancellationToken cancellationToken = default)
        { Calls++; return validate(contents, resources, cancellationToken); }
    }
}
