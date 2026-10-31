using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Commands;

/// <summary>命令工厂与合成命令的测试。</summary>
public sealed class ProjectCommandTests
{
    [Fact]
    public void 委托命令按标识与合并标志构造()
    {
        IProjectCommand command = ProjectCommand.Create("rename", project => project with { Name = "B" }, mergeable: true);

        Assert.Equal("rename", command.Name);
        Assert.True(command.Mergeable);
        Assert.Equal("B", command.Apply(Project.Create("A")).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 标识不能为空(string name) =>
        Assert.Throws<ArgumentException>(() => ProjectCommand.Create(name, static project => project));

    [Fact]
    public void 应用函数不能为空() =>
        Assert.Throws<ArgumentNullException>(() => ProjectCommand.Create("rename", null!));

    [Fact]
    public void 合成命令按顺序应用()
    {
        IProjectCommand composite = ProjectCommand.Combine(
            "two-steps",
            ProjectCommand.Create("rename", project => project with { Name = "B" }),
            ProjectCommand.Create("widen", project => project with { Composition = project.Composition with { CanvasWidth = 1280 } }));

        Project result = composite.Apply(Project.Create("A"));

        Assert.Equal("B", result.Name);
        Assert.Equal(1280, result.Composition.CanvasWidth);
    }

    [Fact]
    public void 合成命令是完整动作不参与合并()
    {
        // 合成命令本身就是"一次用户动作"，不该被时间窗再并进上一步。
        IProjectCommand composite = ProjectCommand.Combine(
            "two-steps",
            ProjectCommand.Create("rename", project => project with { Name = "B" }));

        Assert.False(composite.Mergeable);
    }

    [Fact]
    public void 前面一步没有变化时后面的仍然生效()
    {
        IProjectCommand composite = ProjectCommand.Combine(
            "mixed",
            ProjectCommand.Create("noop", static project => project),
            ProjectCommand.Create("rename", project => project with { Name = "B" }));

        Assert.Equal("B", composite.Apply(Project.Create("A")).Name);
    }

    [Fact]
    public void 合成命令至少要有一条()
    {
        Assert.Throws<ArgumentException>(() => ProjectCommand.Combine("empty"));
        Assert.Throws<ArgumentException>(() => ProjectCommand.Combine(" "));
    }

    [Fact]
    public void 命令里不允许有空项()
    {
        IProjectCommand first = ProjectCommand.Create("rename", project => project with { Name = "B" });

        Assert.Throws<ArgumentNullException>(() => ProjectCommand.Combine("bad", first, null!));
    }
}
