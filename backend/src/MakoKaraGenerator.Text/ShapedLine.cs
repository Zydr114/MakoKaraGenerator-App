using System.Collections.Immutable;

namespace MakoKaraGenerator.Text;

/// <summary>整行整形产出的一个字形；位置和墨迹相对于行左端及基线，不含装饰。</summary>
/// <param name="GlyphId">字体内部字形 ID，不是 Unicode 码点。</param>
/// <param name="X">基线横坐标。</param>
/// <param name="Y">基线纵坐标。</param>
/// <param name="InkBounds">实际非空墨迹；空白字形为空。</param>
public readonly record struct ShapedGlyph(ushort GlyphId, float X, float Y, TextInkBounds? InkBounds);

/// <summary>不可拆字簇的固定绘制单元；字形和位置只来自整行整形，不再整形子串。</summary>
/// <param name="Utf16Start">源文本起点；仅作字簇关联，不用来裁切字形。</param>
/// <param name="Utf16Length">完整字簇的源长度。</param>
/// <param name="FamilyName">实际字体。</param>
/// <param name="X">字簇 Advance 左端。</param>
/// <param name="Advance">字簇 Advance 宽度。</param>
/// <param name="Glyphs">原始字形 ID 与完整行坐标；不可变。</param>
/// <param name="Ascent">实际字体上升部，相对基线；通常为负。</param>
/// <param name="Descent">实际字体下降部，相对基线；通常为正。</param>
public sealed record ShapedDrawUnit(int Utf16Start, int Utf16Length, string FamilyName,
    float X, float Advance, ImmutableArray<ShapedGlyph> Glyphs, float Ascent = 0, float Descent = 0)
{
    /// <summary>源文本终点，不包含。</summary>
    public int Utf16End => checked(Utf16Start + Utf16Length);
}

/// <summary>
/// 一个已布局的文本元素（Unicode grapheme cluster）及其在原始字符串中的位置。
/// </summary>
/// <remarks>
/// <para>
/// "逐字"的单位就是这里的文本元素，而不是 <see cref="ShapedTextElement.Utf16Length"/>
/// 为 1 的 UTF-16 代码单元。一个 emoji 的 ZWJ 序列、
/// 一个"字母 + 组合音标"都可以是单个元素，且长度大于 1。
/// </para>
/// <para>
/// 一个元素可能对应多个 glyph（例如 ZWJ emoji 由多个字形拼成），
/// 此时 <see cref="Advance"/> 是该元素占据的全部宽度，<see cref="GlyphCount"/> 记录字形个数。
/// 逐字高亮按元素而不是按字形推进，视觉上才不会把连字拆开。
/// </para>
/// </remarks>
/// <param name="Utf16Start">在原始字符串中的起始下标。</param>
/// <param name="Utf16Length">占用的 UTF-16 代码单元数量。</param>
/// <param name="Text">元素的原始文本。</param>
/// <param name="X">相对整行左端的横向位置（像素）。</param>
/// <param name="Advance">该元素占据的宽度（像素）。</param>
/// <param name="FamilyName">实际用于绘制该元素的字体族（可能是回退字体）。</param>
/// <param name="IsFallback">是否使用了主字体以外的回退字体。</param>
/// <param name="GlyphCount">该元素由多少个 glyph 组成。</param>
/// <param name="HasMissingGlyph">该元素整形结果里是否出现缺字字形。</param>
public readonly record struct ShapedTextElement(
    int Utf16Start,
    int Utf16Length,
    string Text,
    float X,
    float Advance,
    string FamilyName,
    bool IsFallback,
    int GlyphCount,
    bool HasMissingGlyph = false)
{
    /// <summary>结束下标（不含）。</summary>
    public int Utf16End => Utf16Start + Utf16Length;

    /// <summary>该元素右端的横向位置。</summary>
    public float XEnd => X + Advance;

    /// <summary>是否由多个 glyph 组成（连字、ZWJ emoji、叠加记号等）。</summary>
    public bool IsMultiGlyph => GlyphCount > 1;
}

/// <summary>
/// 整行文本的布局结果。
/// </summary>
/// <remarks>
/// <para>
/// docs/development.md §9.4 要求"用完整行文本整形一次，再在稳定布局上做逐字裁切"。
/// 本类型就是那个稳定布局：元素位置一次算好，
/// 逐字进度只影响绘制时的裁切位置，不会导致整行重新排版（即"整行不跳动"）。
/// </para>
/// </remarks>
public sealed class ShapedLine
{
    /// <summary>构造布局结果。</summary>
    /// <param name="text">原始文本。</param>
    /// <param name="elements">文本元素序列，按横向位置递增。</param>
    /// <param name="width">整行宽度（像素），取自 HarfBuzz 的精确值。</param>
    /// <param name="mappingIsExact">
    /// 元素区间是否完整且连续地覆盖整行。为 <see langword="false"/> 表示整形器给出的
    /// 边界与文本元素切分不完全一致，逐字高亮会有偏差。
    /// </param>
    public ShapedLine(string text, IReadOnlyList<ShapedTextElement> elements, float width, bool mappingIsExact)
        : this(text, elements, width, mappingIsExact, missingGlyphCount: 0)
    {
    }

