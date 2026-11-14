using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private static EffectFailureException Failure(CompiledFrameEffect definition, string stage, long timeUs, Exception error, RuntimeDiagnosticSnapshot diagnostics)
        => new(definition.Id, definition.RuntimeHash, stage, timeUs,
            error switch { EffectOutputException output => output.Code, PreparedFrameException prepared => prepared.Code, _ => "ScriptExecutionFailed" }, error, definition.PackageIdentity) { DiagnosticSnapshot = diagnostics };
    private void RequireAudio(CompiledFrameEffect definition, long timeUs)
    {
        if (!definition.RequiresAudio) return;
        var sample = _project.Audio.GetAt(timeUs);
        if (sample.Availability != AudioAvailability.Available)
            throw Failure(definition, "ValidateAudio", timeUs, new EffectOutputException("RequiredAudioUnavailable",
                $"实例要求已分析音频，时刻 {timeUs} 的状态为 {sample.Availability}：{sample.DiagnosticCode}。"), Diagnostics.Snapshot());
    }
    private static void Invoke(ActiveEffect active, string stage, long timeUs, RuntimeDiagnosticBuffer diagnostics, Action action)
    {
        try
        {
            action();
        }
        catch (OperationCanceledException) { throw; }
        catch (EffectFailureException) { throw; }
        catch (Exception error)
        {
            throw Failure(active.Definition, stage, timeUs, error, diagnostics.Snapshot());
        }
    }
}
