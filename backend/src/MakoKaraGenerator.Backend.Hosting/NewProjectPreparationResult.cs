using MakoKaraGenerator.Core.Lyrics.Import;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>无 GUI 新建准备的中立拒绝代码。</summary>
public enum NewProjectPreparationErrorCode
{
    /// <summary>名称、合成设置或必要输入不合法。</summary>
    InvalidRequest,
    /// <summary>媒体没有音频流，或实际解码结果没有正时长。</summary>
    NoAudioStream,
    /// <summary>受信本机音频读取、探测或解码失败。</summary>
    AudioPreparationFailed,
    /// <summary>固定歌词输入的识别或解析失败。</summary>
    LyricsImportFailed,
    /// <summary>歌词解析没有产生可导入行。</summary>
    EmptyLyrics,
}

/// <summary>由客户端映射本地化提示的新建准备失败。</summary>
/// <param name="Code">稳定的中立错误代码。</param>
/// <param name="Field">相关请求字段；不存在单一字段时为空。</param>
/// <param name="Detail">受信本机调用的可读原因；不包含异常对象或堆栈。</param>
/// <remarks>Detail 可能含本机媒体错误的路径，未来网络入口须另行映射并脱敏，不能直接当作 HTTP 错误 DTO。</remarks>
public sealed record NewProjectPreparationError(NewProjectPreparationErrorCode Code, string? Field = null, string? Detail = null);

/// <summary>尚未发布到任何工作区的新工程、波形及完整导入报告。</summary>
/// <remarks>
/// 数据均为托管结果，不持有播放器、物理设备、临时媒体文件或待运行的解码进程，因而不提供空的释放接口。
/// 桌面调用方仍须准备播放器和正式字体／资源，在全部门禁成功后才发布工程。
/// </remarks>
public sealed class PreparedNewProjectData
{
    internal PreparedNewProjectData(Project project, LoadedWaveform waveform, LyricsImportReport report)
    {
        Project = project; Waveform = waveform; Report = report;
    }

    /// <summary>使用现有默认基线及固定歌词候选组装的领域工程。</summary>
    public Project Project { get; }
    /// <summary>已经完成探测、解码及峰值聚合的托管数据。</summary>
    public LoadedWaveform Waveform { get; }
    /// <summary>共享歌词准备返回的完整结构化报告，不预先格式化为 UI 文本。</summary>
    public LyricsImportReport Report { get; }
}

/// <summary>一次准备结果；成功与失败互斥，取消通过任务的取消异常传递。</summary>
public sealed class NewProjectPreparationResult
{
    internal NewProjectPreparationResult(PreparedNewProjectData candidate) => Candidate = candidate;
    internal NewProjectPreparationResult(NewProjectPreparationError error) => Error = error;

    /// <summary>是否完成全部后端准备门禁。</summary>
    public bool Accepted => Candidate is not null;
    /// <summary>成功候选；拒绝时为空。调用方拥有是否采用结果的决定及会话门禁。</summary>
    public PreparedNewProjectData? Candidate { get; }
    /// <summary>拒绝原因；成功时为空。</summary>
    public NewProjectPreparationError? Error { get; }
}
