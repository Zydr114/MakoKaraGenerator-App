namespace MakoKaraGenerator.Backend.Hosting;

/// <summary>创建固定设备的独立执行器；不修改已排队任务或原执行器。</summary>
public interface IBackendRenderBackendExecutor<out TExecutor>
{
    TExecutor WithRenderBackend(string renderBackend);
}
