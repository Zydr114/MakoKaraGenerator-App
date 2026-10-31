using MakoKaraGenerator.Application;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Tests;

/// <summary>项目包命名与定位规则的测试。</summary>
public sealed class ProjectPackageTests
{
    [Theory]
    [InlineData("春を連れて")]
    [InlineData("song")]
    [InlineData("我的工程 v2")]
    public void 常见名字都可用(string name) => Assert.Null(ProjectPackage.ValidateName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void 空名与能跳出父目录的名字会被拒绝(string? name) =>
        Assert.NotNull(ProjectPackage.ValidateName(name));

    [Fact]
    public void 已带后缀的名字会被拒绝而不是静默补第二遍()
    {
        // 若静默接受，目录会变成 song.mkgproj.mkgproj，用户意图不明，应当明说。
        Assert.NotNull(ProjectPackage.ValidateName("song.mkgproj"));
    }

    [Fact]
    public void 解析目录会补上推荐后缀()
    {
        string path = ProjectPackage.ResolveDirectory("/tmp", "春を連れて");

        Assert.Equal(Path.Combine("/tmp", "春を連れて.mkgproj"), path);
    }

    [Fact]
    public void 名字两端的空白会被丢掉()
    {
        string path = ProjectPackage.ResolveDirectory("/tmp", "  song  ");

        Assert.Equal(Path.Combine("/tmp", "song.mkgproj"), path);
    }

    [Fact]
    public void 名字不合规时解析直接抛错()
    {
        Assert.Throws<ArgumentException>(() => ProjectPackage.ResolveDirectory("/tmp", "a/b"));
        Assert.Throws<ArgumentException>(() => ProjectPackage.ResolveDirectory("/tmp", " "));
    }

    [Fact]
    public void 目录名可以反推回工程名()
    {
        Assert.Equal("song", ProjectPackage.NameFromDirectory("/tmp/song.mkgproj"));
        Assert.Equal("song", ProjectPackage.NameFromDirectory("/tmp/song.mkgproj/"));
        Assert.Equal("song", ProjectPackage.NameFromDirectory("/tmp/song"));
    }

    [Fact]
    public void 只有带project_json的目录才算项目包()
    {
        using TemporaryDirectory directory = new();

        Assert.False(ProjectPackage.Exists(directory.Path));

        directory.WriteFile("project.json", "{}");

        Assert.True(ProjectPackage.Exists(directory.Path));
    }
}
