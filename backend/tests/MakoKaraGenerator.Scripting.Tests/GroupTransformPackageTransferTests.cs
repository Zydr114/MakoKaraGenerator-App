using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class GroupTransformPackageTransferTests
{
    [Fact]
    public async Task 真实组仿射包分享移走原目录后仍从固定归档编译并调用公开作者入口()
    {
        using TemporaryDirectory temp = new(); string directory = temp.FilePath("editable"); Directory.CreateDirectory(directory);
        var contents = V2PackageTestHelper.GroupTransform();
        await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), contents.Manifest.ToArray());
        foreach (var file in contents.Files)
        {
            string path = Path.Combine(directory, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, file.Content.ToArray());
        }
        var original = await EffectPackageTransfer.ReadDirectoryAsync(directory, EffectPackageProfiles.V2);
        string archive = temp.FilePath("group.mkfx"); await EffectPackageTransfer.WriteArchiveAsync(archive, original);
        Directory.Move(directory, temp.FilePath("moved"));
        var imported = await EffectPackageTransfer.ReadArchiveAsync(archive, EffectPackageProfiles.V2);
        Assert.Equal(original.Revision, imported.Revision);
        var compiled = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(imported),
            new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));
        Assert.True(compiled.Artifact.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        var effect = compiled.CreateEffect("group-transform", new("group"), new EffectBinding.Node(new("root"), false), 2, "{\"node\":\"root\"}"u8.ToArray());
        using ScriptModule module = new(compiled.Artifact.PeImage.AsMemory(), compiled.Artifact.PdbImage.AsMemory());
        var author = module.CreateInstance<IPureKaraokeEffect>(effect.EntryType!);
        ContentCatalog catalog = new([], [], [], []);
        SceneCatalog scene = new(catalog, [], [], [], []);
        DeclareContext declare = new(catalog, scene, effect.Parameters, AudioFeatures.NotAnalyzed, effect.Resources, effect.Seed, TimelineIndex.Empty, new(640, 360), effect.Id);
        PreparedEffect prepared = author.Declare(declare);
        GroupGeometrySample group = new() { Target = new(new("root")), BaseWorldMatrix = Affine2.Translation(100, 50),
            ReferenceBounds = new(0, 0, 100, 50), WorldPivot = new(150, 75), WorldInkCorners = [] };
        TextFrameHostInputs legacy = new(0, BaselineStyles.Empty, new(), [], [], [], [group], effect.Binding, OutputMode.Opaque);
        TextFrame frame = new(declare, legacy);
        TextOutput output = new(in frame, ["group.affine"], [group.Target]);
        author.Evaluate(in frame, prepared, output);
        var write = Assert.IsType<TextWrite.Transform>(Assert.Single(output.Complete().Writes));
        Assert.Equal(group.Target, Assert.IsType<TextTarget.Group>(write.Target).Value); Assert.Equal(Affine2.Translation(30, -16), write.LocalMatrix);
    }
}
