using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class PackageSnapshotRuntimeEquivalenceTests
{
    [Theory]
    [InlineData("scale", false)] [InlineData("scale", true)]
    [InlineData("graph", false)] [InlineData("graph", true)]
    [InlineData("sprite", false)] [InlineData("sprite", true)]
    [InlineData("material", false)] [InlineData("material", true)]
    public async Task 包源往返在真实Runtime普通透明前进后退保持像素诊断且反事实证实执行(string kind, bool transparent)
    {
        PackageRuntimeFixture.AssertFixedFont();
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var factory = new ProjectRenderSessionFactory(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
            temp.FilePath("cache"), videos, ReplayReason.Initial);
        ProjectSnapshot source = PackageRuntimeFixture.Create(kind);
        ProjectSnapshot decoded = PackageProjectSnapshotCodec.Decode(PackageProjectSnapshotCodec.Encode(source));
        ProjectSnapshot removed = PackageRuntimeFixture.WithoutContribution(source, kind);
        Assert.NotNull(source.EffectInputsFingerprint); Assert.Equal(source.EffectInputsFingerprint, decoded.EffectInputsFingerprint);
        if (kind == "material")
        {
            Assert.Empty(source.Effects.Instances); Assert.Null(source.Effects.InputsFingerprint);
            Assert.IsType<MakoKaraGenerator.Core.Projects.MaterialSpec.PackageMaterial>(source.LyricStyle.UnsungFill.Material);
        }
        await using IRenderFrameSession original = await factory.CreateAsync(source);
        await using IRenderFrameSession restored = await factory.CreateAsync(decoded);
        await using IRenderFrameSession counterfactual = await factory.CreateAsync(removed);
        var originalCursor = new RuntimeDiagnosticCursor(); var restoredCursor = new RuntimeDiagnosticCursor();
        foreach (long time in new long[] { 0, 100_000, 300_000, 500_000, 100_000, 500_000 })
        {
            using RenderFrame expected = await original.RenderAsync(Request(source, time, transparent));
            using RenderFrame actual = await restored.RenderAsync(Request(decoded, time, transparent));
            using RenderFrame plain = await counterfactual.RenderAsync(Request(removed, time, transparent));
            Assert.Equal(expected.Target, actual.Target); Assert.Equal(expected.TimeUs, actual.TimeUs); Assert.Equal(expected.Format, actual.Format);
            Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
            Assert.False(actual.PixelMemory.Span.SequenceEqual(plain.PixelMemory.Span), $"{kind} 在 {time} 未实际贡献像素。");
            // Each factory owns a real log epoch. Compare report content and occurrence
            // ordering while allowing the independent sessions to retain their GUIDs.
            var expectedOccurrences = expected.DiagnosticSnapshot.ReadAll().ToArray();
            var actualOccurrences = actual.DiagnosticSnapshot.ReadAll().ToArray();
            Assert.Equal(expectedOccurrences.Select(value => (value.Sequence, value.Diagnostic)), actualOccurrences.Select(value => (value.Sequence, value.Diagnostic)));
            Assert.Equal(originalCursor.ReadNew(expected.DiagnosticSnapshot).Select(value => value.Diagnostic),
                restoredCursor.ReadNew(actual.DiagnosticSnapshot).Select(value => value.Diagnostic));
            if (kind != "material")
            {
                var package = Assert.Single(source.Effects.Packages);
                Assert.Contains(actualOccurrences, value => value.Diagnostic.Diagnostic.Code == PackageRuntimeFixture.WarningCode
                    && value.Diagnostic.Diagnostic.Message == PackageRuntimeFixture.WarningMessage
                    && value.Diagnostic.Diagnostic.Severity == DiagnosticSeverity.Warning
                    && value.Diagnostic.PackageIdentity == new EffectPackageIdentity(package.PackageId, package.Revision, kind == "graph" ? "controller" : "scale"));
                Assert.All(actualOccurrences, value => Assert.NotEqual(Guid.Empty, value.Epoch));
            }
        }
        await original.DisposeAsync();
        using RenderFrame survivor = await restored.RenderAsync(Request(decoded, 300_000, transparent));
        Assert.Contains(survivor.PixelMemory.ToArray(), value => value != 0);
        if (kind == "material") Assert.False(Directory.Exists(temp.FilePath("cache")));
    }

    private static RenderRequest Request(ProjectSnapshot snapshot, long time, bool transparent) => RenderRequest.At(snapshot, time) with
    { Target = new() { PixelWidth = 320, PixelHeight = 180 }, TransparentBackground = transparent };
}
