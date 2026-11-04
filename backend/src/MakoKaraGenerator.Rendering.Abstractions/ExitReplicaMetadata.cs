using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>退出装载的固定来源及隐藏左极限外观；不含作者输出或设备资源。</summary>
/// <param name="HideTimeUs">隐藏边界。</param>
/// <param name="LoadOrdinal">固定装载排序。</param>
/// <param name="SourceNodePath">隐藏左极限的源根到文字路径。</param>
/// <param name="LineStyle">隐藏左极限的完整源行外观及持久来源。</param>
/// <param name="TokenStyles">隐藏左极限各源元素的完整外观及来源。</param>
public sealed record ExitReplicaMetadata(long HideTimeUs, int LoadOrdinal, ImmutableArray<string> SourceNodePath,
    PresetStyleResolution LineStyle, ImmutableDictionary<TokenId, PresetStyleResolution> TokenStyles);
