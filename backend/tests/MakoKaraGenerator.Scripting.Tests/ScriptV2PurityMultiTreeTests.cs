using System.Collections.Immutable;
using System.Globalization;
using MakoKaraGenerator.Scripting.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class ScriptV2PurityMultiTreeTests
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        ScriptReferenceIdentity.Read(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), out var images);
        return [.. images.Select(static image => MetadataReference.CreateFromImage(image))];
    });

    [Fact]
    public void 跨树helper和getter构造器与初始化器保留诊断位置和排序()
    {
        SyntaxTree[] trees =
        [
            Tree("src/ZEntry.cs", """
                public sealed class Probe : IPureKaraokeEffect
                {
                    public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                    public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                    {
                        _ = Bridge.Value;
                        _ = Bridge.Read();
                        _ = new Bridge.Box();
                    }
                }
                """),
            Tree("src/BBridge.cs", """
                public static class Bridge
                {
                    public static long Value => Nested.Read();
                    public static long Read() => Nested.Read();
                    public sealed class Box
                    {
                        public Box() { _ = DateTime.UtcNow; }
                    }
                    private static class Nested
                    {
                        public static long Read() => Environment.TickCount64;
                    }
                }
                """),
            Tree("src/AInitializers.cs", """
                public sealed class Initializers
                {
                    private readonly long _stamp = DateTime.Now.Ticks;
                    private static readonly long Stamp = DateTimeOffset.UtcNow.Ticks;
                }
                """),
        ];
        var compilation = CreateCompilation(trees);
        var diagnostics = ScriptV2PurityAnalyzer.Analyze(compilation, ["Probe"]);

        Assert.Collection(diagnostics,
            value => AssertDiagnostic(value, "MKV2001", "src/AInitializers.cs", "DateTime.Now"),
            value => AssertDiagnostic(value, "MKV2001", "src/AInitializers.cs", "DateTimeOffset.UtcNow"),
            value => AssertDiagnostic(value, "MKV2001", "src/BBridge.cs", "DateTime.UtcNow"),
            value => AssertDiagnostic(value, "MKV2001", "src/BBridge.cs", "Environment.TickCount64"),
            value => AssertDiagnostic(value, "MKV2008", "src/ZEntry.cs", "new Bridge.Box()"));
        Assert.Equal(Signatures(diagnostics), Signatures(ScriptV2PurityAnalyzer.Analyze(compilation, ["Probe"])));
        Assert.Equal(Signatures(diagnostics), Signatures(ScriptV2PurityAnalyzer.Analyze(CreateCompilation(trees.Reverse().ToArray()), ["Probe"])));
    }

    [Fact]
    public void 多树嵌套只读准备结果和局部helper仍被接受()
    {
        var diagnostics = ScriptV2PurityAnalyzer.Analyze(CreateReadonlyPackage("node.Next?.Value ?? node.Value"), ["Probe"]);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void 嵌套helper的非纯路径仍在原树报告()
    {
        var diagnostics = ScriptV2PurityAnalyzer.Analyze(CreateReadonlyPackage("(int)DateTime.UtcNow.Ticks"), ["Probe"]);

        AssertDiagnostic(Assert.Single(diagnostics), "MKV2001", "src/Payload.cs", "DateTime.UtcNow");
    }

    [Theory]
    [InlineData("public int Value;", "可写字段")]
    [InlineData("public int Value { get; set; }", "可写属性")]
    public void 多树嵌套准备结果不能隐藏可变成员(string member, string reason)
    {
        var compilation = CreateCompilation(
            Tree("src/Entry.cs", """
                public sealed partial class Probe : IPureKaraokeEffect
                {
                    public PreparedEffect Declare(DeclareContext source) => new Payload(new Nested.Node());
                    public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
                }
                """),
            Tree("src/Payload.cs", $$"""
                public sealed partial class Probe
                {
                    public sealed record Payload(Nested.Node Value) : PreparedEffect;
                    public static class Nested
                    {
                        public sealed class Node { {{member}} }
                    }
                }
                """));

        var diagnostic = Assert.Single(ScriptV2PurityAnalyzer.Analyze(compilation, ["Probe"]));
        Assert.Equal("MKV2004", diagnostic.Id);
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("Probe.Nested.Node.Value", diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Contains(reason, diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void 同一源码树在不同Compilation中绑定到各自helper且重复分析不泄漏状态()
    {
        SyntaxTree entry = Tree("src/Entry.cs", """
            public sealed class Probe : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { _ = Helper.Read(); }
            }
            """);
        var pure = CreateCompilation(entry, Tree("src/Helper.cs", "public static class Helper { public static long Read() => 1; }"));
        var impure = CreateCompilation(entry, Tree("src/Helper.cs", "public static class Helper { public static long Read() => DateTime.UtcNow.Ticks; }"));

        Assert.Empty(ScriptV2PurityAnalyzer.Analyze(pure, ["Probe"]));
        var diagnostics = ScriptV2PurityAnalyzer.Analyze(impure, ["Probe"]);
        AssertDiagnostic(Assert.Single(diagnostics), "MKV2001", "src/Helper.cs", "DateTime.UtcNow");
        Assert.Equal(Signatures(diagnostics), Signatures(ScriptV2PurityAnalyzer.Analyze(impure, ["Probe"])));
        Assert.Empty(ScriptV2PurityAnalyzer.Analyze(pure, ["Probe"]));
    }

    [Fact]
    public void 多树分析的取消保持异常且不影响之后分析()
    {
        var compilation = CreateReadonlyPackage("node.Value");
        var token = new CancellationToken(canceled: true);

        var exception = Assert.Throws<OperationCanceledException>(() => ScriptV2PurityAnalyzer.Analyze(compilation, ["Probe"], token));

        Assert.Equal(token, exception.CancellationToken);
        Assert.Empty(ScriptV2PurityAnalyzer.Analyze(compilation, ["Probe"]));
    }

    private static CSharpCompilation CreateReadonlyPackage(string expression)
        => CreateCompilation(
            Tree("src/Entry.cs", """
                public sealed partial class Probe : IPureKaraokeEffect
                {
                    public PreparedEffect Declare(DeclareContext source) => Helpers.Create();
                    public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
                    {
                        var prepared = (Payload)data;
                        _ = Helpers.Read(prepared.Value);
                        _ = Local(prepared.Value);
                        static int Local(Nested.Node node) => Helpers.Read(node);
                    }
                }
                """),
            Tree("src/Payload.cs", $$"""
                public sealed partial class Probe
                {
                    public sealed record Payload(Nested.Node Value) : PreparedEffect;
                    public static class Nested
                    {
                        public sealed record Node(int Value, Node? Next);
                    }
                    private static class Helpers
                    {
                        public static Payload Create() => new(new Nested.Node(1, new Nested.Node(2, null)));
                        public static int Read(Nested.Node node) => {{expression}};
                    }
                }
                """));

    private static SyntaxTree Tree(string path, string source)
    {
        const string usings = "using System;\nusing MakoKaraGenerator.Scripting;\nusing MakoKaraGenerator.Scripting.V2;\n";
        return CSharpSyntaxTree.ParseText(usings + source, new(LanguageVersion.CSharp14), path);
    }

    private static CSharpCompilation CreateCompilation(params SyntaxTree[] trees)
    {
        var compilation = CSharpCompilation.Create("V2PurityMultiTreeProbe", trees, References.Value,
            new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics().Where(static value => value.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join('\n', errors.Select(static value => value.ToString())));
        return compilation;
    }

    private static void AssertDiagnostic(Diagnostic diagnostic, string id, string path, string expression)
    {
        Assert.Equal(id, diagnostic.Id);
        Assert.True(diagnostic.Location.IsInSource);
        Assert.Equal(path, diagnostic.Location.SourceTree?.FilePath);
        Assert.Equal(expression, diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
    }

    private static string[] Signatures(ImmutableArray<Diagnostic> diagnostics)
        => [.. diagnostics.Select(static value => value.Id + "|" + value.Severity + "|" + value.Location.SourceTree?.FilePath
            + "|" + value.Location.SourceSpan + "|" + value.GetMessage(CultureInfo.InvariantCulture))];
}
