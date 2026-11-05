namespace MakoKaraGenerator.Scripting.V2;

/// <summary>固定媒体输入的明确状态；不可用不表示黑色。</summary>
public enum MediaInputState
{
    /// <summary>输出按固定策略没有背景贡献。</summary>
    NoMedia,
    /// <summary>固定准备尝试尚未完成。</summary>
    Pending,
    /// <summary>固定文件特征或解析纯色已经可用。</summary>
    Available,
    /// <summary>固定准备尝试已经失败。</summary>
    Unavailable,
}

/// <summary>声明查询使用的唯一坐标域。</summary>
public enum MediaCoordinateSpace
{
    /// <summary>旋转后规范源的闭单位方形。</summary>
    SourceNormalized,
    /// <summary>固定画布的逻辑像素。</summary>
    CanvasPixels,
}

/// <summary>一个精确导出目录颁发的查询身份；default 不授予查询权限。</summary>
public readonly struct MediaQueryId
{
    private readonly object? _scope;
    internal MediaQueryId(string value, object scope) { Value = value; _scope = scope; }
    /// <summary>包声明的逻辑查询名，不是文件路径或全局权限。</summary>
    public string? Value { get; }
    internal bool BelongsTo(object scope) => ReferenceEquals(_scope, scope);
}

/// <summary>当前导出已经解析的固定查询；没有动态窗口或资源句柄。</summary>
public sealed class MediaQueryProfile
{
    internal MediaQueryProfile(MediaQueryId id, MediaCoordinateSpace space, long windowUs)
    {
        if (!Enum.IsDefined(space) || windowUs is < 0 or > 2_000_000)
            throw new ArgumentException("媒体查询需要固定坐标域及零至两秒整数微秒窗口。");
        Id = id; Space = space; WindowUs = windowUs;
    }
    /// <summary>当前目录颁发的身份。</summary>
    public MediaQueryId Id { get; }
    /// <summary>固定坐标域。</summary>
    public MediaCoordinateSpace Space { get; }
    /// <summary>固定拖尾窗口，整数微秒。</summary>
    public long WindowUs { get; }
}

