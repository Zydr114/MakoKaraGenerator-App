using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Core.Tests.Persistence;

/// <summary>媒体路径在"内存"与"写盘"两种形式之间的换算测试。</summary>
public sealed class ProjectMediaPathTests
{
    [Fact]
    public void 工程目录内的音频存成相对路径()
    {
        string stored = ProjectMediaPath.ToStored("/work/song.mkgproj/assets/audio.mp3", "/work/song.mkgproj")
            ?? string.Empty;

        Assert.Equal(Path.Combine("assets", "audio.mp3"), stored);
    }

    [Fact]
    public void 工程目录外的音频保持绝对路径()
    {
        // 跨目录树的相对路径（一串 ..）既不好看也一样搬不走，不如老实存绝对路径。
        string stored = ProjectMediaPath.ToStored("/home/user/Music/song.mp3", "/work/song.mkgproj")
            ?? string.Empty;

        Assert.Equal("/home/user/Music/song.mp3", stored);
    }

    [Fact]
    public void 用户手写的相对路径原样保留()
    {
        string stored = ProjectMediaPath.ToStored(Path.Combine("..", "shared", "song.mp3"), "/work/song.mkgproj")
            ?? string.Empty;

        Assert.Equal(Path.Combine("..", "shared", "song.mp3"), stored);
    }

    [Fact]
    public void 打开时把相对路径解析成绝对路径()
    {
        string absolute = ProjectMediaPath.ToAbsolute(Path.Combine("assets", "audio.mp3"), "/work/song.mkgproj")
            ?? string.Empty;

        Assert.Equal(Path.GetFullPath(Path.Combine("/work/song.mkgproj", "assets", "audio.mp3")), absolute);
    }

    [Fact]
    public void 绝对路径在打开时保持原样()
    {
        Assert.Equal("/home/user/Music/song.mp3", ProjectMediaPath.ToAbsolute("/home/user/Music/song.mp3", "/work/p"));
    }

    [Fact]
    public void 空路径在两侧都保持为空()
    {
        Assert.Null(ProjectMediaPath.ToStored(null, "/work/p"));
        Assert.Null(ProjectMediaPath.ToAbsolute(null, "/work/p"));
    }
}
