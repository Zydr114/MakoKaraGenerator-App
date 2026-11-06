using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>
/// Host 与 Worker 共用的媒体输入构造：背景声明、解析纯色档案与透明输出的无媒体输入。
/// 透明输出不读取背景，因此两端从同一快照得到逐字节相同的固定输入。
/// </summary>
public static class ProjectRuntimeMediaOutputs
{
    /// <summary>解析纯色与无媒体输入使用的固定档案。</summary>
    public static MediaProfileBundle AnalyticProfile { get; } = CreateAnalyticProfile();

    /// <summary>快照背景到媒体映射；透明与普通输出只差输出模式。</summary>
    public static MediaMapping FileMapping(ProjectSnapshot snapshot, MediaOutputMode mode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Composition composition = snapshot.Composition;
        return MediaMapping.Create(composition.CanvasWidth, composition.CanvasHeight, snapshot.Background.Fit,
            snapshot.Background.Playback, Linear(composition.BackgroundColor), mode);
    }

    /// <summary>透明输出的固定无媒体输入；快照没有媒体依赖时返回 null。</summary>
    public static MediaFeatureInput.NoMedia? TransparentNoMedia(ProjectSnapshot snapshot, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var plan = ProjectMediaDependencyResolver.Resolve(snapshot.Effects, snapshot.LyricStyle, snapshot.Lyrics,
            snapshot.LyricPresentation, snapshot.PresetApplications, token);
        return plan.Dependencies.IsEmpty ? null : MediaFeatureInput.CreateNoMedia(
            MediaInputContext.Create(AnalyticProfile, FileMapping(snapshot, MediaOutputMode.Transparent),
                plan.QueryCatalogDigest, plan.FixedDependencyDigest),
            MediaNoMediaReason.TransparentOutput, Declaration(snapshot));
    }

    /// <summary>Exact sRGB-encoded straight color to canonical linear premultiplied color.</summary>
    public static LinearPremulRgba Linear(RgbaColor color)
    {
        double alpha = color.Alpha / 255.0;
        static double Decode(byte value)
        {
            double encoded = value / 255.0;
            return encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
        }
        static double Clean(double value) => value <= 0 ? 0 : value;
        double r = Clean(Math.Min(alpha, Decode(color.Red) * alpha)), g = Clean(Math.Min(alpha, Decode(color.Green) * alpha)),
            b = Clean(Math.Min(alpha, Decode(color.Blue) * alpha));
        return new(r, g, b, Clean(alpha));
    }

    /// <summary>捕获的背景声明摘要：种类、媒体身份、适配、播放与底色。</summary>
    public static MediaDigest Declaration(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        BackgroundSource background = snapshot.Background;
        RgbaColor color = snapshot.Composition.BackgroundColor;
        string text = string.Join('\n', "mako.media.background-declaration.v1", background.Kind.ToString(),
            background.Media?.Hash ?? "-", (background.Media?.ByteLength ?? 0).ToString(CultureInfo.InvariantCulture),
            background.Fit.ToString(), background.Playback.ToString(),
            string.Create(CultureInfo.InvariantCulture, $"{color.Red},{color.Green},{color.Blue},{color.Alpha}"));
        return Digest(text);
    }

    private static MediaProfileBundle CreateAnalyticProfile()
    {
        var preliminary = new MediaProfileBundle(MediaProfilesV1.Presentation, MediaProfilesV1.DecoderPipeline, MediaProfilesV1.Color,
            MediaProfilesV1.Statistics, MediaProfilesV1.Quantization, MediaProfilesV1.Codec, MediaProfilesV1.Query, MediaProfilesV1.Gpu,
            Digest("mako.media.analytic-solid-decoder.v1"), Digest("mako.media.analytic-solid-color.v1\n" + MediaProfilesV1.Color), default);
        return new(preliminary.Presentation, preliminary.DecoderPipeline, preliminary.Color, preliminary.Statistics, preliminary.Quantization,
            preliminary.Codec, preliminary.Query, preliminary.Gpu, preliminary.DecoderIdentity, preliminary.ColorInterpretation,
            new MediaAbIdentityWriter().ComputeProfileDigest(preliminary));
    }

    private static MediaDigest Digest(string text) => new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
}
