using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MixedEffectPackageTests
{
    [Theory]
    [InlineData("float", 0)] [InlineData("float2", 0)] [InlineData("float3", 0)] [InlineData("float4", 0)]
    [InlineData("int", 0)] [InlineData("color", 0)] [InlineData("matrix3x3", 0)]
    [InlineData("float", 1)] [InlineData("float2", 2)] [InlineData("float3", 2)] [InlineData("float4", 2)]
    [InlineData("int", 2)] [InlineData("color", 2)] [InlineData("matrix3x3", 2)]
    public async Task 七类与显式数组逐帧覆盖通过真实ABI并产生与固定图相同像素(string type, int length)
    {
        string value = type switch { "float" => "UniformValue.Float(.8)", "float2" => "UniformValue.Float2(.8,.4)",
            "float3" => "UniformValue.Float3(.8,.4,.6)", "float4" => "UniformValue.Float4(.8,.4,.6,.9)",
            "int" => "UniformValue.Int(8)", "color" => "UniformValue.Color(new(.8,.4,.6,.9))",
            _ => "UniformValue.Matrix3x3(.1,.8,.3,.4,.5,.6,.7,.9,1)" };
        double[] numbers = type switch { "float" => [.8], "float2" => [.8, .4], "float3" => [.8, .4, .6],
            "float4" or "color" => [.8, .4, .6, .9], "int" => [8], _ => [.1, .8, .3, .4, .5, .6, .7, .9, 1] };
        string nativeType = type switch { "color" => "half4", "matrix3x3" => "float3x3", _ => type };
        string entry = "amount" + (length > 0 ? $"[{length - 1}]" : "");
        string expression = type switch { "float" => entry, "int" => $"float({entry})*.1", "matrix3x3" => entry + "[1][0]", _ => entry + ".x" };
        string shader = $"{(type == "color" ? "layout(color) " : "")}uniform {nativeType} amount{(length > 0 ? $"[{length}]" : "")}; uniform shader original; half4 main(float2 p) {{half4 c=original.eval(p); return half4(c.rgb*({expression}),c.a);}}";
        void Program(JsonObject program)
        {
            var declaration = program["uniforms"]![0]!; declaration["type"] = type;
            if (length > 0) declaration["arrayLength"] = length;
        }
        void Graph(JsonObject graph, bool final)
        {
            if (type == "color")
            {
                JsonObject Color() => new() { ["r"] = final ? .8 : .2, ["g"] = .4, ["b"] = .6, ["a"] = .9 };
                graph["nodes"]![0]!["uniforms"]!["amount"] = length > 0 ? new JsonArray(Enumerable.Range(0, length).Select(_ => (JsonNode)Color()).ToArray()) : Color();
                return;
            }
            var components = Enumerable.Range(0, Math.Max(1, length)).SelectMany(_ => numbers.Select(n => final ? n : type == "int" ? 2 : .2)).ToArray();
            graph["nodes"]![0]!["uniforms"]!["amount"] = components.Length == 1 && length == 0 ? JsonValue.Create(components[0])
                : new JsonArray(components.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        }
        if (length > 0) value = $"UniformValue.Array(new[]{{{string.Join(',', Enumerable.Repeat(value, length))}}})";
        string source = UniformSource($"foreach(var slot in new[]{{MaterialSlot.UnsungFill,MaterialSlot.SungFill}}) output.Materials.SetUniform(frame.Resources.GetMaterial(\"ink\"),token.WithRuby,slot,\"probe\",\"amount\",{value});");
        var actual = await Compile(MixedEffectPackageFixture.MaterialContents(controllerSource: source, shaderSource: shader, programChange: Program, graphChange: graph => Graph(graph, false)));
        var reference = await Compile(MixedEffectPackageFixture.MaterialContents(shaderSource: shader, programChange: Program, graphChange: graph => Graph(graph, true)));
        Assert.True(actual.Succeeded, string.Join('\n', actual.Artifact.Diagnostics));
        var a = await RenderUniform(actual); var b = await RenderUniform(reference);
        Assert.Equal(b.Pixels, a.Pixels);
        Assert.Contains(a.Pixels.Where((_, i) => i % 4 == 3), alpha => alpha > 100);
    }
}
