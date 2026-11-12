using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Persistence;

public sealed class LibraryPublishedEncodingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 两库编码和保存分别验证且保持规范发布字节与修订(bool configuration)
    {
        using TemporaryDirectory directory = new();
        if (configuration)
        {
            string path = ScriptConfigurationLibrary.ResolvePath(directory.Path);
            var item = ScriptConfiguration.Create("configuration", "精确参数与完整种子", EffectPackageFixture.Contents(), "scale",
                EffectParameterOverrides.Parse("{\"peak\":1.2345678901234567}"), ScriptRelativeTarget.TokenFamily,
                ScriptReadScope.BranchInput, "count-source", ulong.MaxValue);
            Verify(item, path, ScriptConfigurationLibrary.Encode,
                (items, expected) => ScriptConfigurationLibrary.Save(path, items, expected));
        }
        else
        {
            string path = StyleTemplateLibrary.ResolvePath(directory.Path);
            var item = StyleTemplate.Create("template", "完整中文模板", StyleTemplateKind.Line,
                LyricStyle.Default, [LyricStyleProperty.SungFill]);
            Verify(item, path, StyleTemplateLibrary.Encode,
                (items, expected) => StyleTemplateLibrary.Save(path, items, expected));
        }
    }

    private static void Verify<T>(T item, string path, Func<IEnumerable<T>, byte[]> encode,
        Func<IEnumerable<T>, LibraryIndexRevision, LibraryIndexRevision> save) where T : class
    {
        byte[] encoded = encode([item]);
        using JsonDocument document = JsonDocument.Parse(encoded);
        byte[] expectedBytes = JsonSerializer.SerializeToUtf8Bytes(document.RootElement, ProjectJson.Options);
        Assert.False(encoded.AsSpan().SequenceEqual(expectedBytes));

        LibraryIndexRevision revision = save([item], LibraryIndexRevision.Missing);

        Assert.Equal(expectedBytes, File.ReadAllBytes(path));
        Assert.Equal(LibraryIndexRevision.FromBytes(expectedBytes), revision);

        IEnumerable<T>[] invalidInputs = [[item, item], [null!]];
        foreach (IEnumerable<T> invalid in invalidInputs)
        {
            Assert.Throws<ArgumentException>(() => encode(invalid));
            Assert.Throws<ArgumentException>(() => save(invalid, revision));
            Assert.Equal(expectedBytes, File.ReadAllBytes(path));
        }
    }
}
