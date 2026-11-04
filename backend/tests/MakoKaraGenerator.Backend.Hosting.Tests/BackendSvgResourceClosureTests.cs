using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using static MakoKaraGenerator.Backend.Hosting.Tests.BackendAuthorSvgWorkflowFixture;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendSvgResourceClosureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SVG闭包承认正式资源role并保留未知role拒绝边界(bool atlas)
    {
        await using BackendAuthorSvgWorkflowFixture fixture = await CreateAsync(atlas);
        EffectPackageContents package = await fixture.DownloadAsync();
        PackageClosureInspection accepted = PackageProjectSnapshotCodec.InspectResourceClosure(package);
        Assert.True(accepted.IsValidated, accepted.FailureCode);
        Assert.True(accepted.IdentityValid); Assert.True(accepted.RoleReferencesValid);
        Assert.Empty(accepted.UnsupportedPaths);
        Assert.Equal(package.Files.Length, accepted.ValidatedFileCount);
        Assert.Equal(package.Revision, accepted.PackageRevision);

        JsonObject manifest = JsonNode.Parse(package.Manifest.ToArray())!.AsObject();
        manifest["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == BinaryPath)!["role"] = "future-unknown";
        EffectPackageContents future = EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            package.Files.Select(file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray())));
        PackageClosureInspection rejected = PackageProjectSnapshotCodec.InspectResourceClosure(future);
        Assert.False(rejected.IsValidated);
        Assert.True(rejected.IdentityValid); Assert.True(rejected.RoleReferencesValid);
        Assert.Equal(BinaryPath, Assert.Single(rejected.UnsupportedPaths));
        Assert.Equal(package.Files.Length - 1, rejected.ValidatedFileCount);
        Assert.Equal(BinaryBytes, future.Files.Single(file => file.Path == BinaryPath).Content.ToArray());
    }
}
