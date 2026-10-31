using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>仅可信Host可绑定已固定授权的工程输入。</summary>
public interface IBackendRuntimePreviewFactory
{
    IRenderFrameSessionFactory Bind(BackendPreparedProjectRuntimeInput input);
}
