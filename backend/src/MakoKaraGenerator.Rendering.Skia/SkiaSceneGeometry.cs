using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>
/// Skia 场景几何：整行整形、注音与文字标记布局、行与元素的测量，以及装饰外扩量。
/// </summary>
/// <remarks>
/// <para>
/// 几何是脚本求值与绘制共同的输入：脚本宿主经 <see cref="ILineGeometryProvider" /> 取场景帧，
/// <see cref="SkiaRenderPipeline" /> 在同一份布局上绘制。本类型只持有排版引擎与布局缓存，
/// 不依赖渲染设备、中间表面预算或图资源。
/// </para>
/// <para>
/// 排版引擎不跨线程共享（native 字体对象）。测量与使用同一引擎的绘制须在同一把锁内：
/// 由绘制管线或会话工厂创建时传入渲染设备的同步对象，单独创建时使用自己的锁。
/// </para>
/// </remarks>
public sealed partial class SkiaSceneGeometry : ILineGeometryProvider, IDisposable
{
    private readonly SkiaTextLayoutEngine _layoutEngine;
    private readonly bool _ownsLayoutEngine;
    private readonly LayoutCache _layouts;
    private readonly bool _ownsLayouts;
    private readonly object _synchronizationRoot;
    private bool _disposed;

    /// <summary>创建一个独立的几何提供者。</summary>
    /// <param name="layoutEngine">文字布局引擎；为 <see langword="null" /> 时自建并由本实例释放，传入时借用。</param>
    public SkiaSceneGeometry(SkiaTextLayoutEngine? layoutEngine = null)
        : this(layoutEngine, ownsLayoutEngine: false, layouts: null, synchronizationRoot: null) { }

    /// <summary>创建与绘制共用同步对象的几何提供者；布局缓存可在同一固定字体集合的多个实例间共享。</summary>
    internal SkiaSceneGeometry(SkiaTextLayoutEngine? layoutEngine, bool ownsLayoutEngine, LayoutCache? layouts, object? synchronizationRoot)
    {
        _ownsLayoutEngine = layoutEngine is null || ownsLayoutEngine;
        _layouts = layouts ?? new();
        _ownsLayouts = layouts is null;
        _synchronizationRoot = synchronizationRoot ?? new();
        _layoutEngine = layoutEngine ?? new SkiaTextLayoutEngine();
    }

    /// <summary>本实例整形与绘制所用的排版引擎；字形身份属于它，绘制须用同一个。</summary>
    internal SkiaTextLayoutEngine LayoutEngine => _layoutEngine;

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_synchronizationRoot)
        {
            if (_disposed) return;
            if (_ownsLayouts) _layouts.Clear();
            if (_ownsLayoutEngine) _layoutEngine.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 取得（并缓存）一行文本的布局结果。
    /// </summary>
    /// <remarks>
    /// 布局只与"文本 + 字体 + 字号"有关，与时刻无关——这正是"高亮时整行不跳动"的根据：
    /// 同一行在不同时刻复用同一份元素位置。
    /// </remarks>
    internal ShapedLine GetLayout(string text, TextLayoutOptions options)
    {
        LayoutKey key = new(text, options.FamilyName, options.FontSizePx, options.FontWeight,
            options.IsBold, options.IsItalic, options.LetterSpacingPx, options.FallbackFamilyNames);

        return _layouts.GetOrAdd(key, _layoutEngine, text, options);
    }

    /// <summary>布局缓存的键：文本 + 字体 + 字号（与时刻无关）。</summary>
    internal readonly record struct LayoutKey(string Text, string? FamilyName, float FontSizePx,
        int FontWeight, bool IsBold, bool IsItalic, float LetterSpacingPx, ImmutableArray<string> FallbackFamilyNames);

    // The owner shares this only among renderers created for the same fixed font set.
    internal sealed class LayoutCache
    {
        private readonly ConcurrentDictionary<LayoutKey, ShapedLine> _entries = new();
        private readonly ConditionalWeakTable<SkiaTextLayoutEngine, ConcurrentDictionary<LayoutKey, ShapedLine>> _engineEntries = new();
        private readonly object _gate = new();
        private readonly object _rubyGate = new();
        private readonly Dictionary<(LyricLine, LyricStyle), LineLayout> _ruby = [];
        private readonly ConditionalWeakTable<SkiaTextLayoutEngine, Dictionary<(LyricLine, LyricStyle), LineLayout>> _engineRuby = new();
        internal int Count => _entries.Count;
        internal int RubyBuildCount { get; private set; }
        internal void Clear()
        {
            _entries.Clear(); _engineEntries.Clear();
            lock (_rubyGate) { _ruby.Clear(); _engineRuby.Clear(); }
        }

        internal LineLayout GetRuby(LyricLine line, LyricStyle style, SkiaTextLayoutEngine engine, Func<LineLayout> build)
        {
            lock (_rubyGate)
            {
                var key = (line, style);
                if (_ruby.TryGetValue(key, out var result)) return result;
                var local = _engineRuby.GetOrCreateValue(engine);
                if (local.TryGetValue(key, out result)) return result;
                result = build(); RubyBuildCount++;
                // 动态回退字体的字形身份属于原引擎；只有全部布局可共享时才跨管线复用解算。
                bool share = engine.CanShareLayout(result.Shaped, LayoutOptionsFor(style))
                    && result.Ruby.All(ruby => engine.CanShareLayout(ruby.Shaped, ruby.Options));
                var entries = share ? _ruby : local;
                if (entries.Count >= 256) entries.Clear();
                entries.Add(key, result);
                return result;
            }
        }

        internal ShapedLine GetOrAdd(LayoutKey key, SkiaTextLayoutEngine engine, string text, TextLayoutOptions options)
        {
            if (_entries.TryGetValue(key, out var existing)) return existing;
            var local = _engineEntries.GetOrCreateValue(engine);
            if (local.TryGetValue(key, out existing)) return existing;
            var shaped = engine.Shape(text, options);
            if (!engine.CanShareLayout(shaped, options))
            {
                lock (local)
                {
                    if (local.TryGetValue(key, out existing)) return existing;
                    if (local.Count >= 2048) return shaped;
                    return local.GetOrAdd(key, shaped);
                }
            }
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out existing)) return existing;
                if (_entries.Count >= 2048) return shaped;
                return _entries.GetOrAdd(key, shaped);
            }
        }
    }
}
