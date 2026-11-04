using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    public PreparedRenderFrame PreparePureVisuals(PreparedRenderFrame text, RuntimeDiagnosticBuffer diagnostics, CancellationToken token)
    {
        try { return PreparePureVisualsCore(text, diagnostics, token); }
        catch (EffectFailureException error) when (error.DiagnosticSnapshot is not { HasError: true })
        {
            // Host particle budgets already identify the actual failing owner,
            // stage and query. Publish that occurrence at the runtime boundary;
            // Worker must not have to invent an owner from a transport failure.
            // Existing Error snapshots and cancellation propagate unchanged.
            // A host failure may carry prior warnings without its own Error.
            var complete = error.DiagnosticSnapshot is { } prefix ? new RuntimeDiagnosticBuffer(prefix) : diagnostics;
            var diagnostic = error.Diagnostic ?? new RuntimeDiagnostic(error.InstanceId, error.ArtifactHash, error.Stage,
                error.TimeUs, new(DiagnosticSeverity.Error, error.Code, error.GetBaseException().Message))
            { PackageIdentity = error.PackageIdentity };
            complete.Add(diagnostic);
            throw new EffectFailureException(error.InstanceId, error.ArtifactHash, error.Stage, error.TimeUs,
                error.Code, error, error.PackageIdentity)
            {
                Diagnostic = diagnostic,
                DiagnosticSnapshot = complete.Snapshot(),
            };
        }
    }
}
