using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Scripting.Execution;

/// <summary>固定编译代次的帧实例输入；真实包声明保留来源，独立源码接缝不制造包。</summary>
/// <param name="Id">实例身份。</param><param name="Artifact">控制器的固定 DLL 和 PDB；直接图为 null。</param>
/// <param name="EntryType">控制器作者入口；直接图为 null。</param><param name="Parameters">固定参数。</param>
/// <param name="Binding">输出范围。</param><param name="Capabilities">已声明输出能力。</param>
public sealed record CompiledFrameEffect(EffectInstanceId Id, ScriptCompilationResult? Artifact, string? EntryType,
    ParameterValues Parameters, EffectBinding Binding, EffectCapabilities Capabilities)
{
    /// <summary>组合根固定的包资源目录。</summary>
    public ResourceCatalog Resources { get; init; } = ResourceCatalog.Empty;
    /// <summary>固定 UInt64 种子，不使用系统随机或时间。</summary>
    public ulong Seed { get; init; }
    /// <summary>固定源订阅，与当前输出范围独立；指纹须覆盖此设置。</summary>
    public SourceSubscription Subscription { get; init; } = new SourceSubscription.BoundSources();
    /// <summary>声明本实例初始化前必须有实际音频分析；缺失、未完成或失败均阻止加载／导出。</summary>
    public bool RequiresAudio { get; init; }
    /// <summary>真实包导出声明；有值时生产输出必须符合它，不以粗粒度能力位扩大目标类别。</summary>
    public EffectExportDefinition? PackageDeclaration { get; init; }
    /// <summary>完整固定包语义，用于运行时图导出／绑定／参数检查，不包含可写状态。</summary>
    public EffectPackageDefinition? PackageDefinition { get; init; }
    /// <summary>从固定包与导出声明取得真实来源，不另存可修改的包身份。</summary>
    public EffectPackageIdentity? PackageIdentity => PackageDefinition is { } package && PackageDeclaration is { } export
        ? new(package.Contents.PackageId, package.Contents.Revision, export.Id) : null;
    /// <summary>控制器使用真实编译产物身份；没有 DLL 的直接图使用完整固定包修订。</summary>
    public string RuntimeHash => Artifact?.ArtifactHash ?? PackageIdentity?.Revision
        ?? throw new InvalidOperationException("运行实例缺少固定产物或包修订身份。");
    /// <summary>宿主固定的临时图元层覆盖；null保持各作者输出。</summary>
    public OverlayLayer? OverlayLayerOverride { get; init; }
    /// <summary>明确的包执行分派；独立旧源码接缝保持 v1，不按作者类名猜测。</summary>
    public bool IsV2 => PackageDefinition is { } package && EffectPackageProfiles.IsV2(package.Contents.Profile);
}

/// <summary>纯外观准备帧工厂；有状态入口或历史请求明确拒绝，不静默跳过回调。</summary>
public sealed class PureFrameSourceFactory : IEvaluatedFrameSourceFactory
{
    private readonly FrameSessionFactory _factory;
    /// <summary>固定输入和后端；指纹须包含参数／绑定／订阅／代次／资源／种子／音频修订与 required 声明／视觉预算。</summary>
    public PureFrameSourceFactory(ImmutableArray<CompiledFrameEffect> effects, string inputsFingerprint,
        Func<ILineGeometryProvider> geometryFactory, AudioFeatures? audio = null, VisualOutputBudget? visualBudget = null, PreparedSourceMaterials? sourceMaterials = null, ProjectMediaFeatureBinding? mediaBinding = null)
        => _factory = new(effects, inputsFingerprint, geometryFactory, audio, visualBudget, sourceMaterials: sourceMaterials, mediaBinding: mediaBinding);
    /// <inheritdoc/>
    public ValueTask<IEvaluatedFrameSource> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        => _factory.CreateAsync(snapshot, cancellationToken);
}
