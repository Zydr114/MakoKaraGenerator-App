using System.Text;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.TestSupport;

internal static class PackageFontFixture
{
    private static readonly string[] LinuxNotoCandidates =
    [
        "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
        "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
    ];

    internal static string? FindLinuxNotoCjkTtc()
    {
        foreach (string candidate in LinuxNotoCandidates)
            if (File.Exists(candidate))
                return candidate;

        foreach (string root in new[] { "/usr/share/fonts", "/usr/local/share/fonts" })
        {
            if (!Directory.Exists(root)) continue;
            string? exact = Directory.EnumerateFiles(root, "NotoSansCJK-Regular.ttc", SearchOption.AllDirectories).FirstOrDefault();
            if (exact is not null) return exact;
            string? fallback = Directory.EnumerateFiles(root, "NotoSansCJK*.ttc", SearchOption.AllDirectories).FirstOrDefault();
            if (fallback is not null) return fallback;
        }
        return null;
    }

    internal static readonly string Family = OperatingSystem.IsLinux() ? "Mako Sans CJK SC" : ResolveWindowsFamily();

    private static string ResolveWindowsFamily()
    {
        string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (File.Exists(Path.Combine(fontsDir, "msyh.ttc")))
            return "Mako Sans CJK S";
        return "MakoSn";
    }

    internal static byte[] FontBytes()
    {
        string? linuxNotoPath = FindLinuxNotoCjkTtc();
        if (linuxNotoPath is not null)
            return ReplaceUtf16Be(File.ReadAllBytes(linuxNotoPath), "Noto", "Mako");
        if (OperatingSystem.IsLinux())
            throw new FileNotFoundException("未找到可用的 Linux Noto Sans CJK TTC 字体文件。", LinuxNotoCandidates[0]);

        string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string msyhPath = Path.Combine(fontsDir, "msyh.ttc");
        if (File.Exists(msyhPath))
            return ReplaceUtf16Be(File.ReadAllBytes(msyhPath), "Microsoft YaHei", "Mako Sans CJK S");

        string simsunPath = Path.Combine(fontsDir, "simsun.ttc");
        if (File.Exists(simsunPath))
            return ReplaceUtf16Be(File.ReadAllBytes(simsunPath), "SimSun", "MakoSn");

        throw new FileNotFoundException("未找到可用的测试 TTC 字体文件。", msyhPath);
    }

    private static byte[] ReplaceUtf16Be(byte[] bytes, string source, string target)
    {
        byte[] original = Encoding.BigEndianUnicode.GetBytes(source);
        byte[] replacement = Encoding.BigEndianUnicode.GetBytes(target);
        if (original.Length != replacement.Length)
            throw new ArgumentException("UTF-16BE 字体名称替换必须保持字节长度一致。");
        for (int index = 0; index <= bytes.Length - original.Length; index++)
            if (bytes.AsSpan(index, original.Length).SequenceEqual(original))
                replacement.CopyTo(bytes, index);
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
