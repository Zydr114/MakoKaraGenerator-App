using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private static PerformanceTelemetry.ScriptScope MeasureAuthor(ActiveEffect active, string stage)
    {
        if (active.TelemetryIdentity is null || !PerformanceTelemetry.HasFrame) return default;
        return PerformanceTelemetry.MeasureScriptIdentity(active.TelemetryIdentity, stage);
    }
}