    /// <summary>构造布局结果。</summary>
    /// <param name="text">原始文本。</param>
    /// <param name="elements">文本元素序列，按横向位置递增。</param>
    /// <param name="width">整行宽度（像素），取自 HarfBuzz 的精确值。</param>
    /// <param name="mappingIsExact">
    /// 元素区间是否完整且连续地覆盖整行。为 <see langword="false"/> 表示整形器给出的
    /// 边界与文本元素切分不完全一致，逐字高亮会有偏差。
    /// </param>
    /// <param name="inkBounds">相对于文字原点和基线的实际字形边界。</param>
    /// <param name="missingGlyphCount">缺字个数（任何已解析字体都找不到对应字形）。</param>
    /// <param name="drawUnits">整行整形产出的固定字形／位置；默认表示无后端绘制数据。</param>
    public ShapedLine(
        string text,
        IReadOnlyList<ShapedTextElement> elements,
        float width,
        bool mappingIsExact,
        int missingGlyphCount,
        TextInkBounds? inkBounds = null,
        ImmutableArray<ShapedDrawUnit> drawUnits = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(elements);
        InkBounds = inkBounds;
        Text = text;
        Elements = [.. elements];
        Width = width;
        MappingIsExact = mappingIsExact;
        MissingGlyphCount = missingGlyphCount;
        DrawUnits = drawUnits.IsDefault ? [] : drawUnits;

        int fallbackCount = 0;

        foreach (ShapedTextElement element in elements)
        {
            if (element.IsFallback)
            {
                fallbackCount++;
            }
        }

        FallbackElementCount = fallbackCount;
    }

    /// <summary>原始文本。</summary>
    public string Text { get; }

    /// <summary>实际字形边界，不包含描边与阴影。</summary>
    public TextInkBounds? InkBounds { get; }

    /// <summary>固定字形绘制单元；只有真实整形器产出，不从元素文本再次猜测。</summary>
    public ImmutableArray<ShapedDrawUnit> DrawUnits { get; }

    /// <summary>文本元素序列，按横向位置递增。</summary>
    public IReadOnlyList<ShapedTextElement> Elements { get; }

    /// <summary>整行宽度（像素）。</summary>
    public float Width { get; }

    /// <summary>是否有元素使用了回退字体。</summary>
    public bool UsedFallbackFont => FallbackElementCount > 0;

    /// <summary>使用回退字体的元素数量。</summary>
    public int FallbackElementCount { get; }

    /// <summary>元素区间是否完整连续地覆盖整行。</summary>
    public bool MappingIsExact { get; }

    /// <summary>
    /// 缺字数量：已解析字体里都找不到对应字形的字形个数。
    /// </summary>
    /// <remarks>
    /// 大于 0 表示文本里存在任何已解析字体都画不出的字符。
    /// 按 docs/development.md §9.5，界面上必须提示"字体缺失"，而不是默默显示豆腐块。
    /// 这个计数是实测得到的：<c>SKShaper.Result.Codepoints</c> 里装的其实是字形 ID，
    /// 因此 glyph id 为 0 就等于缺字。
    /// </remarks>
    public int MissingGlyphCount { get; }

    /// <summary>是否存在缺字。</summary>
    public bool HasMissingGlyphs => MissingGlyphCount > 0;

    /// <summary>
    /// 取得覆盖某个 UTF-16 下标的位置，以及该元素在行内的序号。
    /// </summary>
    /// <param name="utf16Index">原始字符串中的下标。</param>
    /// <returns>元素序号与元素；找不到时返回 <c>-1</c> 与 <c>default</c>。</returns>
    public (int Index, ShapedTextElement Element) ElementAt(int utf16Index)
    {
        for (int i = 0; i < Elements.Count; i++)
        {
            if (utf16Index >= Elements[i].Utf16Start && utf16Index < Elements[i].Utf16End)
            {
                return (i, Elements[i]);
            }
        }

        return (-1, default);
    }

    /// <summary>
    /// 取得某个 UTF-16 下标所对应的横向位置（该元素左端）。
    /// </summary>
    /// <param name="utf16Index">原始字符串中的下标。</param>
    /// <returns>横向位置（像素）；找不到时返回 <see cref="Width"/>。</returns>
    public float XAt(int utf16Index)
    {
        (int index, ShapedTextElement element) = ElementAt(utf16Index);

        return index < 0 ? Width : element.X;
    }

    /// <summary>
    /// 取得某个元素内部的插值位置，用于按进度推进高亮边界。
    /// </summary>
    /// <param name="utf16Index">元素起始下标。</param>
    /// <param name="progress">元素内部进度，0 到 1。</param>
    /// <returns>横向位置（像素）。</returns>
    public float XWithin(int utf16Index, double progress)
    {
        (int index, ShapedTextElement element) = ElementAt(utf16Index);

        if (index < 0)
        {
            return Width;
        }

        return element.X + (element.Advance * (float)Math.Clamp(progress, 0d, 1d));
    }
}

/// <summary>相对于文字基线原点的字形几何边界。</summary>
/// <param name="Left">左端。</param>
/// <param name="Top">顶端。</param>
/// <param name="Right">右端。</param>
/// <param name="Bottom">底端。</param>
public readonly record struct TextInkBounds(float Left, float Top, float Right, float Bottom);
