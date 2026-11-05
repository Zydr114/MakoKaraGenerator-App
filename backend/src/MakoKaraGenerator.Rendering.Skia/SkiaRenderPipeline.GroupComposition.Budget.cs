using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    // Frame-only scheduling limits, independent of script metadata or graph protocol.
    // Surface/byte allocations continue to use the existing shared frame/session account.
    internal sealed class GroupCompositionBudget
    {
        internal const int MaximumDepth = 32, MaximumAncestry = 256;
        private const int MaximumUnits = 4096, MaximumLayers = 512, MaximumBindings = 256;
        private const int MaximumResults = 256, MaximumPasses = 8192;
        private int _units, _bindings, _results, _passes, _activeLayers, _replayBatches;
        private long _replayItems;
        private const int MaximumReplayBatches = 4096;
        private const long MaximumReplayItems = 200_000;
        internal void Replay(OverlayBatch batch)
        {
            // Trail.ItemCount is its actual point sum, other kinds their native
            // primitives. Native vector expansion and filters use svgWork too.
            if (batch is not (DotBatch or SegmentBatch or TrailBatch or SpriteBatch or PathBatch or AtlasSpriteBatch))
                throw new PreparedFrameException("UnsupportedOverlayBatch", "下层重绘只接受六类封闭批次。");
            Charge(ref _replayBatches, 1, MaximumReplayBatches, "下层重绘批次");
            long count = batch.ItemCount;
            if (count < 0 || count > MaximumReplayItems - _replayItems)
                throw new PreparedFrameException("GroupCompositionBudgetExceeded", "下层实际累计重绘图元／轨迹点超过固定预算。");
            _replayItems += count;
        }
        internal void Unit() => Charge(ref _units, 1, MaximumUnits, "运行单元");
        internal void Binding() => Charge(ref _bindings, 1, MaximumBindings, "图绑定");
        internal void Result() => Charge(ref _results, 1, MaximumResults, "图历史结果");
        internal void Pass(int count = 1) => Charge(ref _passes, count, MaximumPasses, "捕获／图／合成 pass");
        internal static void Layers(int count)
        { if (count > MaximumLayers) throw Exceeded("作用域来源层", count, MaximumLayers); }
        internal void ActiveLayers(ref int previous, int count)
        {
            Layers(count);
            int next = checked(_activeLayers - previous + count);
            if (next > MaximumLayers) throw Exceeded("全部活动来源层", next, MaximumLayers);
            _activeLayers = next; previous = count;
        }
        internal static void Depth(int depth)
        { if (depth > MaximumDepth) throw Exceeded("组递归深度", depth, MaximumDepth); }
        private static void Charge(ref int current, int add, int limit, string name)
        {
            int next = checked(current + add);
            if (add < 0 || next > limit) throw Exceeded(name, next, limit);
            current = next;
        }
        private static PreparedFrameException Exceeded(string name, int value, int limit)
            => new("GroupCompositionBudgetExceeded", $"{name} {value}/{limit}；不降质或截断父树。");
    }
}
