using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Tests;

public sealed class EffectPackageV2AdmissionTests
{
    [Fact]
    public void 显式阻断v1并固定v2实际SDK身份及能力()
    {
        var files = EffectPackageFixture.Files();
        byte[] oldManifest = EffectPackageFixture.Bytes(V1Manifest(files));
        Assert.False(EffectPackageProfiles.IsSupported(V1Profile));
        Assert.Equal("PackageProfileMismatch", Assert.Throws<EffectPackageException>(() =>
            EffectPackageProfiles.RequireSupported(V1Profile)).Code);
        Assert.Equal("PackageProfileMismatch", Assert.Throws<EffectPackageException>(() =>
            EffectPackageProfiles.ShaderProgramProfile(V1Profile)).Code);
        Assert.Equal(ShaderProgramDefinition.RasterProfile,
            EffectPackageProfiles.ShaderProgramProfile(EffectPackageProfiles.V2));
        Assert.Equal("PackageProfileMismatch", Assert.Throws<EffectPackageException>(() =>
            EffectPackageContents.CreateSupported(oldManifest, EffectPackageFixture.Inputs(files))).Code);
        Assert.Equal("PackageProfileMismatch", Assert.Throws<EffectPackageException>(() =>
            EffectPackageContents.Create(oldManifest, EffectPackageFixture.Inputs(files), EffectPackageProfiles.V2)).Code);
        Assert.Equal("PackageProfileMismatch", Assert.Throws<EffectPackageException>(() =>
            EffectPackageContents.Create(oldManifest, EffectPackageFixture.Inputs(files), V1Profile)).Code);

        var current = SdkV2PackageFixture.Contents(manifest => manifest["sdkVersion"] = "2.0.7");
        Assert.Equal("2.0.7", current.Profile.SdkVersion); Assert.True(EffectPackageProfiles.IsV2(current.Profile));
        Assert.Equal(600_000, current.LineExitDurationUs); Assert.Empty(current.Emitters);
        var definition = EffectPackageDefinition.Read(current);
        Assert.Equal(current.Requires, definition.Requires); Assert.Equal(current.LineExitDurationUs, definition.LineExitDurationUs);
        Assert.NotEqual(current.Revision, SdkV2PackageFixture.Contents().Revision);
    }

    [Theory]
    [InlineData("sdkVersion", "3.0.0")]
    [InlineData("sdkVersion", "2.1.0")]
    [InlineData("sdkVersion", "2.0")]
    [InlineData("sdkVersion", "2.00.0")]
    [InlineData("sdkVersion", "2.0.0-preview")]
    [InlineData("shaderProfile", "skia-ganesh-3.119.4-v1")]
    public void SDK主版本未来次版本及错误shader身份不回退v1(string field, string value)
        => Assert.Equal("PackageProfileMismatch", Assert.Throws<EffectPackageException>(() =>
            SdkV2PackageFixture.Contents(manifest => manifest[field] = value)).Code);

