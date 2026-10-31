using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class EffectPackageCompilationTests
{
    [Fact]
    public async Task 分享包移走原目录后实际离线编译并调用SDK且图片变化不重编译()
    {
        using TemporaryDirectory temp = new(); string source = temp.FilePath("editable"); EffectPackageFixture.WriteDirectory(source);
        var original = await EffectPackageTransfer.ReadDirectoryAsync(source, EffectPackageFixture.Profile);
        string share = temp.FilePath("share.mkfx"); await EffectPackageTransfer.WriteArchiveAsync(share, original);
        Directory.Move(source, temp.FilePath("moved")); var imported = await EffectPackageTransfer.ReadArchiveAsync(share, EffectPackageFixture.Profile);
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await compiler.CompileAsync(EffectPackageSources.Read(imported)); Assert.True(compiled.Succeeded, string.Join('\n', compiled.Diagnostics));
        var changedResource = EffectPackageFixture.Contents(9); var sameCode = await compiler.CompileAsync(EffectPackageSources.Read(changedResource));
        Assert.NotEqual(imported.Revision, changedResource.Revision); Assert.Equal(compiled.ArtifactHash, sameCode.ArtifactHash);
        Assert.True(compiled.PeImage.AsSpan().SequenceEqual(sameCode.PeImage.AsSpan()));
        using ScriptModule module = new(compiled.PeImage.AsMemory(), compiled.PdbImage.AsMemory());
        var effect = module.CreateInstance<IKaraokeEffect>("PackEffect"); TokenFamilyTarget target = new(new("token"), new("binding"));
        TokenGeometrySample geometry = new() { WithRuby = target, BaseOnly = new(target.TokenId, target.BindingId), BranchId = new("branch"), LineId = new("line"),
            BaseGeometry = new() { AdvanceBounds = new(10, 20, 30, 40), Baseline = new(10, 35), BaseWorldMatrix = Affine2.Identity, CanTransformIndependently = true },
            FamilyGeometry = new() { AdvanceBounds = new(10, 20, 30, 40), Baseline = new(10, 35), BaseWorldMatrix = Affine2.Identity } };
        FrameToken token = new() { TokenId = target.TokenId, BindingId = target.BindingId, BranchId = geometry.BranchId, LineId = geometry.LineId, SourceText = "字",
            WithRuby = target, BaseOnly = geometry.BaseOnly, Performance = new(new(0, 1_000_000), 1_000_000, 500_000, .5, true, false, ProgressSource.Token) };
        ContentCatalog catalog = new([], [], [], []);
        var frame = new FrameContext { TimeUs = 500_000, Project = new() { Content = catalog, Scene = new(catalog, [], [], [], []), CanvasSize = new(640, 360) },
            Parameters = new([new("peak", new ParameterValue.Number(1.5))]), Random = new(1, new("instance")),
            Binding = new EffectBinding.Tokens([target.TokenId]), Presentations = new() { TokensInBinding = [token] }, Geometry = [geometry] };
        FrameWriter writer = new([geometry], EffectCapabilities.Transforms); effect.Evaluate(frame, writer);
        var transform = Assert.Single(writer.Complete().TokenTransforms); Assert.Equal(target, transform.Target); Assert.Equal(1.5, transform.LocalMatrix.A);
        Assert.Equal(new Point2(20, 30), transform.LocalMatrix.Map(new(20, 30)));
    }
    [Fact]
    public void 无源码不伪造控制器且投影保留包内诊断路径()
    {
        var sources = EffectPackageSources.Read(EffectPackageFixture.Contents()); Assert.Equal("src/PackEffect.cs", Assert.Single(sources).Path);
        var files = EffectPackageFixture.Files(); files.Remove("src/PackEffect.cs");
        var content = EffectPackageContents.Create(EffectPackageFixture.Bytes(EffectPackageFixture.Manifest(files)), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        Assert.Equal("PackageCSharpMissing", Assert.Throws<EffectPackageException>(() => EffectPackageSources.Read(content)).Code);
    }
    [Fact]
    public async Task 包内源码错误仍给出真实文件行列且不发布DLL()
    {
        using TemporaryDirectory temp = new(); var files = EffectPackageFixture.Files(); files["src/PackEffect.cs"] = System.Text.Encoding.UTF8.GetBytes("public class PackEffect {\n invalid !!!\n}");
        var contents = EffectPackageContents.Create(EffectPackageFixture.Bytes(EffectPackageFixture.Manifest(files)), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        string share = temp.FilePath("broken.mkfx"); await EffectPackageTransfer.WriteArchiveAsync(share, contents);
        var imported = await EffectPackageTransfer.ReadArchiveAsync(share, EffectPackageFixture.Profile);
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")); var compiled = await compiler.CompileAsync(EffectPackageSources.Read(imported));
        Assert.False(compiled.Succeeded); Assert.Empty(compiled.PeImage); Assert.Contains(compiled.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Path == "src/PackEffect.cs" && diagnostic.Line == 2 && diagnostic.Column > 0);
    }
}
