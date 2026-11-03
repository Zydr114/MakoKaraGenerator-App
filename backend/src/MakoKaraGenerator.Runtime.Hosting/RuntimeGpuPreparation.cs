using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using SkiaSharp;

namespace MakoKaraGenerator.Runtime.Hosting;

/// <summary>未绑定作者输入的完整预热结果；原设备只允许向一个固定输入会话转移一次。</summary>
public sealed class RuntimeGpuPreparation : IDisposable
{
    private readonly object _gate = new();
    private ISkiaRenderDevice? _device, _boundDevice;
    private ImmutableArray<ImmutableArray<byte>> _referenceImages;
    private bool _consumed, _disposed;
    private readonly string _initializationId;
    private bool _boundDeviceVerified;

    private RuntimeGpuPreparation(ISkiaRenderDevice device, ScriptReferenceIdentity references,
        ImmutableArray<ImmutableArray<byte>> images, RuntimeRenderBackendProbeInfo deviceInfo,
        ImmutableArray<RuntimeLoadedAssemblyIdentity> runtimeDependencies)
    {
        _initializationId = ((VulkanSkiaRenderDevice)device).InitializationId;
        _device = device; References = references; _referenceImages = images; DeviceInfo = deviceInfo;
        PreparationId = Guid.NewGuid().ToString("N");
        ReferenceBytes = images.Sum(image => (long)image.Length);
        RuntimeDependencies = runtimeDependencies;
    }

    /// <summary>同一引用字节快照计算的固定 SDK／运行参考身份。</summary>
    public ScriptReferenceIdentity References { get; }
    /// <summary>对本实例持有的原设备实际绘制、提交及读回所得的摘要。</summary>
    public RuntimeRenderBackendProbeInfo DeviceInfo { get; }
    /// <summary>仅供私有握手证明初始化与绑定使用同一个准备结果。</summary>
    public string PreparationId { get; }
    /// <summary>备用阶段实际保留的参考程序集字节。</summary>
    public long ReferenceBytes { get; }
    /// <summary>原准备设备或已移交设备仍由本次准备对象持有且支持共享预览。</summary>
    public bool IsDeviceAlive
    { get { lock (_gate) return !_disposed && (_device ?? _boundDevice) is VulkanSkiaRenderDevice { SupportsGpuFrames: true }; } }
    /// <summary>读取原设备初始化及一次移交状态的不可变证明。</summary>
    public RuntimeGpuPreparationProof Proof
    {
        get { lock (_gate) return new(PreparationId, _initializationId, VulkanSkiaRenderDevice.DeviceInitializations,
            VulkanSkiaRenderDevice.ContextInitializations, _consumed, _boundDeviceVerified); }
    }
    /// <summary>真正用于执行的 SDK／运行依赖；与 reference-only 编译参考程序集分开登记。</summary>
    public ImmutableArray<RuntimeLoadedAssemblyIdentity> RuntimeDependencies { get; }
    /// <summary>一次性所有权转移是否已经发生；不得以其为依据再次绑定输入。</summary>
    public bool Consumed { get { lock (_gate) return _consumed; } }

    /// <summary>不读取业务输入、不启动 consumer socket；保留原引用快照和 Vulkan／Skia 设备。</summary>
    public static RuntimeGpuPreparation Create(string referenceDirectory, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var references = ScriptReferenceIdentity.Read(referenceDirectory, out var images);
        var runtimeDependencies = PrepareRuntimeDependencies();
        token.ThrowIfCancellationRequested();
        ISkiaRenderDevice? device = null;
        try
        {
            device = SkiaRenderDeviceFactory.Create(SkiaRenderBackendOptions.Parse("vulkan") with { EnableGpuPreview = true });
            if (device is not VulkanSkiaRenderDevice)
                throw new InvalidOperationException("GPU 预热没有取得 Vulkan 原设备。");
            var info = RuntimeRenderBackendProbe.ProbeRetainedDevice(device, "vulkan", token);
            token.ThrowIfCancellationRequested();
            var prepared = new RuntimeGpuPreparation(device, references, images, info, runtimeDependencies);
            device = null;
            return prepared;
        }
        finally { device?.Dispose(); }
    }

