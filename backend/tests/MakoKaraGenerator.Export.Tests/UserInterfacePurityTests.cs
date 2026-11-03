using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MakoKaraGenerator.Export.Tests;

/// <summary>
/// 「界面不碰编解码器细节」这条规则的守卫测试。
/// </summary>
/// <remarks>
/// <para>
/// docs/development.md §12.2 要求"不要让 ViewModel 直接拼接 FFmpeg 命令行"，
/// docs/roadmap.md §10 的 <c>EXPORT-01</c> 验收是"UI 与 FFmpeg 解耦"。
/// 这条规则没有编译期约束——写一行 <c>"libx264"</c> 或一个 <c>"-crf"</c> 就能绕过——
/// 所以用测试把具体形状钉住：<b>编码器名与命令行参数不得出现在界面层</b>。
/// </para>
/// <para>
/// 刻意<b>不</b>禁止"FFmpeg"这个词本身：缺失时告诉用户"请安装 FFmpeg"是 ADR-0031 要求的
/// 用户可见提示，而演示音频的生成（<c>Demo/DemoContent.cs</c>）本来就要调它。
/// 要防的是"界面自己拼命令、自己挑编码器"，不是禁止提到这个名字。
/// </para>
/// </remarks>
public sealed class UserInterfacePurityTests
{
    private static readonly string[] BackendSourceDirectories = ["src", "tests"];

    /// <summary>界面层不得出现的编码器名与命令行参数。</summary>
    private static readonly string[] Forbidden =
    [
        "ffprobe",
        "libx264",
        "prores_ks",
        "-hide_banner",
        "-pix_fmt",
        "-movflags",
        "-crf",
        "-c:v",
        "-c:a",
        "pipe:0",
        "+faststart",
    ];

    [Fact]
    public void 界面层源码里不出现编码器名与命令行参数()
    {
        string repositoryRoot = FindRepositoryRoot();
        string applicationRoot = Path.Combine(repositoryRoot, "samples", "TechnicalSpike.App");

        if (!Directory.Exists(applicationRoot))
        {
            AssertBackendRepositoryBoundary(repositoryRoot, applicationRoot);
            return;
        }

        List<string> offenders = [];

        foreach (string file in EnumerateSources(applicationRoot))
        {
            string text = File.ReadAllText(file);
            string relative = Path.GetRelativePath(repositoryRoot, file);

            foreach (string token in Forbidden)
            {
                if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add(string.Create(CultureInfo.InvariantCulture, $"{relative}：出现了 '{token}'"));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "界面层碰了编解码器细节，应改走 IMediaEncoder / IMediaProbe 抽象："
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// 视图模型不得引用具体后端类型：换后端不该改界面。
    /// </summary>
    /// <remarks>
    /// 只查<b>类型名</b>，不查注释与文案：视图模型里说一句"FFmpeg 不可用"是产品文案，
    /// 而 <c>new FfmpegMediaEncoder()</c> 才是把自己焊死在后端上的写法。
    /// 组合根（<c>App.axaml.cs</c>）是唯一允许出现这些类型的地方。
    /// </remarks>
    [Fact]
    public void 视图模型不引用具体后端类型()
    {
        string[] backendTypes =
        [
            "FfmpegMediaEncoder",
            "FfmpegMediaProbe",
            "FfmpegPcmReader",
            "SkiaRenderPipeline",
            "SkiaTextLayoutEngine",
            "SoundFlowAudioPlayback",
        ];

        string repositoryRoot = FindRepositoryRoot();
        string viewModels = Path.Combine(repositoryRoot, "samples", "TechnicalSpike.App", "ViewModels");

        if (!Directory.Exists(viewModels))
        {
            AssertBackendRepositoryBoundary(repositoryRoot, Path.Combine(repositoryRoot, "samples", "TechnicalSpike.App"));
            return;
        }

        List<string> offenders = [];

        foreach (string file in EnumerateSources(viewModels))
        {
            string text = File.ReadAllText(file);
            string relative = Path.GetRelativePath(repositoryRoot, file);

            foreach (string type in backendTypes)
            {
                if (text.Contains(type, StringComparison.Ordinal))
                {
                    offenders.Add(string.Create(CultureInfo.InvariantCulture, $"{relative}：引用了 {type}"));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "视图模型引用了具体后端类型，应只认 VideoExporter / IRenderPipeline / IMediaProbe 抽象："
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// 当前 checkout 是无窗口后端仓库，Avalonia 编辑器已从 solution 和工作树移除。
    /// 如果未来重新引入界面目录，调用方会继续执行原有的源码扫描。
    /// </summary>
    private static void AssertBackendRepositoryBoundary(string repositoryRoot, string applicationRoot)
    {
        string solution = File.ReadAllText(Path.Combine(repositoryRoot, "MakoKaraGenerator.slnx"));
        string relativeApplicationPath = Path.GetRelativePath(repositoryRoot, applicationRoot)
            .Replace(Path.DirectorySeparatorChar, '/');

        Assert.DoesNotContain(relativeApplicationPath, solution, StringComparison.Ordinal);
        foreach (string root in BackendSourceDirectories.Select(name => Path.Combine(repositoryRoot, name)))
        {
            foreach (string project in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
            {
                var references = XDocument.Load(project).Descendants()
                    .Where(element => element.Name.LocalName is "PackageReference" or "ProjectReference" or "Reference")
                    .Select(element => (string?)element.Attribute("Include") ?? string.Empty);
                Assert.DoesNotContain(references, reference =>
                    reference.Contains("Avalonia", StringComparison.OrdinalIgnoreCase)
                    || reference.Contains("SukiUI", StringComparison.OrdinalIgnoreCase)
                    || reference.Contains("TechnicalSpike.App", StringComparison.OrdinalIgnoreCase));
            }

            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                string text = File.ReadAllText(file);
                Assert.False(Regex.IsMatch(text, @"^\s*(?:global\s+)?using\s+(?:Avalonia|SukiUI)(?:\.|;)", RegexOptions.Multiline),
                    $"后端源码引用了界面命名空间：{Path.GetRelativePath(repositoryRoot, file)}");
            }
        }
    }

    /// <summary>枚举源码文件（跳过构建产物与开发用的演示素材生成器）。</summary>
    /// <remarks>
    /// <c>Demo/DemoContent.cs</c> 是被排除的：它用 FFmpeg 合成一段演示音频（开发期工具，
    /// 不属界面功能），在导出层出现之前就写好了。把它搬进 <c>Media</c> 是后续的小清理，
    /// 但那是"移动既有代码"，不该顺手混进导出这一轮。
    /// </remarks>
    private static IEnumerable<string> EnumerateSources(string root) =>
        Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(static path => path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}Demo{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>从测试程序集所在目录往上找仓库根。</summary>
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        StringBuilder visited = new();

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MakoKaraGenerator.slnx")))
            {
                return directory.FullName;
            }

            visited.Append(directory.FullName).Append(" → ");
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"从测试目录往上找不到仓库根（MakoKaraGenerator.slnx）：{visited}");
    }
}
