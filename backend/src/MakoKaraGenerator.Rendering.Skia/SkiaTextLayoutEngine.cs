using System.Collections.Immutable;
using System.Text;
using MakoKaraGenerator.Core.Text;
using MakoKaraGenerator.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>
/// 用 SkiaSharp + HarfBuzz 实现文字布局（阶段 0 SPIKE-03 的产品化版本）。
/// </summary>
/// <remarks>
/// <para>
/// 实现 docs/development.md §9.4 / §9.5 的两条前提：
/// </para>
/// <list type="number">
///   <item>按 Unicode 文本元素切分，而不是按 UTF-16 <see cref="char" />；</item>
///   <item>字形边界来自真正的整形器，因此可以在稳定布局上做逐字裁切。</item>
/// </list>
/// <para>
/// <b>两个反直觉的 API 陷阱</b>（已实测确认，见 docs/decisions.md ADR-0008）：
/// </para>
/// <list type="bullet">
///   <item><see cref="SKShaper.Result.Codepoints" /> 里装的是<b>字形 ID</b>，不是 Unicode 码点。
///         "夜"（U+591C）在该字体里返回 0x36E5。</item>
///   <item><see cref="SKShaper.Result.Clusters" /> 是 <b>UTF-8 字节偏移</b>，不是 UTF-16 下标。
///         七个汉字得到 0,3,6,…,18；"é"（e + 组合符）占 3 字节。</item>
/// </list>
/// <para>
/// 因此本实现用 <c>Clusters</c> 做边界、再按 UTF-8→UTF-16 换算，
/// 并把同一 cluster 的多个 glyph 归并成一个文本元素（连字、ZWJ emoji、叠加记号）。
/// </para>
/// <para>
/// 字体回退按"文本元素"而不是按单码点判断：一个 emoji 的多个码点必须交给同一个字体，
/// 否则 ZWJ 序列会被拆到两个字体里而画错。
/// </para>
/// <para>
/// 字体对象按"族名 + 字号"缓存在实例里：布局每帧都会发生，重复打开字体文件会拖慢预览。
/// native 字体对象不跨线程共享，因此预览与导出各自持有实例。
/// </para>
/// <para>
/// 自动回退按完整文本元素缓存成功与缺字结果，每组字体最多 1024 项，
/// 超过 64 个 UTF-16 代码单元的元素不缓存。已匹配的字体族复用原生对象；
/// 字号、字重、粗斜体与显式回退链继续各自独立。系统字体变更需创建新引擎。
/// </para>
/// </remarks>
public sealed class SkiaTextLayoutEngine : ITextLayoutEngine
{
    /// <summary>默认字体族。本机已确认存在，且覆盖简体中文。</summary>
    public const string DefaultFamilyName = "Noto Sans CJK SC";

    private readonly Func<string, int, SKTypeface?> _matchCharacter;
    private readonly Func<string, SKFontStyle, SKTypeface?> _createFallbackTypeface;
    private readonly Dictionary<FontKey, FontSet> _fontSets = [];
    private readonly Dictionary<string, string> _resolvedFamilyNames = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>创建一个布局引擎。</summary>
    /// <remarks>引擎不带字号：字号属于 <see cref="TextLayoutOptions" />，每个组合各自缓存。</remarks>
    public SkiaTextLayoutEngine() : this(
        (family, codepoint) => SKFontManager.Default.MatchCharacter(family, codepoint),
        (family, style) => SKFontManager.Default.MatchFamily(family, style))
    {
    }

    /// <summary>固定包字体优先参与同族选择与按字符回退，系统回退仍沿原路径。</summary>
    public SkiaTextLayoutEngine(IEnumerable<ReadOnlyMemory<byte>> fixedFonts) : this(new FixedFontCatalog(fixedFonts)) { }

    private SkiaTextLayoutEngine(FixedFontCatalog fonts) : this(fonts.MatchCharacter, fonts.MatchFamily) { }

