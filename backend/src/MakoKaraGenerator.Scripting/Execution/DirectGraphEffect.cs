using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Scripting.Execution;

/// <summary>宿主纯图求值入口；只投影固定图绑定，不编译源码、创建 DLL 或执行图像后端。</summary>
internal sealed class DirectGraphEffect(EffectGraphDefinition graph) : EffectBase
{
    public override void Evaluate(FrameContext frame, FrameWriter output)
    {
        GraphHandle handle = frame.Resources.GetGraph(graph.Id);
        foreach (var declaration in graph.Outputs)
        {
            frame.CancellationToken.ThrowIfCancellationRequested();
            switch (declaration.Target)
            {
                case "tokenFamily":
                    foreach (var token in frame.Presentations.TokensInBinding)
                    {
                        frame.CancellationToken.ThrowIfCancellationRequested();
                        output.ImageEffects.Bind(token.WithRuby, handle, frame.Parameters);
                    }
                    break;
                case "line":
                    foreach (var line in frame.Presentations.LinesInBinding)
                    {
                        frame.CancellationToken.ThrowIfCancellationRequested();
                        output.ImageEffects.Bind(line.Target, handle, frame.Parameters);
                    }
                    break;
                case "textMark":
                    foreach (var mark in frame.Presentations.MarksInBinding)
                    {
                        frame.CancellationToken.ThrowIfCancellationRequested();
                        output.ImageEffects.Bind(mark.Target, handle, frame.Parameters);
                    }
                    break;
                default:
                    throw new EffectOutputException("PackageWriterPending", "直接图目标未通过固定生产门禁。");
            }
        }
    }
}