/// <summary>宿主已固定输入的安全元数据；所有状态都有完整 profile 身份。</summary>
public sealed class MediaFeatureInputInfo
{
    internal MediaFeatureInputInfo(MediaInputState state, string inputRevision, string mappingKey, string profileDigest,
        string? sourceKey = null, string? artifactHash = null, string? diagnosticCode = null, string? noMediaReason = null)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        ValidateDigest(inputRevision); ValidateDigest(mappingKey); ValidateDigest(profileDigest);
        if (sourceKey is not null) ValidateDigest(sourceKey);
        if (artifactHash is not null) ValidateDigest(artifactHash);
        if (diagnosticCode is not null) ValidateCode(diagnosticCode);
        if (noMediaReason is not null) ValidateCode(noMediaReason);
        if (state == MediaInputState.NoMedia ? sourceKey is not null || artifactHash is not null || noMediaReason is null
            : sourceKey is null || noMediaReason is not null)
            throw new ArgumentException("媒体状态与源身份或无媒体原因不一致。");
        if (artifactHash is not null && state != MediaInputState.Available
            || state == MediaInputState.Unavailable && diagnosticCode is null)
            throw new ArgumentException("媒体状态与产物或失败码不一致。");
        State = state; InputRevision = inputRevision; MappingKey = mappingKey; ProfileDigest = profileDigest;
        SourceKey = sourceKey; ArtifactHash = artifactHash; DiagnosticCode = diagnosticCode; NoMediaReason = noMediaReason;
    }
    /// <summary>固定状态；不会随后台任务改变。</summary>
    public MediaInputState State { get; }
    /// <summary>规范 SHA-256 固定输入身份。</summary>
    public string InputRevision { get; }
    /// <summary>规范 SHA-256 映射身份。</summary>
    public string MappingKey { get; }
    /// <summary>全部状态共有的完整请求 profile 摘要。</summary>
    public string ProfileDigest { get; }
    /// <summary>文件或解析纯色的源身份；NoMedia 为空。</summary>
    public string? SourceKey { get; }
    /// <summary>仅可用文件分支具有完整产物字节摘要。</summary>
    public string? ArtifactHash { get; }
    /// <summary>有界安全诊断码，不包含异常详情或宿主路径。</summary>
    public string? DiagnosticCode { get; }
    /// <summary>NoMedia 的固定策略原因；其他状态为空。</summary>
    public string? NoMediaReason { get; }

    private static void ValidateDigest(string value)
    {
        if (value is null || value.Length != 64 || value.Any(static c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("媒体身份需要规范小写 SHA-256。", nameof(value));
    }
    private static void ValidateCode(string value)
    {
        if (value.Length is < 1 or > 64 || value.Any(static c => c is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z')
            and not (>= '0' and <= '9') and not '.' and not '_' and not '-'))
            throw new ArgumentException("媒体诊断或原因需要有界安全码。", nameof(value));
    }
}

/// <summary>线性预乘颜色均值；不是显示空间颜色或非预乘 RGB。</summary>
public readonly struct LinearPremulMean
{
    /// <summary>建立有限的线性预乘值；每个颜色分量不得超过 alpha。</summary>
    public LinearPremulMean(double r, double g, double b, double alpha)
    {
        Unit(r); Unit(g); Unit(b); Unit(alpha);
        if (r > alpha || g > alpha || b > alpha) throw new ArgumentException("预乘颜色不得超过 alpha。");
        R = r; G = g; B = b; Alpha = alpha;
    }
    /// <summary>线性预乘红均值。</summary>
    public double R { get; }
    /// <summary>线性预乘绿均值。</summary>
    public double G { get; }
    /// <summary>线性预乘蓝均值。</summary>
    public double B { get; }
    /// <summary>alpha 均值。</summary>
    public double Alpha { get; }
    internal static void Unit(double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
    }
}

/// <summary>未摆放、未合成底色的源统计；交叉矩不是均值的乘积。</summary>
public sealed class SourceMediaFeatureValues
{
    internal SourceMediaFeatureValues(LinearPremulMean mean, double meanPremulY, double meanSquaredPremulY,
        double meanAlphaTimesPremulY, double meanSquaredAlpha, double premulYVariance)
    {
        LinearPremulMean.Unit(meanPremulY); LinearPremulMean.Unit(meanSquaredPremulY);
        LinearPremulMean.Unit(meanAlphaTimesPremulY); LinearPremulMean.Unit(meanSquaredAlpha); LinearPremulMean.Unit(premulYVariance);
        Mean = mean; MeanPremulY = meanPremulY; MeanSquaredPremulY = meanSquaredPremulY;
        MeanAlphaTimesPremulY = meanAlphaTimesPremulY; MeanSquaredAlpha = meanSquaredAlpha; PremulYVariance = premulYVariance;
    }
    /// <summary>原始源的线性预乘均值。</summary>
    public LinearPremulMean Mean { get; }
    /// <summary>E(Yp)。</summary>
    public double MeanPremulY { get; }
    /// <summary>E(Yp²)，先逐样本求平方再平均。</summary>
    public double MeanSquaredPremulY { get; }
    /// <summary>E(aYp)，保留原始交叉矩。</summary>
    public double MeanAlphaTimesPremulY { get; }
    /// <summary>E(a²)。</summary>
    public double MeanSquaredAlpha { get; }
    /// <summary>沿正式数值内核规则求出的预乘亮度方差。</summary>
    public double PremulYVariance { get; }
}

/// <summary>整个画布查询区域经固定底色合成后的统计，包含留白面积。</summary>
public sealed class CanvasMediaFeatureValues
{
    internal CanvasMediaFeatureValues(LinearPremulMean mean, double meanPremulY, double meanSquaredPremulY, double premulYVariance)
    {
        LinearPremulMean.Unit(meanPremulY); LinearPremulMean.Unit(meanSquaredPremulY); LinearPremulMean.Unit(premulYVariance);
        Mean = mean; MeanPremulY = meanPremulY; MeanSquaredPremulY = meanSquaredPremulY; PremulYVariance = premulYVariance;
    }
    /// <summary>合成后的线性预乘均值。</summary>
    public LinearPremulMean Mean { get; }
    /// <summary>合成后的 E(Yp)。</summary>
    public double MeanPremulY { get; }
    /// <summary>合成后的 E(Yp²)。</summary>
    public double MeanSquaredPremulY { get; }
    /// <summary>合成后的预乘亮度方差。</summary>
    public double PremulYVariance { get; }
    /// <summary>均值 alpha 恰为一时，才可当作不透明可见背景。</summary>
    public bool IsOpaque => Mean.Alpha == 1d;
}

/// <summary>一次明确查询的不可变数值快照；不携带 reader、callback 或阶段引用。</summary>
public sealed class MediaFeatureSample
{
    internal MediaFeatureSample(MediaFeatureInputInfo input, MediaQueryProfile query, Rect2 rect, long projectTimeUs,
        SourceMediaFeatureValues? sourceValues = null, CanvasMediaFeatureValues? canvasValues = null)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(query);
        if (projectTimeUs < 0 || rect.Width <= 0 || rect.Height <= 0) throw new ArgumentException("媒体查询需要非负时刻和非空矩形。");
        bool source = query.Space == MediaCoordinateSpace.SourceNormalized;
        if (input.State == MediaInputState.Available
            ? source ? sourceValues is null || canvasValues is not null : canvasValues is null || sourceValues is not null
            : sourceValues is not null || canvasValues is not null)
            throw new ArgumentException("媒体状态或坐标域与数值分支不一致。");
        Input = input; Query = query; Rect = rect; ProjectTimeUs = projectTimeUs; SourceValues = sourceValues; CanvasValues = canvasValues;
    }
    /// <summary>本次查询所属的完整固定输入元数据。</summary>
    public MediaFeatureInputInfo Input { get; }
    /// <summary>已声明且解析完成的查询。</summary>
    public MediaQueryProfile Query { get; }
    /// <summary>未经过 float 转换的原请求矩形。</summary>
    public Rect2 Rect { get; }
    /// <summary>明确工程时刻，整数微秒。</summary>
    public long ProjectTimeUs { get; }
    /// <summary>仅 Available + SourceNormalized 非空。</summary>
    public SourceMediaFeatureValues? SourceValues { get; }
    /// <summary>仅 Available + CanvasPixels 非空。</summary>
    public CanvasMediaFeatureValues? CanvasValues { get; }
}