    [Theory]
    [InlineData("future.unknown")]
    [InlineData("path.svg")]
    [InlineData("media.features.cpu")]
    [InlineData("unknown.required")]
    public void 尚未接通的必须能力在读取闭包时具名拒绝(string capability)
    {
        var error = Assert.Throws<EffectPackageException>(() => SdkV2PackageFixture.Contents(manifest =>
            manifest["requires"]!.AsArray().Add(capability)));
        Assert.Equal("PackageCapabilityUnsupported", error.Code); Assert.Contains(capability, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing", "PackageManifestMissingField")]
    [InlineData("duplicate", "PackageReferenceDuplicate")]
    [InlineData("empty", "PackageParameterTypeMismatch")]
    [InlineData("type", "PackageParameterTypeMismatch")]
    [InlineData("emitter", "PackageManifestMissingField")]
    [InlineData("emitter-type", "PackageManifestType")]
    [InlineData("negative-exit", "PackageLineExitInvalid")]
    [InlineData("long-exit", "PackageLineExitInvalid")]
    [InlineData("fraction-exit", "PackageLineExitInvalid")]
    [InlineData("undeclared-exit", "PackageCapabilityMissing")]
    public void v2清单必须完整且不猜测能力退场或发射器(string failure, string code)
    {
        var error = Assert.Throws<EffectPackageException>(() => SdkV2PackageFixture.Contents(manifest =>
        {
            switch (failure)
            {
                case "missing": manifest.Remove("requires"); break;
                case "duplicate": manifest["requires"]!.AsArray().Add("token.affine"); break;
                case "empty": manifest["requires"]!.AsArray().Add(""); break;
                case "type": manifest["requires"] = "token.affine"; break;
                case "emitter": manifest["emitters"]!.AsArray().Add(new JsonObject { ["id"] = "spark" }); break;
                case "emitter-type": manifest["emitters"] = "none"; break;
                case "negative-exit": manifest["lineExit"]!["durationUs"] = -1; break;
                case "long-exit": manifest["lineExit"]!["durationUs"] = 3_000_001; break;
                case "fraction-exit": manifest["lineExit"]!["durationUs"] = 1.5; break;
                case "undeclared-exit": manifest["requires"] = new JsonArray("token.affine"); break;
            }
        }));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void v1包在带或不带v2字段时均拒绝且v2输出不能绕过包级必须能力()
    {
        var files = EffectPackageFixture.Files(); var manifest = V1Manifest(files);
        Assert.Equal("PackageProfileMismatch", Assert.Throws<EffectPackageException>(() =>
            EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files))).Code);
        manifest["requires"] = new JsonArray();
        Assert.Equal("PackageProfileMismatch", Assert.Throws<EffectPackageException>(() =>
            EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files))).Code);
        var undeclared = SdkV2PackageFixture.Contents(value => value["exports"]![0]!["outputs"]![0]!["requires"] = new JsonArray("group.affine"));
        Assert.Equal("PackageCapabilityMissing", Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(undeclared)).Code);
        var unsupported = SdkV2PackageFixture.Contents(value => value["exports"]![0]!["outputs"]![0]!["requires"] = new JsonArray("future.unknown"));
        Assert.Equal("PackageCapabilityUnsupported", Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(unsupported)).Code);
    }

    [Theory]
    [InlineData("baseToken", "VisualTransform")]
    [InlineData("baseToken", "StyleModulation")]
    [InlineData("cluster", "VisualTransform")]
    public void v2正式字与簇目标按输出类型准入且v1直接阻断(string target, string kind)
    {
        var current = SdkV2PackageFixture.Contents(manifest =>
        {
            manifest["requires"]!.AsArray().Add("cluster.affine");
            manifest["exports"]![0]!["outputs"]![0]!["target"] = target;
            manifest["exports"]![0]!["outputs"]![0]!["kind"] = kind;
            manifest["exports"]![0]!["outputs"]![0]!["requires"] = new JsonArray(target == "cluster" ? "cluster.affine" : "token.affine");
        });
        var output = Assert.Single(Assert.Single(EffectPackageDefinition.Read(current).Controllers).Outputs);
        Assert.Equal(target, output.Target); Assert.Equal(kind, output.Kind);
        var files = EffectPackageFixture.Files(); var old = V1Manifest(files);
        old["exports"]![0]!["outputs"]![0]!["target"] = target;
        old["exports"]![0]!["outputs"]![0]!["kind"] = kind;
        Assert.Equal("PackageProfileMismatch", Assert.Throws<EffectPackageException>(() =>
            EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(old), EffectPackageFixture.Inputs(files))).Code);
    }

    [Theory]
    [InlineData("baseToken", "GeometryMapping")]
    [InlineData("baseToken", "ImageGraph")]
    [InlineData("cluster", "StyleModulation")]
    [InlineData("cluster", "Overlay")]
    public void v2不授予基础字或簇未接通的输出类型(string target, string kind)
    {
        var package = SdkV2PackageFixture.Contents(manifest =>
        {
            manifest["exports"]![0]!["outputs"]![0]!["target"] = target;
            manifest["exports"]![0]!["outputs"]![0]!["kind"] = kind;
        });
        Assert.Equal("PackageOutputTargetUnsupported", Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(package)).Code);
    }

    [Fact]
    public void 分派前仍执行清单预算取消与重复身份门禁()
    {
        var files = EffectPackageFixture.Files(); byte[] manifest = EffectPackageFixture.Bytes(SdkV2PackageFixture.Manifest(files));
        Assert.Equal("PackageFileBudgetExceeded", Assert.Throws<EffectPackageException>(() => EffectPackageContents.CreateSupported(
            manifest, EffectPackageFixture.Inputs(files), new() { MaximumFileBytes = 1 })).Code);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => EffectPackageContents.CreateSupported(manifest, EffectPackageFixture.Inputs(files), cancellationToken: canceled.Token));
        string duplicate = Encoding.UTF8.GetString(manifest).Replace("\"sdkVersion\":\"2.0.0\"", "\"sdkVersion\":\"2.0.0\",\"sdkVersion\":\"2.0.0\"", StringComparison.Ordinal);
        Assert.Equal("PackageJsonDuplicateKey", Assert.Throws<EffectPackageException>(() => EffectPackageContents.CreateSupported(
            Encoding.UTF8.GetBytes(duplicate), EffectPackageFixture.Inputs(files))).Code);
    }

    private static readonly EffectPackageProfile V1Profile = new(2, "1.0.0", ShaderProgramDefinition.RasterProfile);

    private static JsonObject V1Manifest(Dictionary<string, byte[]> files)
    {
        var manifest = EffectPackageFixture.Manifest(files);
        manifest["packageVersion"] = V1Profile.PackageVersion;
        manifest["sdkVersion"] = V1Profile.SdkVersion;
        manifest["shaderProfile"] = V1Profile.ShaderProfile;
        manifest.Remove("requires");
        manifest.Remove("lineExit");
        manifest.Remove("emitters");
        manifest["exports"]![0]!["outputs"]![0]!["requires"] = new JsonArray();
        return manifest;
    }
}
