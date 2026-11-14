using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class EffectPackageCompilationTests
{
    [Fact]
    public async Task 分享包移走原目录后实际离线编译并调用SDK且图片变化不重编译()
    {
        using TemporaryDirectory temp = new(); string source = temp.FilePath("editable"); V2PackageTestHelper.WriteDirectory(source);
        var original = await EffectPackageTransfer.ReadDirectoryAsync(source, EffectPackageProfiles.V2);
        string share = temp.FilePath("share.mkfx"); await EffectPackageTransfer.WriteArchiveAsync(share, original);
        Directory.Move(source, temp.FilePath("moved")); var imported = await EffectPackageTransfer.ReadArchiveAsync(share, EffectPackageProfiles.V2);
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await compiler.CompileAsync(EffectPackageSources.Read(imported)); Assert.True(compiled.Succeeded, string.Join('\n', compiled.Diagnostics));
        var changedResource = V2PackageTestHelper.Contents(9); var sameCode = await compiler.CompileAsync(EffectPackageSources.Read(changedResource));
        Assert.NotEqual(imported.Revision, changedResource.Revision); Assert.Equal(compiled.ArtifactHash, sameCode.ArtifactHash);
        Assert.True(compiled.PeImage.AsSpan().SequenceEqual(sameCode.PeImage.AsSpan()));
        using ScriptModule module = new(compiled.PeImage.AsMemory(), compiled.PdbImage.AsMemory());
        var effect = module.CreateInstance<IPureKaraokeEffect>("PackEffect"); TokenFamilyTarget target = new(new("token"), new("binding"));
        TokenGeometrySample geometry = new() { WithRuby = target, BaseOnly = new(target.TokenId, target.BindingId), BranchId = new("branch"), LineId = new("line"),
            BaseGeometry = new() { AdvanceBounds = new(10, 20, 30, 40), Baseline = new(10, 35), BaseWorldMatrix = Affine2.Identity, CanTransformIndependently = true },
            FamilyGeometry = new() { AdvanceBounds = new(10, 20, 30, 40), Baseline = new(10, 35), BaseWorldMatrix = Affine2.Identity } };
        FrameToken token = new() { TokenId = target.TokenId, BindingId = target.BindingId, BranchId = geometry.BranchId, LineId = geometry.LineId, SourceText = "字",
            WithRuby = target, BaseOnly = geometry.BaseOnly, Performance = new(new(0, 1_000_000), 1_000_000, 500_000, .5, true, false, ProgressSource.Token) };
        ContentCatalog catalog = new([], [], [], []);
        SceneCatalog scene = new(catalog, [], [], [], []);
        ParameterValues parameters = new([new("peak", new ParameterValue.Number(1.5))]);
        var legacy = new TextFrameHostInputs(500_000, BaselineStyles.Empty, new() { TokensInBinding = [token] },
            [geometry], [], [], [], new EffectBinding.Tokens([target.TokenId]), OutputMode.Opaque);
        DeclareContext declare = new(catalog, scene, parameters, AudioFeatures.NotAnalyzed, ResourceCatalog.Empty, 1, TimelineIndex.Empty, new(640, 360), new("instance"));
        PreparedEffect prepared = effect.Declare(declare);
        TextFrame frame = new(declare, legacy);
        TextOutput output = new(in frame, ["token.affine"]); effect.Evaluate(in frame, prepared, output);
        var write = Assert.IsType<TextWrite.Transform>(Assert.Single(output.Complete().Writes));
        var family = Assert.IsType<TextTarget.Family>(write.Target); Assert.Equal(target, family.Value); Assert.Equal(1.5, write.LocalMatrix.A);
        Assert.Equal(geometry.BaseGeometry.AdvanceBounds.Center, write.LocalMatrix.Map(geometry.BaseGeometry.AdvanceBounds.Center));
    }
    [Fact]
    public void 无源码不伪造控制器且投影保留包内诊断路径()
    {
        var sources = EffectPackageSources.Read(V2PackageTestHelper.Contents()); Assert.Equal("src/PackEffect.cs", Assert.Single(sources).Path);
        var files = V2PackageTestHelper.Files(); files.Remove("src/PackEffect.cs");
        var content = EffectPackageContents.Create(V2PackageTestHelper.Bytes(V2PackageTestHelper.Manifest(files)), V2PackageTestHelper.Inputs(files), EffectPackageProfiles.V2);
        Assert.Equal("PackageCSharpMissing", Assert.Throws<EffectPackageException>(() => EffectPackageSources.Read(content)).Code);
    }
    [Fact]
    public async Task 包内源码错误仍给出真实文件行列且不发布DLL()
    {
        using TemporaryDirectory temp = new(); var files = V2PackageTestHelper.Files(); files["src/PackEffect.cs"] = System.Text.Encoding.UTF8.GetBytes("public class PackEffect {\n invalid !!!\n}");
        var contents = EffectPackageContents.Create(V2PackageTestHelper.Bytes(V2PackageTestHelper.Manifest(files)), V2PackageTestHelper.Inputs(files), EffectPackageProfiles.V2);
        string share = temp.FilePath("broken.mkfx"); await EffectPackageTransfer.WriteArchiveAsync(share, contents);
        var imported = await EffectPackageTransfer.ReadArchiveAsync(share, EffectPackageProfiles.V2);
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")); var compiled = await compiler.CompileAsync(EffectPackageSources.Read(imported));
        Assert.False(compiled.Succeeded); Assert.Empty(compiled.PeImage); Assert.Contains(compiled.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Path == "src/PackEffect.cs" && diagnostic.Line == 2 && diagnostic.Column > 0);
    }
}
