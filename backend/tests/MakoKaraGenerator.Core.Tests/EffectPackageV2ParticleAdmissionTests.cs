using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Tests;

public sealed class EffectPackageV2ParticleAdmissionTests
{
    private static readonly string[] TextThenLate = ["text", "late"];

    [Theory]
    [InlineData("particles.continuous")]
    [InlineData("particles.burst")]
    [InlineData("visuals.dots")]
    [InlineData("visuals.segments")]
    [InlineData("visuals.trails")]
    [InlineData("visuals.sprites")]
    public void 已接通M08能力必须准入且保留精确名字(string capability)
    {
        Assert.Contains(capability, EffectPackageProfiles.SupportedV2Capabilities);
        var contents = SdkV2PackageFixture.Contents(manifest => manifest["requires"]!.AsArray().Add(capability));
        Assert.Contains(capability, contents.Requires);
    }

    [Theory]
    [InlineData("missing", "PackageManifestMissingField")]
    [InlineData("unknown", "PackageManifestUnknownField")]
    [InlineData("kind", "PackageEmitterKindUnsupported")]
    [InlineData("zero", "PackageEmitterLifetimeInvalid")]
    [InlineData("long", "PackageEmitterLifetimeInvalid")]
    [InlineData("fraction", "PackageEmitterLifetimeInvalid")]
    [InlineData("duplicate", "PackageEmitterDuplicate")]
    [InlineData("budget", "PackageEmitterBudgetExceeded")]
    public void 根发射器声明严格校验字段种类整数寿命与独立数量(string failure, string code)
    {
        var error = Assert.Throws<EffectPackageException>(() => SdkV2PackageFixture.Contents(manifest =>
        {
            manifest["requires"]!.AsArray().Add("particles.continuous");
            var emitter = Emitter();
            switch (failure)
            {
                case "missing": emitter.Remove("kind"); break;
                case "unknown": emitter["rate"] = 10; break;
                case "kind": emitter["kind"] = "Cursor"; break;
                case "zero": emitter["maxLifetimeUs"] = 0; break;
                case "long": emitter["maxLifetimeUs"] = 5_000_001; break;
                case "fraction": emitter["maxLifetimeUs"] = 1.5; break;
            }
            var list = new JsonArray(emitter);
            if (failure == "duplicate")
            {
                emitter["id"] = "same"; list.Add(Emitter("same"));
            }
            if (failure == "budget") for (int index = 1; index < 33; index++) list.Add(Emitter("emitter-" + index));
            manifest["emitters"] = list;
        }));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void 合法发射器保留声明且遗漏包级能力明确失败()
    {
        EffectPackageContents Create(bool declare) => SdkV2PackageFixture.Contents(manifest =>
        {
            if (declare) manifest["requires"]!.AsArray().Add("particles.continuous");
            manifest["emitters"] = new JsonArray(Emitter());
        });
        var emitter = Assert.Single(Create(true).Emitters);
        Assert.Equal("cursor", emitter.Kind); Assert.Equal(5_000_000, emitter.MaxLifetimeUs);
        Assert.Equal("PackageCapabilityMissing", Assert.Throws<EffectPackageException>(() => Create(false)).Code);
    }

    [Fact]
    public void v2画布目标仅属于Overlay且旧目标规则保持()
    {
        foreach (string kind in new[] { "Overlay", "VisualTransform", "StyleModulation", "GeometryMapping", "ImageGraph" })
        {
            var contents = SdkV2PackageFixture.Contents(manifest =>
            {
                var output = manifest["exports"]![0]!["outputs"]![0]!;
                output["kind"] = kind; output["target"] = "canvas"; output["requires"] = new JsonArray();
            });
            if (kind == "Overlay") Assert.Equal("canvas", Assert.Single(Assert.Single(EffectPackageDefinition.Read(contents).Controllers).Outputs).Target);
            else Assert.Equal("PackageOutputTargetUnsupported", Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(contents)).Code);
        }
        var textOverlay = SdkV2PackageFixture.Contents(manifest => manifest["exports"]![0]!["outputs"]![0]!["kind"] = "Overlay");
        Assert.Equal("PackageOutputTargetUnsupported", Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(textOverlay)).Code);
        var files = EffectPackageFixture.Files(); var old = EffectPackageFixture.Manifest(files);
        old["packageVersion"] = EffectPackageProfiles.V1.PackageVersion;
        old["sdkVersion"] = EffectPackageProfiles.V1.SdkVersion;
        old["shaderProfile"] = EffectPackageProfiles.V1.ShaderProfile;
        old.Remove("requires"); old.Remove("lineExit"); old.Remove("emitters");
        old["exports"]![0]!["outputs"]![0]!["requires"] = new JsonArray();
        old["exports"]![0]!["outputs"]![0]!["kind"] = "Overlay";
        var legacy = EffectPackageDefinition.Read(EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(old), EffectPackageFixture.Inputs(files)));
        Assert.Equal("tokenFamily", Assert.Single(Assert.Single(legacy.Controllers).Outputs).Target);
        old["exports"]![0]!["outputs"]![0]!["target"] = "canvas";
        Assert.Equal("PackageOutputTargetUnsupported", Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(
            EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(old), EffectPackageFixture.Inputs(files)))).Code);
    }

    [Fact]
    public void 晚期依赖不能反向进入文字但可以读取文字输出()
    {
        EffectPackageContents Package(bool reverse) => SdkV2PackageFixture.Contents(manifest =>
        {
            var outputs = manifest["exports"]![0]!["outputs"]!.AsArray(); var text = outputs[0]!;
            text["id"] = "text"; text["dependsOn"] = reverse ? new JsonArray("late") : new JsonArray();
            outputs.Add(new JsonObject { ["id"] = "late", ["kind"] = "Overlay", ["target"] = "canvas",
                ["requires"] = new JsonArray(), ["dependsOn"] = reverse ? new JsonArray() : new JsonArray("text") });
        });
        Assert.Equal("PackageOutputStageDependencyInvalid", Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(Package(true))).Code);
        Assert.Equal(TextThenLate, Assert.Single(EffectPackageDefinition.Read(Package(false)).Controllers).TopologicalOutputs.Select(output => output.Id));
    }

    private static JsonObject Emitter(string id = "cursor") => new() { ["id"] = id, ["kind"] = "cursor", ["maxLifetimeUs"] = 5_000_000L };
}
