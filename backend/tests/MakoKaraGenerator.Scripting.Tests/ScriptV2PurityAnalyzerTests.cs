using System.Collections.Immutable;
using MakoKaraGenerator.Scripting.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class ScriptV2PurityAnalyzerTests
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        ScriptReferenceIdentity.Read(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), out var images);
        return [.. images.Select(static image => MetadataReference.CreateFromImage(image))];
    });

    [Fact]
    public void 准备允许局部LINQ与集合并递归接受只读记录()
    {
        const string source = """
            public sealed record Node(int Value, Node? Next);
            public sealed record Data(ImmutableArray<Node> Nodes, FrozenDictionary<int, string> Names) : PreparedEffect;
            public sealed class Probe : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source)
                {
                    var values = new List<int>();
                    for (int i = 0; i < 3; i++) values.Add(i);
                    var nodes = values.Select(static value => new Node(value, null)).ToImmutableArray();
                    var names = values.ToFrozenDictionary(static value => value, static value => "fixed");
                    return new Data(nodes, names);
                }
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    var local = new List<int>(); local.Add(1); local[0] += 2;
                    int count = local.Count; count++;
                    foreach (var token in frame.Presentations.TokensInBinding)
                        output.MultiplyOpacity(token.WithRuby, token.Performance.IsCurrent ? .5 : 1);
                }
            }
            """;
        var diagnostics = Analyze(source);
        Assert.DoesNotContain(diagnostics, static value => value.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains(diagnostics, static value => value.Id == "MKV2008");
        Assert.DoesNotContain(diagnostics, static value => value.Id == "MKV2009");
    }

    [Theory]
    [InlineData("ImmutableArray<Mutable>", "new Data(ImmutableArray<Mutable>.Empty)")]
    [InlineData("FrozenDictionary<int, Mutable>", "new Data(new Dictionary<int, Mutable>().ToFrozenDictionary())")]
    [InlineData("List<int>", "new Data(new List<int>())")]
    [InlineData("int[]", "new Data(new int[0])")]
    [InlineData("Action", "new Data(static () => { })")]
    [InlineData("object", "new Data(new Mutable())")]
    [InlineData("nint", "new Data(0)")]
    public void 准备拒绝外层不可变包裹可变元素和开放容器(string type, string expression)
    {
        string source = $$"""
            public sealed class Mutable { public int Value { get; set; } }
            public sealed record Data({{type}} Value) : PreparedEffect;
            public sealed class Probe : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => {{expression}};
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            }
            """;
        AssertError(Analyze(source), "MKV2004");
    }

    [Theory]
    [InlineData("_ = DateTime.UtcNow;", "", "MKV2001")]
    [InlineData("_ = Guid.NewGuid();", "", "MKV2001")]
    [InlineData("_ = Environment.GetEnvironmentVariable(\"PURE_PROBE\");", "", "MKV2001")]
    [InlineData("_ = System.IO.File.ReadAllText(\"unused\");", "", "MKV2001")]
    [InlineData("_ = System.Threading.Tasks.Task.Run(static () => 1);", "", "MKV2001")]
    [InlineData("_ = typeof(Probe);", "", "MKV2001")]
    [InlineData("_state++;", "private int _state;", "MKV2003")]
    [InlineData("ref int alias = ref _state; alias++;", "private int _state;", "MKV2003")]
    [InlineData("_ = Clock;", "private static long Clock => DateTime.Now.Ticks;", "MKV2001")]
    [InlineData("_ = Read();", "private static long Read() => Environment.TickCount64;", "MKV2001")]
    [InlineData("Func<long> read = Read; _ = read();", "private static long Read() => DateTime.UtcNow.Ticks;", "MKV2001")]
    [InlineData("Func<long> read = static () => DateTime.UtcNow.Ticks; _ = read();", "", "MKV2001")]
    [InlineData("_ = Async();", "private static async System.Threading.Tasks.Task<int> Async() { await System.Threading.Tasks.Task.Yield(); return 1; }", "MKV2007")]
    [InlineData("Unsafe();", "private static unsafe void Unsafe() { }", "MKV2007")]
    [InlineData("dynamic value = 1; _ = value.ToString();", "", "MKV2007")]
    [InlineData("_ = _items.Add(1);", "private readonly HashSet<int> _items = new();", "MKV2003")]
    [InlineData("var alias = _items; alias.Add(1);", "private readonly ImmutableArray<int>.Builder _items = ImmutableArray.CreateBuilder<int>();", "MKV2003")]
    [InlineData("output.Complete();", "", "MKV2001")]
    [InlineData("_ = StringComparer.Ordinal.GetHashCode(\"fixed\");", "", "MKV2001")]
    [InlineData("_ = EqualityComparer<string>.Default.GetHashCode(\"fixed\");", "", "MKV2001")]
    [InlineData("_ = new HashData(1).GetHashCode();", "public sealed record HashData(int Value);", "MKV2001")]
    public void 可定位拒绝入口及helper和getter委托的非纯路径(string body, string members, string id)
    {
        string source = $$"""
            public sealed class Probe : IPureKaraokeEffect
            {
                {{members}}
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { {{body}} }
            }
            """;
        AssertError(Analyze(source), id);
    }

    [Theory]
    [InlineData("private static int _count;")]
    [InlineData("private static readonly List<int> _items = new();")]
    [InlineData("private static readonly long _time = DateTime.UtcNow.Ticks;")]
    [InlineData("private static int Count { get; set; }")]
    [InlineData("private static readonly object Value = new List<int>();")]
    public void 拒绝可变静态状态及readonly初始化墙钟(string members)
    {
        string source = $$"""
            public sealed class Probe : IPureKaraokeEffect
            {
                {{members}}
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            }
            """;
        var diagnostics = Analyze(source);
        Assert.Contains(diagnostics, static value => value.Id is "MKV2002" or "MKV2001");
    }

    [Theory]
    [InlineData("private TextOutput? _output;", "_output = output;")]
    [InlineData("", "var handles = new List<TextOutput>(); handles.Add(output);")]
    [InlineData("", "Action later = () => output.Invalidate();")]
    [InlineData("", "Action later = output.Invalidate;")]
    [InlineData("", "object alias = output; var handles = new List<object>(); handles.Add(alias);")]
    public void 拒绝字段集合和闭包保留输出句柄(string members, string body)
    {
        string source = $$"""
            public sealed class Probe : IPureKaraokeEffect
            {
                {{members}}
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { {{body}} }
            }
            """;
        AssertError(Analyze(source), "MKV2005");
    }

    [Fact]
    public void 准备结果不能保留上下文和帧句柄()
    {
        const string source = """
            public sealed record Data(DeclareContext Source) : PreparedEffect;
            public sealed class Probe : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => new Data(source);
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            }
            """;
        var diagnostics = Analyze(source);
        AssertError(diagnostics, "MKV2004"); AssertError(diagnostics, "MKV2005");
    }

    [Theory]
    [InlineData("SourceIndexes", "source.Indexes")]
    [InlineData("SourcePresentationIndex", "source.Indexes.Presentations")]
    [InlineData("SourceLayoutIndex", "source.Indexes.Layout")]
    [InlineData("SourceCursorIndex", "source.Indexes.Cursors")]
    public void 准备不能保留任意源查询服务句柄(string type, string expression)
    {
        string source = $$"""
            public sealed record Data({{type}} Value) : PreparedEffect;
            public sealed class Probe : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => new Data({{expression}});
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            }
            """;
        var diagnostics = Analyze(source);
        AssertError(diagnostics, "MKV2004"); AssertError(diagnostics, "MKV2005");
    }

    [Fact]
    public void 源索引允许当前阶段有限查询但不能保存进闭包()
    {
        const string source = """
            public sealed record Data(ImmutableArray<SourcePresentationLoad> Loads) : PreparedEffect;
            public sealed class Probe : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => new Data(source.Indexes.Presentations.GetRange(new(0, 100), new SourceFilter.Project()));
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    var geometry = frame.Indexes.Layout.GetBaseAt(frame.TimeUs, new SourceFilter.Project());
                    _ = geometry.Lines.Length;
                    var index = frame.Indexes.Layout;
                    Action later = () => index.GetBaseAt(0, new SourceFilter.Project());
                }
            }
            """;
        var diagnostics = Analyze(source);
        AssertError(diagnostics, "MKV2005");
        Assert.DoesNotContain(diagnostics, static value => value.Id == "MKV2006");
    }

    [Fact]
    public void helper可修改调用方创建的局部集合但不能修改外部捕获变量()
    {
        const string source = """
            public sealed class Probe : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                private static void Add(List<int> values) => values.Add(3);
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    var values = new List<int>(); Add(values);
                    int captured = 0; Action mutate = () => captured++; mutate();
                }
            }
            """;
        var diagnostics = Analyze(source);
        AssertError(diagnostics, "MKV2003");
        Assert.DoesNotContain(diagnostics, static value => value.Id == "MKV2003" && value.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("非局部集合", StringComparison.Ordinal));
    }

    [Fact]
    public void 字段集合经局部别名和helper返回仍然不能修改()
    {
        const string source = """
            public sealed class Probe : IPureKaraokeEffect
            {
                private readonly List<int> _items = new();
                private static void Add(List<int> values) => values.Add(1);
                private List<int> Items() => _items;
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                {
                    var alias = _items; Add(alias);
                    var returned = Items(); returned.Add(2);
                }
            }
            """;
        AssertError(Analyze(source), "MKV2003");
    }

    [Fact]
    public void 返回委托不能绕过阶段句柄捕获()
    {
        const string source = """
            public sealed class Probe : IPureKaraokeEffect
            {
                private static Action Later(TextOutput output) => () => output.Invalidate();
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { var later = Later(output); later(); }
            }
            """;
        AssertError(Analyze(source), "MKV2005");
    }

    [Fact]
    public void 外层Frozen不能隐藏可变自定义comparer回调()
    {
        const string source = """
            public sealed class HiddenComparer : IEqualityComparer<int>
            {
                private int _counter;
                public bool Equals(int left, int right) => left == right;
                public int GetHashCode(int value) => _counter++;
            }
            public sealed record Data(FrozenDictionary<int, int> Values) : PreparedEffect;
            public sealed class Probe : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source)
                {
                    var values = new Dictionary<int, int>(new HiddenComparer());
                    values.Add(1, 2); return new Data(values.ToFrozenDictionary(new HiddenComparer()));
                }
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            }
            """;
        AssertError(Analyze(source), "MKV2006");
    }

    [Fact]
    public void 扩展helper不能把另一个局部参数的所有权误授予外部接收者()
    {
        const string source = """
            public static class Extension
            {
                public static void AddFrom(this List<int> external, List<int> local) => external.Add(local.Count);
            }
            public sealed class Probe : IPureKaraokeEffect
            {
                private readonly List<int> _items = new();
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { _items.AddFrom(new List<int>()); }
            }
            """;
        AssertError(Analyze(source), "MKV2003");
    }

    [Fact]
    public void 旧v1和仅同名入口不触发v2规则()
    {
        const string source = """
            public sealed class Probe
            {
                private static int Count;
                public long Evaluate() => DateTime.UtcNow.Ticks + Count++;
            }
            """;
        Assert.Empty(Analyze(source));
    }

    [Fact]
    public void 重载运算符和构造器不能隐藏环境读取()
    {
        const string source = """
            public readonly struct Hidden
            {
                public Hidden(int value) { _ = Environment.TickCount; }
                public static int operator +(Hidden left, Hidden right) => Environment.TickCount;
            }
            public sealed class Probe : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { _ = new Hidden(1) + new Hidden(2); }
            }
            """;
        AssertError(Analyze(source), "MKV2001");
    }

    [Theory]
    [InlineData("")]
    [InlineData("public Probe() { }")]
    public void 隐式和显式入口构造都检查源码祖先构造(string constructor)
    {
        string source = $$"""
            public class Ancestor { protected Ancestor() { _ = DateTime.UtcNow; } }
            public class Parent : Ancestor { }
            public sealed class Probe : Parent, IPureKaraokeEffect
            {
                {{constructor}}
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            }
            """;
        AssertError(Analyze(source), "MKV2001");
    }

    [Theory]
    [InlineData("public void Dispose() { _ = DateTime.UtcNow; }", "")]
    [InlineData("void IDisposable.Dispose() { _ = DateTime.UtcNow; }", "")]
    [InlineData("", "public class Parent : IDisposable { public void Dispose() { _ = DateTime.UtcNow; } }")]
    public void 实际释放入口的显式继承和模块调用不能隐藏非纯能力(string dispose, string parent)
    {
        string inheritance = parent.Length == 0 ? "" : "Parent, ";
        string source = $$"""
            {{parent}}
            public sealed class Probe : {{inheritance}}IPureKaraokeEffect, IDisposable
            {
                {{dispose}}
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            }
            """;
        AssertError(Analyze(source), "MKV2001");
    }

    [Fact]
    public void 局部using的隐式释放也必须纯且允许空释放实现()
    {
        const string source = """
            public sealed class Resource : IDisposable { public void Dispose() { _ = DateTime.UtcNow; } }
            public sealed class Probe : IPureKaraokeEffect, IDisposable
            {
                public void Dispose() { }
                public PreparedEffect Declare(DeclareContext source) { using var local = new Resource(); return PreparedEffect.Empty; }
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            }
            """;
        AssertError(Analyze(source), "MKV2001");
        Assert.DoesNotContain(Analyze(source.Replace("_ = DateTime.UtcNow;", "", StringComparison.Ordinal)),
            static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void 作者析构器依赖非固定GC时机而明确拒绝()
    {
        const string source = """
            public sealed class Probe : IPureKaraokeEffect
            {
                ~Probe() { }
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            }
            """;
        AssertError(Analyze(source), "MKV2007");
    }

    [Fact]
    public void 粒子封闭准备值与四类晚期图元调用属于确定子集()
    {
        const string source = """
            public sealed record Data(ParticleSpec Spec, ParticleIdentity Id, ImmutableArray<FinalSceneGeometry> Frames) : PreparedEffect;
            public sealed class Probe : IPureKaraokeEffect, IPureVisualEffect
            {
                public PreparedEffect Declare(DeclareContext source)
                {
                    var spec = new ParticleSpec { LifetimeUs = 100, SizePx = 1, Color = new(1, 1, 1, 1), SeedStreamKey = "fixed",
                        Motion = new() { Opacity = new(1, 0, new CurveValue.Easing(EasingKind.Linear)) } };
                    var id = new ParticleIdentity(source.InstanceId, new("cursor"), new ParticleOrigin.Continuous(new ParticleHead.Normal(new("track"), new("binding")), 0, 1));
                    var selector = new TimelineSelector(TimelineEventKind.TokenStarted);
                    var item = source.Timeline.Nth(selector, 0);
                    if (item is TokenStarted token) _ = new BurstAnchor.SourceToken(token.TokenId);
                    if (item is ElementVisible { Element: SceneElementIdentity.Mark mark }) _ = mark.NodeId;
                    return new Data(spec, id, []) { VisualOutputs = [new(new("dots"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText)] };
                }
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
                public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
                {
                    foreach (var particle in frame.Particles.Alive)
                    {
                        _ = particle.Birth.Spec.Motion; _ = frame.Particles.GetTrail(particle.Id);
                        _ = frame.FinalGeometry.Playback.Cursors.Length;
                    }
                    output.DrawDots(new("dots"), [new(new(0, 0), 1, new(1, 1, 1, 1))], OverlayLayer.AboveText);
                    output.DrawSegments(new("segments"), [], OverlayLayer.AboveText);
                    output.DrawTrails(new("trails"), [new([new(new(0, 0), 0, new(1, 1, 1, 1)), new(new(1, 1), 1, new(1, 1, 1, 1))])], OverlayLayer.AboveText);
                    output.DrawSprites(new("sprites"), [], OverlayLayer.AboveText);
                }
            }
            """;
        Assert.DoesNotContain(Analyze(source), static value => value.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void 粒子阶段句柄不能保存到准备结果或闭包()
    {
        const string source = """
            public sealed record Data(ParticleFrame Value) : PreparedEffect;
            public sealed class Probe : IPureKaraokeEffect, IPureVisualEffect
            {
                private ParticleFrame? _saved;
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
                public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
                {
                    _saved = frame.Particles;
                    var particles = frame.Particles;
                    Action later = () => { _ = particles.Alive; };
                    later();
                }
            }
            """;
        var diagnostics = Analyze(source); AssertError(diagnostics, "MKV2004"); AssertError(diagnostics, "MKV2005");
    }

    [Theory]
    [InlineData("_ = default(VisualFrame);", "MKV2001")]
    [InlineData("_ = new VisualWriter(new(\"other\"), EffectCapabilities.Overlay);", "MKV2006")]
    [InlineData("Action later = () => output.DrawDots(new(\"dots\"), [], OverlayLayer.AboveText); later();", "MKV2005")]
    public void 晚期入口不能伪造阶段或回退旧生命周期(string body, string code)
    {
        string source = $$"""
            public sealed class Probe : IPureKaraokeEffect, IPureVisualEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
                public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output) { {{body}} }
            }
            """;
        AssertError(Analyze(source), code);
    }

    private static ImmutableArray<Diagnostic> Analyze(string source)
    {
        const string usings = "using System;\nusing System.Collections.Generic;\nusing System.Collections.Immutable;\nusing System.Collections.Frozen;\nusing System.Linq;\nusing MakoKaraGenerator.Scripting;\nusing MakoKaraGenerator.Scripting.V2;\n";
        var compilation = CSharpCompilation.Create("V2PurityProbe",
            [CSharpSyntaxTree.ParseText(usings + source, new(LanguageVersion.CSharp14), "src/Probe.cs")], References.Value,
            new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics().Where(static value => value.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join('\n', errors.Select(static value => value.ToString())));
        return ScriptV2PurityAnalyzer.Analyze(compilation, ["Probe"]);
    }

    private static void AssertError(ImmutableArray<Diagnostic> diagnostics, string id)
        => Assert.Contains(diagnostics, value => value.Id == id && value.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
            && value.Location.IsInSource && value.Location.GetLineSpan().Path == "src/Probe.cs"
            && value.Location.GetLineSpan().StartLinePosition.Line >= 0);
}
