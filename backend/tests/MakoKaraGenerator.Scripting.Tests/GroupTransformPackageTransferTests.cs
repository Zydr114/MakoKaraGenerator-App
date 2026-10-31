using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class GroupTransformPackageTransferTests
{
    [Fact]
    public async Task 真实组仿射包分享移走原目录后仍从固定归档编译并调用公开作者入口()
    {
        using TemporaryDirectory temp = new(); string directory = temp.FilePath("editable"); Directory.CreateDirectory(directory);
        var contents = EffectPackageFixture.GroupTransform();
        await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), contents.Manifest.ToArray());
        foreach (var file in contents.Files)
        {
            string path = Path.Combine(directory, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, file.Content.ToArray());
        }
        var original = await EffectPackageTransfer.ReadDirectoryAsync(directory, EffectPackageFixture.Profile);
        string archive = temp.FilePath("group.mkfx"); await EffectPackageTransfer.WriteArchiveAsync(archive, original);
        Directory.Move(directory, temp.FilePath("moved"));
        var imported = await EffectPackageTransfer.ReadArchiveAsync(archive, EffectPackageFixture.Profile);
        Assert.Equal(original.Revision, imported.Revision);
        var compiled = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(imported),
            new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));
        Assert.True(compiled.Artifact.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        var effect = compiled.CreateEffect("group-transform", new("group"), new EffectBinding.Node(new("root"), false), 2, "{\"node\":\"root\"}"u8.ToArray());
        using ScriptModule module = new(compiled.Artifact.PeImage.AsMemory(), compiled.Artifact.PdbImage.AsMemory());
        var author = module.CreateInstance<IKaraokeEffect>(effect.EntryType!);
        ContentCatalog catalog = new([], [], [], []);
        ProjectContext project = new() { Content = catalog, Scene = new(catalog, [], [], [], []), CanvasSize = new(640, 360) };
        DeterministicRandom random = new(effect.Seed, effect.Id);
        author.Initialize(new() { Project = project, Parameters = effect.Parameters, Resources = effect.Resources, Random = random });
        GroupGeometrySample group = new() { Target = new(new("root")), BaseWorldMatrix = Affine2.Translation(100, 50),
            ReferenceBounds = new(0, 0, 100, 50), WorldPivot = new(150, 75), WorldInkCorners = [] };
        FrameWriter writer = new([], effect.Capabilities, groupGeometry: [group]);
        author.Evaluate(new() { TimeUs = 0, Project = project, Parameters = effect.Parameters, Random = random, Binding = effect.Binding,
            Presentations = new(), GroupGeometry = [group] }, writer);
        var output = Assert.Single(writer.Complete().GroupTransforms);
        Assert.Equal(group.Target, output.Target); Assert.Equal(Affine2.Translation(30, -16), output.LocalMatrix);
        author.Dispose();
    }
}
