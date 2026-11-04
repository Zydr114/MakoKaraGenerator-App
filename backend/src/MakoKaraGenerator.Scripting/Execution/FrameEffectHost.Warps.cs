using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private sealed class TextWarpBudget
    {
        private int _writes, _vertices, _triangles;
        private long _bytes;
        public void Charge(TextWrite.Warp write)
        {
            int vertices = write.Value?.SourceVertices.Length ?? 0;
            int triangles = (write.Value?.TriangleIndices.Length ?? 0) / 3;
            long bytes = checked(128L + 64L * vertices + 64L * triangles + 6L * triangles);
            if (_writes == 256 || vertices > 65_536 - _vertices || triangles > 131_072 - _triangles || bytes > 8 * 1024 * 1024 - _bytes)
                throw new EffectOutputException("WarpGeometryBudgetExceeded", "全实例网格写入、规范顶点、三角或验证字节超过固定帧限值。");
            _writes = checked(_writes + 1); _vertices = checked(_vertices + vertices);
            _triangles = checked(_triangles + triangles); _bytes = checked(_bytes + bytes);
        }
    }

    private static void ComposeWarps(ActiveEffect active, FrameOutput output, TextWarpBudget budget,
        Dictionary<TextTarget, PreparedTextWarpBinding> warps)
    {
        foreach (var write in output.TextWarps)
        {
            budget.Charge(write);
            var definition = active.Definition;
            string target = write.Target switch { TextTarget.Family => "tokenFamily", TextTarget.Base => "baseToken",
                TextTarget.Cluster => "cluster", TextTarget.Mark => "textMark", _ => throw new EffectOutputException("InvalidWarpTarget", "网格不支持此文字粒度。") };
            if (definition.PackageDeclaration is not { } declaration
                || !declaration.Outputs.Any(item => item.Kind == "VisualTransform" && item.Target == target))
                throw new EffectOutputException("UndeclaredPackageOutput", "网格目标未声明 VisualTransform 输出：" + target);
            warps[write.Target] = new(definition.Id, definition.RuntimeHash, definition.PackageIdentity, write.Target, write.Value);
        }
    }
}
