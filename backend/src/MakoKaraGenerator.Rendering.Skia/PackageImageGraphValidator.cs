using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>正式 CPU 编译每个包图，包括不可达节点；及时释放全部验证对象。</summary>
/// <remarks>显式使用 CPU 设备，不随 <c>MAKO_RENDER_BACKEND</c> 另建 Vulkan 设备；
/// GPU Worker 的预热证明要求进程内只有一个原设备。</remarks>
public sealed class PackageImageGraphValidator : IPackageImageGraphValidator
{
    /// <inheritdoc/>
    public void Validate(ImmutableDictionary<string, EffectPackageImageGraph> graphs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graphs);
        if (graphs.IsEmpty) { cancellationToken.ThrowIfCancellationRequested(); return; }
        using var device = SkiaRenderDeviceFactory.Create(new(SkiaRenderBackend.Cpu));
        foreach (var pair in graphs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { using var graph = CompiledImageGraph.Compile(pair.Value.Plan, device, cancellationToken: cancellationToken); }
            catch (ImageGraphException error) { throw new EffectPackageException(error.Code, pair.Key + "." + error.Field, error.Message, error); }
            catch (ShaderProgramException error) { throw new EffectPackageException(error.Code, pair.Key + "." + error.Field, error.Message, error); }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
