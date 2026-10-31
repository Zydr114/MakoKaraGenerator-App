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
    private static void Invoke(ActiveEffect active, string stage, long timeUs, RuntimeDiagnosticBuffer diagnostics, Action<DiagnosticReporter> action)
    {
        RuntimeDiagnostic? fatal = null;
        DiagnosticReporter reporter = new(diagnostic =>
        {
            RuntimeDiagnostic item = new(active.Definition.Id, active.Definition.RuntimeHash, stage, timeUs, diagnostic)
            { PackageIdentity = active.Definition.PackageIdentity };
            diagnostics.Add(item); if (diagnostic.Severity == DiagnosticSeverity.Error) fatal ??= item;
        });
        try
        {
            action(reporter);
            if (fatal is not null)
                throw new EffectFailureException(active.Definition.Id, active.Definition.RuntimeHash, stage, timeUs,
                    fatal.Diagnostic.Code, new EffectOutputException(fatal.Diagnostic.Code, fatal.Diagnostic.Message), active.Definition.PackageIdentity)
                    { Diagnostic = fatal, DiagnosticSnapshot = diagnostics.Snapshot() };
        }
        catch (OperationCanceledException) { throw; }
        catch (EffectFailureException) { throw; }
        catch (Exception error)
        {
            if (fatal is not null)
                throw new EffectFailureException(active.Definition.Id, active.Definition.RuntimeHash, stage, timeUs,
                    fatal.Diagnostic.Code, error, active.Definition.PackageIdentity)
                    { Diagnostic = fatal, DiagnosticSnapshot = diagnostics.Snapshot() };
            throw Failure(active.Definition, stage, timeUs, error, diagnostics.Snapshot());
        }
        finally { reporter.Invalidate(); }
    }
}
