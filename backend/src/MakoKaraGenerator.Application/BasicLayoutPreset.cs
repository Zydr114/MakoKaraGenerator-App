using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application;

/// <summary>基础界面只选择布局，不包含样式或效果。</summary>
public enum BasicLayoutPreset
{
    /// <summary>单行居中。</summary>
    SingleCentered,
    /// <summary>交错双行，两端锚点。</summary>
    KaraokeDouble,
    /// <summary>双行居中。</summary>
    DoubleCentered,
    /// <summary>三行居中。</summary>
    TripleCentered,
}

/// <summary>固定画布坐标、锚点和槽位数，不包含界面标签。</summary>
public static class BasicLayoutPresets
{
    /// <summary>预设的精确槽位几何。</summary>
    public static ScenePlacement[] Placements(BasicLayoutPreset preset) => preset switch
    {
        BasicLayoutPreset.SingleCentered => [ScenePlacement.Create(new(.5, .75))],
        BasicLayoutPreset.KaraokeDouble => [ScenePlacement.Create(new(.05, .6), ScenePlacementAnchor.LeftCenter),
            ScenePlacement.Create(new(.95, .8), ScenePlacementAnchor.RightCenter)],
        BasicLayoutPreset.DoubleCentered => [ScenePlacement.Create(new(.5, .55)), ScenePlacement.Create(new(.5, .77))],
        BasicLayoutPreset.TripleCentered => [ScenePlacement.Create(new(.5, .55)), ScenePlacement.Create(new(.5, .66)), ScenePlacement.Create(new(.5, .77))],
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };
}
