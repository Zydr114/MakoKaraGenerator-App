using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>无 GUI 新建准备的固定歌词输入、合成设置及受信本机音频路径。</summary>
/// <param name="Name">工程名称，不可为空白。</param>
/// <param name="Composition">客户端已确认的合成设置。</param>
/// <param name="AudioPath">调用方有权读取的本机音频路径。</param>
/// <param name="LyricsSource">文件选择阶段捕获的完整歌词字节，不再次读取歌词路径。</param>
/// <param name="LyricsOptions">格式、编码和元素切分选择。</param>
/// <remarks>
/// 这是受信进程内入口，不是 HTTP DTO 或文件访问授权。音频仍由既有媒体加载器按路径探测和解码，
/// 本请求没有建立音频内容快照、资产句柄或后续播放／保存的字节一致性保证。
/// </remarks>
public sealed record NewProjectPreparationRequest(
    string Name, Composition Composition, string AudioPath, LyricsImportSource LyricsSource, LyricsImportOptions LyricsOptions);
