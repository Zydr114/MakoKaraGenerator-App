using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Projects;

/// <summary>视频背景的编辑函数测试（roadmap P1「背景视频完整支持」）。</summary>
public sealed class BackgroundVideoEditingTests
{
    [Fact]
    public void 设视频后种类路径与默认策略都正确()
    {
        Project edited = ProjectBackgroundEditor.SetVideo(Project.Create("工程"), "/tmp/a.mp4");

        Assert.Equal(BackgroundKind.Video, edited.Background.Kind);
        Assert.Equal("/tmp/a.mp4", edited.Background.Path);
        Assert.Equal(BackgroundPlaybackMode.Loop, edited.Background.Playback);
        Assert.True(edited.Background.HasVideo);
        Assert.False(edited.Background.HasImage);
        Assert.True(edited.Background.HasMedia);
    }

    [Fact]
    public void 同一段视频重复设置返回原实例()
    {
        Project once = ProjectBackgroundEditor.SetVideo(Project.Create("工程"), "/tmp/a.mp4");

        Assert.Same(once, ProjectBackgroundEditor.SetVideo(once, "/tmp/a.mp4"));
    }

    [Fact]
    public void 图片换成视频时保留适配方式()
    {
        Project project = ProjectBackgroundEditor.SetFit(
            ProjectBackgroundEditor.SetImage(Project.Create("工程"), "/tmp/a.png"),
            BackgroundFit.Cover);

        Project edited = ProjectBackgroundEditor.SetVideo(project, "/tmp/a.mp4");

        Assert.Equal(BackgroundKind.Video, edited.Background.Kind);
        Assert.Equal(BackgroundFit.Cover, edited.Background.Fit);
    }

    [Fact]
    public void 视频换成图片时适配方式与播放方式都保留()
    {
        Project project = ProjectBackgroundEditor.SetPlaybackMode(
            ProjectBackgroundEditor.SetFit(
                ProjectBackgroundEditor.SetVideo(Project.Create("工程"), "/tmp/a.mp4"),
                BackgroundFit.Original),
            BackgroundPlaybackMode.FreezeLastFrame);

        Project edited = ProjectBackgroundEditor.SetImage(project, "/tmp/b.png");

        Assert.Equal(BackgroundFit.Original, edited.Background.Fit);
        Assert.Equal(BackgroundPlaybackMode.FreezeLastFrame, edited.Background.Playback);
    }

    [Fact]
    public void 设置播放方式且同值返回原实例()
    {
        Project project = ProjectBackgroundEditor.SetVideo(Project.Create("工程"), "/tmp/a.mp4");
        Project edited = ProjectBackgroundEditor.SetPlaybackMode(project, BackgroundPlaybackMode.FreezeLastFrame);

        Assert.Equal(BackgroundPlaybackMode.FreezeLastFrame, edited.Background.Playback);
        Assert.Same(edited, ProjectBackgroundEditor.SetPlaybackMode(edited, BackgroundPlaybackMode.FreezeLastFrame));
        Assert.NotSame(project, edited);
    }

    [Fact]
    public void 清除会同时清掉路径与两个偏好()
    {
        Project project = ProjectBackgroundEditor.SetPlaybackMode(
            ProjectBackgroundEditor.SetFit(
                ProjectBackgroundEditor.SetVideo(Project.Create("工程"), "/tmp/a.mp4"),
                BackgroundFit.Cover),
            BackgroundPlaybackMode.FreezeLastFrame);

        Project cleared = ProjectBackgroundEditor.Clear(project);

        Assert.Equal(BackgroundKind.Color, cleared.Background.Kind);
        Assert.Null(cleared.Background.Path);
        Assert.Equal(BackgroundFit.Contain, cleared.Background.Fit);
        Assert.Equal(BackgroundPlaybackMode.Loop, cleared.Background.Playback);
        Assert.Same(cleared, ProjectBackgroundEditor.Clear(cleared));
    }

    [Fact]
    public void 空白路径被拒绝()
    {
        Assert.Throws<ArgumentException>(() => ProjectBackgroundEditor.SetVideo(Project.Create("工程"), " "));
    }
}
