using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.V2;

/// <summary>整行整形产出的一个真实不可拆绘制单元；RubyId 为空表示基础字，UnitIndex 不按 UTF-16 拆字。</summary>
public sealed record TextClusterTarget
{
    /// <summary>固定源族、装载、已有绘制单元身份、整形单元序号和可选真实注音。</summary>
    public TextClusterTarget(TokenId tokenId, BindingId bindingId, DrawUnitId drawUnitId, int unitIndex, RubyId? rubyId = null)
    {
        if (!tokenId.IsValid || !bindingId.IsValid || !drawUnitId.IsValid || unitIndex < 0 || rubyId is { IsValid: false })
            throw new ArgumentException("字簇需要真实源、装载和整形单元身份。", nameof(tokenId));
        TokenId = tokenId; BindingId = bindingId; DrawUnitId = drawUnitId; UnitIndex = unitIndex; RubyId = rubyId;
    }
    /// <summary>源字族；跨源元素单元使用首个所属元素，完整所属范围另见几何。</summary>
    public TokenId TokenId { get; }
    /// <summary>真实装载。</summary>
    public BindingId BindingId { get; }
    /// <summary>既有整形绘制单元的稳定身份。</summary>
    public DrawUnitId DrawUnitId { get; }
    /// <summary>完整基础行或完整 RubyRun 的整形单元序号。</summary>
    public int UnitIndex { get; }
    /// <summary>真实注音 Run；基础字为空。</summary>
    public RubyId? RubyId { get; }
}

/// <summary>真实不可拆字簇的只读源关联和几何；跨 token 保留完整所属范围并明确不可独立。</summary>
public sealed record TextClusterGeometrySample
{
    /// <summary>保存宿主整行整形结果；关联区间只说明来源，不用于裁剪字形。</summary>
    public TextClusterGeometrySample(TextClusterTarget target, ImmutableArray<TokenId> ownerTokens,
        int utf16Start, int utf16Length, GeometrySample geometry)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(geometry);
        if (ownerTokens.IsDefaultOrEmpty || ownerTokens.Any(static id => !id.IsValid) || !ownerTokens.Contains(target.TokenId)
            || ownerTokens.Distinct().Count() != ownerTokens.Length || utf16Start < 0 || utf16Length <= 0
            || ownerTokens.Length > 1 && geometry.CanTransformIndependently)
            throw new ArgumentException("字簇所属范围或源关联无效。", nameof(ownerTokens));
        Target = target; OwnerTokens = ownerTokens; Utf16Start = utf16Start; Utf16Length = utf16Length; Geometry = geometry;
    }
    /// <summary>真实单元目标。</summary>
    public TextClusterTarget Target { get; }
    /// <summary>完整真实源元素范围；多个元素时禁止独立修改。</summary>
    public ImmutableArray<TokenId> OwnerTokens { get; }
    /// <summary>整行或 RubyRun 内的 UTF-16 关联起点。</summary>
    public int Utf16Start { get; }
    /// <summary>完整不可拆单元的源关联长度。</summary>
    public int Utf16Length { get; }
    /// <summary>真实字形几何和不可独立原因。</summary>
    public GeometrySample Geometry { get; }
    /// <summary>宿主在父族／基础字和本单元变换合成后保存新的只读几何。</summary>
    public TextClusterGeometrySample WithGeometry(GeometrySample geometry) => new(Target, OwnerTokens, Utf16Start, Utf16Length, geometry);
}

/// <summary>显式只影响基础字的局部仿射，关联 Ruby 保持父族基线。</summary>
public sealed record BaseTokenTransform(BaseTokenTarget Target, Affine2 LocalMatrix);
/// <summary>显式只影响基础字的外观；关联 Ruby 保持父族外观。</summary>
public sealed record BaseTokenStyle(BaseTokenTarget Target, StyleUpdate Update);
/// <summary>一个真实不可拆字簇的局部仿射；沿源族继承，基础字还继承 BaseOnly 变换。</summary>
public sealed record TextClusterTransform(TextClusterTarget Target, Affine2 LocalMatrix);
