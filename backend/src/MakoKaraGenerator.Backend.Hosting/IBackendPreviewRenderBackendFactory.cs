using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>纯绑定预览设备选择；返回工厂须保留原固定输入和资源获取规则。</summary>
public interface IBackendPreviewRenderBackendFactory
{
    string RenderBackend { get; }
    IRenderFrameSessionFactory WithRenderBackend(string renderBackend);
}
