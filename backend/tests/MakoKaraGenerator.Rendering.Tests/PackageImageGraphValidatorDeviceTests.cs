using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>修改进程级渲染后端环境变量，必须与其他用例串行。</summary>
[CollectionDefinition("Render backend environment", DisableParallelization = true)]
public sealed class RenderBackendEnvironmentSerialTests;

[Collection("Render backend environment")]
public sealed class PackageImageGraphValidatorDeviceTests
{
    [Fact]
    public void 环境选择Vulkan时包图校验仍只用CPU且不另建Vulkan设备()
    {
        // GPU Worker 进程带 MAKO_RENDER_BACKEND=vulkan；另建设备会破坏"进程内恰好一个原设备"的预热证明。
        var graphs = EffectPackageImageGraphs.Read(EffectPackageGraphFixture.Contents());
        Assert.NotEmpty(graphs);
        string? previous = Environment.GetEnvironmentVariable("MAKO_RENDER_BACKEND");
        int devices = VulkanSkiaRenderDevice.DeviceInitializations, contexts = VulkanSkiaRenderDevice.ContextInitializations;
        try
        {
            Environment.SetEnvironmentVariable("MAKO_RENDER_BACKEND", "vulkan");
            new PackageImageGraphValidator().Validate(graphs);
        }
        finally { Environment.SetEnvironmentVariable("MAKO_RENDER_BACKEND", previous); }
        Assert.Equal(devices, VulkanSkiaRenderDevice.DeviceInitializations);
        Assert.Equal(contexts, VulkanSkiaRenderDevice.ContextInitializations);
    }
}
