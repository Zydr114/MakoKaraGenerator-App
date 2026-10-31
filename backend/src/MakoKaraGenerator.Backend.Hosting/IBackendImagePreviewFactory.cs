using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>把已授权固定图片内容绑定到受管工程预览工厂；Host 不执行 native 解码。</summary>
public interface IBackendImagePreviewFactory
{
    /// <summary>验证固定输入并持有独立 bytes；每个 Runtime 会话仍拥有独立内存 resolver。</summary>
    IRenderFrameSessionFactory Bind(ProjectSnapshot snapshot, ReadOnlyMemory<byte> imageBytes);
}
