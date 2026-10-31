using System.Text;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.TestSupport;

internal static class PackageFontFixture
{
    internal const string Family = "Mako Sans CJK SC";
    internal static byte[] FontBytes()
    {
        byte[] bytes = File.ReadAllBytes("/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc");
        byte[] original = Encoding.BigEndianUnicode.GetBytes("Noto");
        byte[] replacement = Encoding.BigEndianUnicode.GetBytes("Mako");
        for (int index = 0; index <= bytes.Length - original.Length; index++)
            if (bytes.AsSpan(index, original.Length).SequenceEqual(original)) replacement.CopyTo(bytes, index);
        return bytes;
    }
    internal static EffectPackageContents Contents()
    {
        var files = EffectPackageFixture.Files(); files.Add("fonts/fixed-cjk.ttc", FontBytes());
        var manifest = EffectPackageFixture.Manifest(files);
        foreach (var file in manifest["files"]!.AsArray())
            if (file!["path"]!.GetValue<string>() == "fonts/fixed-cjk.ttc") file["role"] = "font";
        return EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
    }
}
