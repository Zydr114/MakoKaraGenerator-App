using System.Collections.Immutable;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 一组具名样式预设，按 <see cref="LyricStylePreset.Id" /> 查找。
/// </summary>
/// <remarks>
/// <para>
/// 预设必须有一个统一的栖身处，否则"当前预设"这件事就没有稳定表示：界面、工程文件
/// 与渲染三方各自持有一份样式副本，改一处不会带动别处。
/// </para>
/// <para>
/// 本类型不可变；<see cref="Default" /> 里的起始预设只是让功能可用，
/// 后续（STYLE-01 剩余部分）会扩展成可编辑、可导入的预设库。
/// </para>
/// </remarks>
public sealed class LyricStylePresetLibrary
{
    /// <summary>默认预设的标识。</summary>
    /// <remarks>
    /// 新建工程会把它记成"基线来自默认预设"，界面因此能显示一致的来源，
    /// 而不是显示成"手工调整过"。
    /// </remarks>
    public const string DefaultPresetId = "default";

    private readonly ImmutableDictionary<string, LyricStylePreset> _byId;

    private LyricStylePresetLibrary(ImmutableArray<LyricStylePreset> presets)
    {
        Presets = presets;
        _byId = presets.ToImmutableDictionary(static preset => preset.Id, StringComparer.Ordinal);
    }

    /// <summary>空库。</summary>
    public static LyricStylePresetLibrary Empty { get; } = new([]);

    /// <summary>起始预设库。</summary>
    /// <remarks>
    /// 预设覆盖当前能渲染的全部档位：字体、字号、纵向位置、未唱/已唱颜色与渐变、
    /// 描边、阴影、不透明度与缩放。带渐变的两个预设是 STYLE-03 的可视入口，
    /// 也是阶段 4 出口要求的“未唱/当前/已唱渐变样式”。
    /// </remarks>
    public static LyricStylePresetLibrary Default { get; } = Create(
    [
        LyricStylePreset.Create(DefaultPresetId, "默认", LyricStyle.Default),
        LyricStylePreset.Create("karaoke-large", "大字", LyricStyle.Create(120f)),
        LyricStylePreset.Create(
            "karaoke-warm",
            "暖色",
            LyricStyle.Create(
                96f,
                unsungColor: new RgbaColor(0x6B, 0x51, 0x40),
                sungColor: new RgbaColor(0xFF, 0x8A, 0x3D))),

        // 未唱与已唱各自一对起止色，保证已唱覆盖能从未唱底色中清晰扫过（ADR-0032）。
        LyricStylePreset.Create(
            "karaoke-gradient",
            "渐变高亮",
            LyricStyle.Create(
                96f,
                unsungColor: new RgbaColor(0x5A, 0x5F, 0x70),
                sungColor: new RgbaColor(0xFF, 0x8A, 0x3D)) with
            {
                UnsungFill = new FillSpec
                {
                    Color = new RgbaColor(0x5A, 0x5F, 0x70),
                    Gradient = GradientSpec.Create([
                        new GradientStop(0d, new RgbaColor(0x4A, 0x50, 0x60)),
                        new GradientStop(1d, new RgbaColor(0x6E, 0x76, 0x8A)),
                    ]),
                },
                SungFill = new FillSpec
                {
                    Color = new RgbaColor(0xFF, 0x8A, 0x3D),
                    Gradient = GradientSpec.Create([
                        new GradientStop(0d, new RgbaColor(0x00, 0xE5, 0xFF)),
                        new GradientStop(1d, new RgbaColor(0x7C, 0x4D, 0xFF)),
                    ]),
                },
            }),

        // 描边 + 阴影：卡拉 OK 歌词最常见的“贴纸感”外观。
        LyricStylePreset.Create(
            "karaoke-outline",
            "描边阴影",
            LyricStyle.Create(
                104f,
                unsungColor: new RgbaColor(0xF2, 0xF4, 0xF8),
                sungColor: new RgbaColor(0xFF, 0xD1, 0x66)) with
            {
                Stroke = StrokeSpec.Create(new RgbaColor(0x10, 0x12, 0x18), 6f),
                Shadow = new ShadowSpec
                {
                    Color = new RgbaColor(0x00, 0x00, 0x00, 0xAA),
                    AngleDegrees = 90d,
                    DistancePx = 4f,
                    BlurRadiusPx = 8f,
                },
            }),
    ]);

    /// <summary>库里的预设，按创建顺序。</summary>
    public ImmutableArray<LyricStylePreset> Presets { get; }

    /// <summary>预设数量。</summary>
    public int Count => Presets.Length;

    /// <summary>创建一个预设库。</summary>
    /// <param name="presets">预设序列。</param>
    /// <returns>新的预设库。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="presets"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="ArgumentException">标识重复时抛出。</exception>
    public static LyricStylePresetLibrary Create(IEnumerable<LyricStylePreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);

        ImmutableArray<LyricStylePreset> array = [.. presets];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (LyricStylePreset preset in array)
        {
            if (!seen.Add(preset.Id))
            {
                throw new ArgumentException($"预设标识重复：'{preset.Id}'。", nameof(presets));
            }
        }

        return new LyricStylePresetLibrary(array);
    }

    /// <summary>按标识查找预设。</summary>
    /// <param name="id">预设标识；为 <see langword="null"/> 或空白时找不到。</param>
    /// <param name="preset">找到的预设。</param>
    /// <returns>找到时为 <see langword="true"/>。</returns>
    public bool TryFind(string? id, out LyricStylePreset preset)
    {
        if (!string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id, out LyricStylePreset? found))
        {
            preset = found;
            return true;
        }

        preset = null!;
        return false;
    }
}