    private static ImmutableArray<RuntimeLoadedAssemblyIdentity> PrepareRuntimeDependencies()
    {
        // These are the actual implementation assemblies selected by the Worker
        // host. Never Assembly.Load the BCL reference-only script-refs directory.
        Type[] required = [typeof(TokenId), typeof(ProjectSnapshot), typeof(RenderRequest),
            typeof(IRenderFrameSession), typeof(SkiaRenderPipeline), typeof(ProjectRenderSessionFactory),
            typeof(ScriptArtifactDescriptor), typeof(IVideoFrameProvider), typeof(SKSurface), typeof(SharpVk.Device)];
        foreach (Type type in required) RuntimeHelpers.RunClassConstructor(type.TypeHandle);
        // Prepare a bounded author-free runtime entry point. Business package ALC,
        // input resources/uploads, compiled exports and layout indexes remain cold.
        var requestFactory = typeof(RenderRequest).GetMethod(nameof(RenderRequest.At), [typeof(ProjectSnapshot), typeof(long)])
            ?? throw new MissingMethodException(nameof(RenderRequest.At));
        RuntimeHelpers.PrepareMethod(requestFactory.MethodHandle);
        return [.. required.Select(type => type.Assembly).Distinct().OrderBy(assembly => assembly.FullName, StringComparer.Ordinal)
            .Select(assembly => new RuntimeLoadedAssemblyIdentity(assembly.FullName
                ?? throw new InvalidOperationException("运行依赖没有程序集身份。"), assembly.ManifestModule.ModuleVersionId.ToString("N")))];
    }

    // Called only by ProjectRenderSessionFactory in this assembly. Returning the
    // exact device transfers its disposal responsibility to ProjectRenderSession.
    internal ISkiaRenderDevice TakeDevice()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_consumed || _device is null) throw new InvalidOperationException("GPU 预热结果已绑定过业务输入。");
            var device = _device;
            if (((VulkanSkiaRenderDevice)device).InitializationId != _initializationId)
                throw new InvalidOperationException("GPU 预热原设备身份已改变。");
            _boundDevice = device; _device = null; _consumed = true; _boundDeviceVerified = true;
            // Loading inputs no longer needs the reference byte snapshot once the
            // host-compiled artifact provider has admitted this exact reference set.
            _referenceImages = [];
            return device;
        }
    }

    /// <summary>只释放尚未转移的原设备；已经绑定的设备仍由业务会话负责。</summary>
    public void Dispose()
    {
        ISkiaRenderDevice? device;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; device = _device; _device = null; _referenceImages = [];
        }
        device?.Dispose();
    }
}

/// <summary>实际已加载执行程序集身份；不包含机器路径或 reference-only DLL。</summary>
public sealed record RuntimeLoadedAssemblyIdentity(string AssemblyIdentity, string ModuleVersionId);

/// <summary>原 Vulkan device/Skia context 与单次业务绑定的只读证明。</summary>
/// <param name="PreparationId">本次准备身份。</param>
/// <param name="InitializationId">原设备及上下文实例身份。</param>
/// <param name="DeviceInitializations">本 Worker 实际成功创建 Vulkan device 的次数。</param>
/// <param name="ContextInitializations">本 Worker 实际成功创建 Skia context 的次数。</param>
/// <param name="Consumed">准备结果是否已被业务尝试消费。</param>
/// <param name="OriginalDeviceBound">原设备是否已移交业务会话。</param>
public sealed record RuntimeGpuPreparationProof(string PreparationId, string InitializationId,
    int DeviceInitializations, int ContextInitializations, bool Consumed, bool OriginalDeviceBound);
