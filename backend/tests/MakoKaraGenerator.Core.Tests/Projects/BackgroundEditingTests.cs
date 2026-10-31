using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Projects;

/// <summary>背景源编辑函数的测试（roadmap `EXPORT-04`）。</summary>
/// <remarks>
/// 每条"没变化"的用例都断言<b>引用相等</b>：编辑函数返回原实例是既有约定
/// （ADR-0025），它同时决定了撤销栈里会不会多出空步骤、自动保存会不会误判"有改动"。
/// </remarks>
public sealed class BackgroundEditingTests
{
    [Fact]
    public void 默认只有背景色()
    {
        Project project = Project.Create("工程");

        Assert.Equal(BackgroundKind.Color, project.Background.Kind);
        Assert.Null(project.Background.Path);
        Assert.False(project.Background.HasImage);
        Assert.Equal(BackgroundFit.Contain, project.Background.Fit);
    }

    [Fact]
    public void 设置图片()
    {
        Project edited = ProjectBackgroundEditor.SetImage(Project.Create("工程"), "/tmp/bg.png");

        Assert.Equal(BackgroundKind.Image, edited.Background.Kind);
        Assert.Equal("/tmp/bg.png", edited.Background.Path);
        Assert.True(edited.Background.HasImage);
    }

    [Fact]
    public void 重复设置同一张图不产生新实例()
    {
        Project once = ProjectBackgroundEditor.SetImage(Project.Create("工程"), "/tmp/bg.png");

        Assert.Same(once, ProjectBackgroundEditor.SetImage(once, "/tmp/bg.png"));
    }

    [Fact]
    public void 换图时保留适配方式()
    {
        Project project = ProjectBackgroundEditor.SetFit(
            ProjectBackgroundEditor.SetImage(Project.Create("工程"), "/tmp/a.png"),
            BackgroundFit.Cover);

        Project edited = ProjectBackgroundEditor.SetImage(project, "/tmp/b.png");

        Assert.Equal("/tmp/b.png", edited.Background.Path);
        Assert.Equal(BackgroundFit.Cover, edited.Background.Fit);
    }

    [Fact]
    public void 清除图片回到纯色()
    {
        Project project = ProjectBackgroundEditor.SetFit(
            ProjectBackgroundEditor.SetImage(Project.Create("工程"), "/tmp/a.png"),
            BackgroundFit.Cover);

        Project edited = ProjectBackgroundEditor.Clear(project);

        Assert.Equal(BackgroundKind.Color, edited.Background.Kind);
        Assert.Null(edited.Background.Path);

        // 没有图时适配方式也回到默认：留着它会让"再导入一张"的初始状态不可预期。
        Assert.Equal(BackgroundFit.Contain, edited.Background.Fit);
    }

    [Fact]
    public void 本来就没有图时清除不产生新实例()
    {
        Project project = Project.Create("工程");

        Assert.Same(project, ProjectBackgroundEditor.Clear(project));
    }

    [Fact]
    public void 设置适配方式()
    {
        Project project = ProjectBackgroundEditor.SetImage(Project.Create("工程"), "/tmp/a.png");

        Assert.Equal(BackgroundFit.Cover, ProjectBackgroundEditor.SetFit(project, BackgroundFit.Cover).Background.Fit);
        Assert.Same(project, ProjectBackgroundEditor.SetFit(project, BackgroundFit.Contain));
    }

    [Fact]
    public void 空白路径直接报错()
    {
        Assert.Throws<ArgumentException>(() => BackgroundSource.ForImage("  "));
        Assert.Throws<ArgumentException>(() => ProjectBackgroundEditor.SetImage(Project.Create("工程"), ""));
    }
}