    /// <summary>
    /// 用与固定包字体目录相同的加载规则读出一份字体文件里每个字形的家族名（按出现顺序去重）。
    /// 无法加载时抛 <see cref="InvalidDataException" />，与渲染时拒绝同一份字体的条件一致。
    /// </summary>
    public static IReadOnlyList<string> ReadFixedFontFamilies(ReadOnlyMemory<byte> font)
        => [.. new FixedFontCatalog([font]).Families.Distinct(StringComparer.Ordinal)];

    private sealed class FixedFontCatalog
    {
        private readonly Entry[] _entries;
        internal IEnumerable<string> Families => _entries.Select(entry => entry.Family);
        internal FixedFontCatalog(IEnumerable<ReadOnlyMemory<byte>> fonts)
        {
            ArgumentNullException.ThrowIfNull(fonts);
            _entries = [.. fonts.SelectMany(bytes =>
            {
                using SKData data = SKData.CreateCopy(bytes.Span);
                int count = bytes.Span.StartsWith("ttcf"u8) && bytes.Length >= 12
                    ? checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Span[8..])) : 1;
                if (count <= 0 || count > bytes.Length / 4) throw new InvalidDataException("固定字体集合索引无效。");
                List<Entry> entries = []; byte[] content = bytes.ToArray();
                for (int index = 0; index < count; index++)
                {
                    using var typeface = SKTypeface.FromData(data, index) ?? throw new InvalidDataException("固定包字体无法建立typeface。");
                    if (!HasFontData(typeface)) throw new InvalidDataException("固定包字体没有可读数据。");
                    entries.Add(new(content, index, typeface.FamilyName, typeface.FontWeight, typeface.FontWidth, typeface.FontSlant));
                }
                return entries;
            })];
        }
        internal SKTypeface? MatchFamily(string family, SKFontStyle style)
        {
            var entry = _entries.Where(value => string.Equals(value.Family, family, StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => Math.Abs(value.Weight - style.Weight) + Math.Abs(value.Width - style.Width) * 100
                    + (value.Slant == style.Slant ? 0 : 1000)).FirstOrDefault();
            return entry is null ? SKFontManager.Default.MatchFamily(family, style) : Open(entry);
        }
        internal SKTypeface? MatchCharacter(string family, int codepoint)
        {
            foreach (var entry in _entries.OrderBy(value => string.Equals(value.Family, family, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                var typeface = Open(entry);
                if (typeface.ContainsGlyphs(char.ConvertFromUtf32(codepoint))) return typeface;
                typeface.Dispose();
            }
            return SKFontManager.Default.MatchCharacter(family, codepoint);
        }
        private static SKTypeface Open(Entry entry)
        { using var data = SKData.CreateCopy(entry.Bytes); return SKTypeface.FromData(data, entry.Index) ?? throw new InvalidDataException("固定包字体无法建立typeface。"); }
        private sealed record Entry(byte[] Bytes, int Index, string Family, int Weight, int Width, SKFontStyleSlant Slant);
    }

    // Keep the native lookup observable in tests while exercising real Skia shaping and drawing.
    internal SkiaTextLayoutEngine(Func<string, int, SKTypeface?> matchCharacter,
        Func<string, SKFontStyle, SKTypeface?> createFallbackTypeface)
    {
        _matchCharacter = matchCharacter;
        _createFallbackTypeface = createFallbackTypeface;
    }

    /// <inheritdoc/>
    public string ResolveFamilyName(string? requestedFamilyName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ResolveFamilyNameCore(requestedFamilyName);
    }

    /// <inheritdoc/>
    public ShapedLine Shape(string text, TextLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.FontSizePx);
        if (!float.IsFinite(options.FontSizePx)) throw new ArgumentOutOfRangeException(nameof(options), "字号必须有限。");
        if (options.FontWeight is < 100 or > 900 || !float.IsFinite(options.LetterSpacingPx))
            throw new ArgumentOutOfRangeException(nameof(options), "字重或字距超出有效范围。");

        if (text.Length == 0)
        {
            return new ShapedLine(text, [], 0f, mappingIsExact: true);
        }

        FontSet fonts = GetOrAddFontSet(options);
        List<ShapedTextElement> elements = [];
        List<ShapedDrawUnit> units = [];
        List<FontRun> runs = BuildRuns(text, fonts);
        float x = 0f;
        bool mappingIsExact = true;
        int missingGlyphCount = 0;

        foreach (FontRun run in runs)
        {
            SKShaper.Result result = run.Entry.Shaper.Shape(run.Text, run.Entry.Font);
            int[] byteToChar = BuildUtf8ToUtf16Map(run.Text);
            bool isFallback = !ReferenceEquals(run.Entry, fonts.PrimaryEntry);

            mappingIsExact &= AppendRunElements(run, result, byteToChar, x, isFallback, elements, units, ref missingGlyphCount);
            x += result.Width;
        }

        mappingIsExact &= CoversTextExactly(text.Length, elements);

        if (options.LetterSpacingPx != 0f && elements.Count > 1)
        {
            for (int i = 0; i < elements.Count; i++)
            {
                float offset = i * options.LetterSpacingPx;
                ShapedDrawUnit unit = units[i];
                units[i] = unit with
                {
                    X = unit.X + offset,
                    Advance = unit.Advance + (i < elements.Count - 1 ? options.LetterSpacingPx : 0f),
                    Glyphs = [.. unit.Glyphs.Select(glyph => glyph with
                    {
                        X = glyph.X + offset,
                        InkBounds = glyph.InkBounds is { } bounds
                            ? bounds with { Left = bounds.Left + offset, Right = bounds.Right + offset } : null,
                    })],
                };
                elements[i] = elements[i] with
                {
                    X = elements[i].X + (i * options.LetterSpacingPx),
                    Advance = elements[i].Advance + (i < elements.Count - 1 ? options.LetterSpacingPx : 0f),
                };
            }
            x += (elements.Count - 1) * options.LetterSpacingPx;
        }

        TextInkBounds? ink = null;
        foreach (ShapedGlyph glyph in units.SelectMany(static unit => unit.Glyphs))
        {
            if (glyph.InkBounds is not { } box) continue;
            ink = ink is { } previous ? new(Math.Min(previous.Left, box.Left), Math.Min(previous.Top, box.Top),
                Math.Max(previous.Right, box.Right), Math.Max(previous.Bottom, box.Bottom)) : box;
        }
        return new ShapedLine(text, elements, x, mappingIsExact, missingGlyphCount, ink, [.. units]);
    }

    internal bool CanShareLayout(ShapedLine line, TextLayoutOptions options)
    {
        if (line.DrawUnits.IsEmpty) return true;
        FontSet fonts = GetOrAddFontSet(options);
        return line.DrawUnits.All(unit => fonts.IsFixedFamily(unit.FamilyName));
    }

    /// <summary>
    /// 把一行已经布局好的文本画到画布上。
    /// </summary>
    /// <param name="canvas">目标画布。</param>
    /// <param name="line">布局结果；必须由本引擎用同一组 <paramref name="options"/> 产出。</param>
    /// <param name="options">产出该布局时使用的选项（用于找回同一组字体对象）。</param>
    /// <param name="originX">整行左端在画布上的横坐标。</param>
    /// <param name="baselineY">基线纵坐标。</param>
    /// <param name="color">文字颜色。</param>
    /// <param name="antialias">是否开启抗锯齿。草稿档位会关掉它，几何位置不受影响。</param>
    /// <param name="style">Skia 绘制样式。</param>
    /// <param name="strokeWidth">描边宽度。</param>
    /// <param name="shader">可选填充着色器。</param>
    /// <param name="imageFilter">可选图像滤镜。</param>
    /// <param name="strokeJoin">描边连接方式。</param>
    /// <param name="blendMode">文字与目标图层的混合方式；字形遮罩使用 DstOut。</param>
    /// <param name="drawUnitIndices">可选完整字簇索引；默认绘制全部，空集合不画。不会重新整形或按字符串切字形。</param>
    /// <exception cref="ArgumentNullException">任一引用参数为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="ObjectDisposedException">已释放时抛出。</exception>
    /// <remarks>
    /// <para>
    /// 按固定字簇使用实际字体绘制；字形 ID 和位置来自整行唯一的 SKShaper.Shape。
    /// DrawShapedText 的缓存字形入口使用与上游相同的 positioned text blob 绘制流程，
    /// 不再次调用子串整形，也不使用 DrawText(string) 猜测字形。
    /// </para>
    /// <para>
    /// 绘制是本引擎的职责而不是渲染管线的：字体对象刻意封装在这里，
    /// 管线只负责"画什么、什么颜色、裁到哪里"（docs/development.md §9）。
    /// </para>
    /// </remarks>
    public void DrawLine(
        SKCanvas canvas,
        ShapedLine line,
        TextLayoutOptions options,
        float originX,
        float baselineY,
        SKColor color,
        bool antialias = true,
        SKPaintStyle style = SKPaintStyle.Fill,
        float strokeWidth = 0f,
        SKShader? shader = null,
        SKImageFilter? imageFilter = null,
        SKStrokeJoin strokeJoin = SKStrokeJoin.Round,
        SKBlendMode blendMode = SKBlendMode.SrcOver,
        ImmutableArray<int> drawUnitIndices = default)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (line.Elements.Count == 0)
        {
            return;
        }

        FontSet fonts = GetOrAddFontSet(options);

        using SKPaint paint = new()
        {
            IsAntialias = antialias,
            Color = color,
            Style = style,
            StrokeWidth = strokeWidth,
            StrokeJoin = strokeJoin,
            Shader = shader,
            ImageFilter = imageFilter,
            BlendMode = blendMode,
        };

        if (line.DrawUnits.IsEmpty)
            throw new ArgumentException("绘制需要整行整形产出的固定字形单元。", nameof(line));
        IEnumerable<ShapedDrawUnit> selected = drawUnitIndices.IsDefault ? line.DrawUnits
            : drawUnitIndices.Select(index => line.DrawUnits[index]);
        foreach (ShapedDrawUnit unit in selected)
            DrawShapedText(canvas, unit.Glyphs, fonts.GetEntry(unit.FamilyName).Font, originX, baselineY, paint);
    }

    // 与 SkiaSharp.HarfBuzz CanvasExtensions 的最终路径一致；使用已有 Result 的 ID／位置。
    private static void DrawShapedText(SKCanvas canvas, ImmutableArray<ShapedGlyph> glyphs,
        SKFont font, float originX, float baselineY, SKPaint paint)
    {
        if (glyphs.IsEmpty) return;
        using SKTextBlobBuilder builder = new();
        var run = builder.AllocateRawPositionedRun(font, glyphs.Length, null);
        for (int i = 0; i < glyphs.Length; i++)
        {
            run.Glyphs[i] = glyphs[i].GlyphId;
            run.Positions[i] = new(glyphs[i].X, glyphs[i].Y);
        }
        using SKTextBlob blob = builder.Build() ?? throw new InvalidOperationException("固定字形绘制单元无法构造文本图元。");
        canvas.DrawText(blob, originX, baselineY, paint);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (FontSet set in _fontSets.Values)
        {
            set.Dispose();
        }

        _fontSets.Clear();
        _resolvedFamilyNames.Clear();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>解析一次字体族请求；只缓存具有可读取字体数据的实际族名。</summary>
    private string ResolveFamilyNameCore(string? requestedFamilyName)
    {
        string request = requestedFamilyName ?? string.Empty;
        if (_resolvedFamilyNames.TryGetValue(request, out string? cached)) return cached;
        using SKTypeface probe = CreateUsableTypeface(requestedFamilyName, SKFontStyle.Normal);
        string resolved = probe.FamilyName;
        _resolvedFamilyNames[request] = resolved;
        return resolved;
    }

    private SKTypeface CreateTypeface(TextLayoutOptions options)
    {
        int weight = options.IsBold ? Math.Max(700, options.FontWeight) : options.FontWeight;
        using SKFontStyle style = new(weight, SKFontStyle.Normal.Width,
            options.IsItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        return CreateUsableTypeface(ResolveFamilyNameCore(options.FamilyName), style);
    }

    private SKTypeface CreateUsableTypeface(string? requestedFamilyName, SKFontStyle style)
    {
        string[] families = [requestedFamilyName ?? string.Empty, SKTypeface.Default.FamilyName, DefaultFamilyName];
        foreach (string family in families.Where(static name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal))
        {
            SKTypeface? candidate = _createFallbackTypeface(family, style);
            if (HasFontData(candidate)) return candidate!;
            candidate?.Dispose();
        }
        throw new InvalidOperationException($"无法读取字体数据：{requestedFamilyName ?? "系统默认字体"}。请检查系统字体后重试。");
    }

    private static bool HasFontData(SKTypeface? typeface)
    {
        if (typeface is null || typeface.Handle == IntPtr.Zero) return false;
        using SKStreamAsset? stream = typeface.OpenStream();
        return stream is { Length: > 0 };
    }

    private FontSet GetOrAddFontSet(TextLayoutOptions options)
    {
        ImmutableArray<string> fallbackNames = options.FallbackFamilyNames.IsDefault
            ? []
            : options.FallbackFamilyNames;
        string fallbackKey = string.Join('\u001F', fallbackNames);
        FontKey key = new(options.FamilyName, fallbackKey, options.FontSizePx, options.FontWeight,
            options.IsBold, options.IsItalic);

        if (_fontSets.TryGetValue(key, out FontSet? existing))
        {
            return existing;
        }

        FontSet created = new(CreateTypeface(options), fallbackNames, options.FontSizePx, _matchCharacter, _createFallbackTypeface,
            options.FontWeight, options.IsBold, options.IsItalic);
        _fontSets[key] = created;
        return created;
    }

    /// <summary>
    /// 把一个 run 的整形结果拆成文本元素，并追加到 <paramref name="elements"/>。
    /// </summary>
    /// <returns>元素边界是否与 run 的字符范围一致。</returns>
    private static bool AppendRunElements(
        FontRun run,
        SKShaper.Result result,
        int[] byteToChar,
        float runX,
        bool isFallback,
        List<ShapedTextElement> elements,
        List<ShapedDrawUnit> units,
        ref int missingGlyphCount)
    {
        if (result.Clusters.Length == 0)
        {
            // 空 run 或者整形器没有产出：让调用方把映射标为不精确。
            return run.Length == 0;
        }

        int runEndByte = byteToChar.Length - 1;
        if (result.Clusters.Any(cluster => cluster >= runEndByte)) return false;
        ushort[] ids = Array.ConvertAll(result.Codepoints, static id => checked((ushort)id));
        run.Entry.Font.GetGlyphWidths(ids, out SKRect[] glyphBounds);
        var groups = result.Clusters.Select((cluster, index) => (cluster, index))
            .GroupBy(static item => item.cluster).OrderBy(static group => group.Key).ToArray();
        var visualStarts = groups.Select(group => group.Min(item => result.Points[item.index].X))
            .Append(result.Width).Distinct().Order().ToArray();
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];
            int startChar = byteToChar[group.Key];
            int endChar = byteToChar[groupIndex + 1 < groups.Length ? groups[groupIndex + 1].Key : (uint)runEndByte];
            float startX = group.Min(item => result.Points[item.index].X);
            float nextX = visualStarts.FirstOrDefault(position => position > startX, result.Width);
            ImmutableArray<ShapedGlyph>.Builder glyphs = ImmutableArray.CreateBuilder<ShapedGlyph>();
            bool hasMissingGlyph = false;
            foreach (var item in group)
            {
                int i = item.index;
                if (ids[i] == 0) { missingGlyphCount++; hasMissingGlyph = true; }
                float glyphX = runX + result.Points[i].X, glyphY = result.Points[i].Y;
                SKRect box = glyphBounds[i];
                TextInkBounds? ink = box.IsEmpty ? null : new(box.Left + glyphX, box.Top + glyphY,
                    box.Right + glyphX, box.Bottom + glyphY);
                glyphs.Add(new(ids[i], glyphX, glyphY, ink));
            }
            elements.Add(new(run.Start + startChar, endChar - startChar, run.Text[startChar..endChar],
                runX + startX, nextX - startX, run.Entry.Typeface.FamilyName, isFallback, group.Count(), hasMissingGlyph));
            units.Add(new(run.Start + startChar, endChar - startChar, run.Entry.Typeface.FamilyName,
                runX + startX, nextX - startX, glyphs.ToImmutable(), run.Entry.Font.Metrics.Ascent, run.Entry.Font.Metrics.Descent));
        }
        return true;
    }

    /// <summary>检查元素区间是否首尾相接、完整覆盖整行。</summary>
    private static bool CoversTextExactly(int textLength, List<ShapedTextElement> elements)
    {
        int expected = 0;

        foreach (ShapedTextElement element in elements)
        {
            if (element.Utf16Start != expected || element.Utf16Length <= 0)
            {
                return false;
            }

            expected = element.Utf16End;
        }

        return expected == textLength;
    }

    /// <summary>把连续使用同一字体的文本元素归并成整形用的 run。</summary>
    private static List<FontRun> BuildRuns(string text, FontSet fonts)
    {
        IReadOnlyList<TextElementRange> elements = GraphemeSplitter.Split(text);
        List<FontRun> runs = [];
        FontEntry? current = null;
        int runStart = 0;
        int runLength = 0;

        foreach (TextElementRange element in elements)
        {
            FontEntry entry = fonts.ResolveFontEntry(element.Text);

            if (current is not null && ReferenceEquals(current, entry))
            {
                runLength += element.Length;
                continue;
            }

            if (current is not null)
            {
                runs.Add(new FontRun(current, runStart, runLength, text.Substring(runStart, runLength)));
            }

            current = entry;
            runStart = element.Start;
            runLength = element.Length;
        }

        if (current is not null)
        {
            runs.Add(new FontRun(current, runStart, runLength, text.Substring(runStart, runLength)));
        }

        return runs;
    }

    /// <summary>
    /// 建立 UTF-8 字节偏移到 UTF-16 下标的映射。
    /// </summary>
    /// <remarks>
    /// <see cref="SKShaper.Result.Clusters"/> 给的是 UTF-8 字节偏移，
    /// 而 .NET 字符串是 UTF-16。这个映射是两者之间唯一的桥。
    /// 落在多字节序列中间的偏移会映射到该码点的起点，
    /// 因为 cluster 偏移总是落在码点边界上。
    /// </remarks>
    private static int[] BuildUtf8ToUtf16Map(string text)
    {
        int byteLength = Encoding.UTF8.GetByteCount(text);
        int[] map = new int[byteLength + 1];
        int byteOffset = 0;
        int charIndex = 0;

        while (charIndex < text.Length)
        {
            bool isSurrogatePair = char.IsHighSurrogate(text[charIndex]) && charIndex + 1 < text.Length;
            int charLength = isSurrogatePair ? 2 : 1;
            int byteCount = Encoding.UTF8.GetByteCount(text.AsSpan(charIndex, charLength));

            for (int i = 1; i <= byteCount; i++)
            {
                map[byteOffset + i] = i == byteCount ? charIndex + charLength : charIndex;
            }

            byteOffset += byteCount;
            charIndex += charLength;
        }

        return map;
    }

    /// <summary>字体缓存的键：请求的族名 + 字号。</summary>
    private readonly record struct FontKey(string? FamilyName, string FallbackKey, float FontSizePx,
        int FontWeight, bool IsBold, bool IsItalic);

    /// <summary>一段连续使用同一字体的文本。</summary>
    private sealed record FontRun(FontEntry Entry, int Start, int Length, string Text);

    /// <summary>
    /// 一组用于同一字号字体对象：主字体 + 已解析出的回退字体。
    /// </summary>
    private sealed class FontSet : IDisposable
    {
        // Automatic fallback includes misses. Bound both entry count and grapheme length;
        // a long-lived editor must not retain arbitrary lyrics as lookup keys.
        private const int ElementCacheCapacity = 1024;
        private const int MaximumCachedElementLength = 64;
        private readonly Func<string, int, SKTypeface?> _matchCharacter;
        private readonly Func<string, SKFontStyle, SKTypeface?> _createFallbackTypeface;
        private readonly Dictionary<string, FontEntry> _resolvedElements = new(StringComparer.Ordinal);
        private readonly Queue<string> _elementOrder = [];
        private readonly Dictionary<string, FontEntry> _fontsByFamily;
        private readonly ImmutableArray<FontEntry> _fallbackEntries;

        public FontSet(SKTypeface primary, ImmutableArray<string> fallbackFamilyNames, float fontSize,
            Func<string, int, SKTypeface?> matchCharacter, Func<string, SKFontStyle, SKTypeface?> createFallbackTypeface,
            int fontWeight, bool isBold, bool isItalic)
        {
            _matchCharacter = matchCharacter;
            _createFallbackTypeface = createFallbackTypeface;
            RequestedStyle = new SKFontStyle(isBold ? Math.Max(700, fontWeight) : fontWeight,
                SKFontStyle.Normal.Width, isItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            PrimaryEntry = new FontEntry(primary, new SKFont(primary, fontSize), new SKShaper(primary));
            PrimaryFamilyName = primary.FamilyName;
            _fontsByFamily = new Dictionary<string, FontEntry>(StringComparer.Ordinal)
            {
                [PrimaryFamilyName] = PrimaryEntry,
            };
            ImmutableArray<FontEntry>.Builder fallbackEntries = ImmutableArray.CreateBuilder<FontEntry>();
            foreach (string familyName in fallbackFamilyNames)
            {
                if (string.IsNullOrWhiteSpace(familyName)
                    || string.Equals(familyName, PrimaryFamilyName, StringComparison.Ordinal))
                {
                    continue;
                }

                SKTypeface? fallback = _createFallbackTypeface(familyName, RequestedStyle);
                if (HasFontData(fallback) && !_fontsByFamily.ContainsKey(fallback!.FamilyName))
                {
                    FontEntry entry = new(fallback!, new SKFont(fallback!, fontSize), new SKShaper(fallback!));
                    _fontsByFamily[fallback.FamilyName] = entry;
                    fallbackEntries.Add(entry);
                }
                else
                {
                    fallback?.Dispose();
                }
            }
            _fallbackEntries = fallbackEntries.ToImmutable();
        }

        public string PrimaryFamilyName { get; }

        private SKFontStyle RequestedStyle { get; }

        public FontEntry PrimaryEntry { get; }

        public bool IsFixedFamily(string familyName) =>
            string.Equals(PrimaryFamilyName, familyName, StringComparison.Ordinal)
            || _fallbackEntries.Any(entry => string.Equals(entry.Typeface.FamilyName, familyName, StringComparison.Ordinal));

        /// <summary>为一个文本元素选出能完整覆盖它的字体。</summary>
        public FontEntry ResolveFontEntry(string element)
        {
            if (SkiaTypefaceCoverage.IsElementCoveredBy(PrimaryEntry.Typeface, element))
            {
                return PrimaryEntry;
            }

            foreach (FontEntry entry in _fallbackEntries)
            {
                if (SkiaTypefaceCoverage.IsElementCoveredBy(entry.Typeface, element))
                {
                    return entry;
                }
            }

            if (_resolvedElements.TryGetValue(element, out FontEntry? resolved))
                return resolved;

            foreach (int codepoint in TextElementRules.EnumerateCodepoints(element))
            {
                SKTypeface? matched = _matchCharacter(PrimaryFamilyName, codepoint);

                if (!HasFontData(matched))
                {
                    matched?.Dispose();
                    continue;
                }

                FontEntry entry;
                if (_fontsByFamily.TryGetValue(matched!.FamilyName, out FontEntry? cached))
                {
                    matched.Dispose();
                    entry = cached;
                }
                else
                {
                    SKTypeface? styledCandidate = _createFallbackTypeface(matched!.FamilyName, RequestedStyle);
                    SKTypeface styled;
                    if (HasFontData(styledCandidate)) styled = styledCandidate!;
                    else { styledCandidate?.Dispose(); styled = matched; }
                    if (!ReferenceEquals(styled, matched)) matched.Dispose();
                    entry = GetOrAdd(styled);
                }

                if (SkiaTypefaceCoverage.IsElementCoveredBy(entry.Typeface, element))
                {
                    return RememberElement(element, entry);
                }
            }

            // 没有任何字体能完整覆盖：交回主字体，让 Skia 画缺字标记，
            // ShapedLine.MissingGlyphCount 会把这一情况带给界面（docs/development.md §9.5）。
            return RememberElement(element, PrimaryEntry);
        }

        private FontEntry RememberElement(string element, FontEntry entry)
        {
            if (element.Length > MaximumCachedElementLength) return entry;
            if (_resolvedElements.Count == ElementCacheCapacity)
                _resolvedElements.Remove(_elementOrder.Dequeue());
            _resolvedElements.Add(element, entry);
            _elementOrder.Enqueue(element);
            return entry;
        }

        /// <summary>按族名取字体条目；找不到时退回主字体（布局结果里的族名一定来自本集合）。</summary>
        public FontEntry GetEntry(string familyName) =>
            _fontsByFamily.TryGetValue(familyName, out FontEntry? entry) ? entry : PrimaryEntry;

        public void Dispose()
        {
            foreach (FontEntry entry in _fontsByFamily.Values)
            {
                entry.Dispose();
            }

            _fontsByFamily.Clear();
            _resolvedElements.Clear();
            _elementOrder.Clear();
        }

        private FontEntry GetOrAdd(SKTypeface typeface)
        {
            string familyName = typeface.FamilyName;

            if (_fontsByFamily.TryGetValue(familyName, out FontEntry? existing))
            {
                typeface.Dispose();
                return existing;
            }

            FontEntry created = new(typeface, new SKFont(typeface, PrimaryEntry.Font.Size), new SKShaper(typeface));
            _fontsByFamily[familyName] = created;
            return created;
        }
    }

    /// <summary>一个字体及其配套的 <see cref="SKFont" /> 与 <see cref="SKShaper" />。</summary>
    private sealed class FontEntry : IDisposable
    {
        public FontEntry(SKTypeface typeface, SKFont font, SKShaper shaper)
        {
            Typeface = typeface;
            Font = font;
            Shaper = shaper;
        }

        public SKTypeface Typeface { get; }

        public SKFont Font { get; }

        public SKShaper Shaper { get; }

        public void Dispose()
        {
            Shaper.Dispose();
            Font.Dispose();
            Typeface.Dispose();
        }

        public override string ToString() => Typeface.FamilyName;
    }
}
