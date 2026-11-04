using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private static ImageEffectTarget GraphTarget(TextTarget target) => target switch
    {
        TextTarget.Family family => new ImageEffectTarget.Token(family.Value),
        TextTarget.Line line => new ImageEffectTarget.Line(line.Value),
        TextTarget.Mark mark => new ImageEffectTarget.Mark(mark.Value),
        _ => throw new EffectOutputException("PropertyTargetUnsupported", "纯图绑定只支持真实字族、整行和提示标记。"),
    };

    private static void ValidatePureGraphWrites(CompiledFrameEffect definition, FrameOutput output,
        GraphBindingBudget frameBudget, CancellationToken token)
    {
        if (!definition.IsV2)
        {
            if (!output.GraphWrites.IsEmpty) throw new EffectOutputException("InvalidGraphOutputStage", "纯图事务不能通过旧 SDK 生命周期发布。");
            return;
        }
        if (output.GraphWrites.IsDefault) throw new EffectOutputException("InvalidGraphBindingValue", "图写事务不能是 default。");
        Dictionary<ImageEffectTarget, ImageEffectBinding> final = [];
        foreach (var write in output.GraphWrites)
        {
            token.ThrowIfCancellationRequested();
            var binding = new ImageEffectBinding(GraphTarget(write.Target), write.Value);
            frameBudget.Charge(write.Value, token);
            // Export ownership, declaration and parameter schema belong to this
            // complete write; shader/node compatibility belongs to final identity.
            ValidatePackageGraphBinding(definition, binding, false, token);
            final[binding.Target] = binding;
        }
        if (!output.ImageEffects.SequenceEqual(final.Values))
            throw new EffectOutputException("InvalidGraphBindingValue", "图最终绑定必须与完整有序事务的最后完整值一致。");
    }
}
