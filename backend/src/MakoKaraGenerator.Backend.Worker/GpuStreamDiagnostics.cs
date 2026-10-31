using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Backend.Worker;

// Counts describe the actual snapshot; only the textual projection is bounded.
internal static class GpuStreamDiagnostics
{
    internal static GpuStreamDiagnosticSummary Summarize(GpuRenderFrame frame)
    {
        GpuStreamDiagnosticSummary runtime = Summarize(frame.DiagnosticSnapshot);
        var items = runtime.Items.ToList();
        bool truncated = runtime.Truncated;
        foreach (var diagnostic in frame.ImageEffectDiagnostics)
            Add(new("Warning", diagnostic.Code, "Image effect fallback", diagnostic.InstanceId.Value, "image"));
        foreach (var diagnostic in frame.MaterialDiagnostics)
            Add(new("Warning", diagnostic.Code, "Material fallback", diagnostic.OwnerDescription, "material"));
        int warnings = checked(frame.ImageEffectDiagnostics.Length + frame.MaterialDiagnostics.Length);
        return new(checked(runtime.Total + warnings), checked(runtime.WarningCount + warnings), runtime.ErrorCount,
            truncated, [.. items]);
        void Add(GpuStreamDiagnosticItem item)
        {
            if (items.Count == 4) { truncated = true; return; }
            items.Add(Bound(item, ref truncated));
        }
    }
    internal static GpuStreamDiagnosticSummary Summarize(RuntimeDiagnosticSnapshot snapshot)
    {
        List<GpuStreamDiagnosticItem> items = [];
        int warnings = 0, errors = 0;
        bool truncated = false;
        foreach (var occurrence in snapshot.ReadAll())
        {
            RuntimeDiagnostic diagnostic = occurrence.Diagnostic;
            if (diagnostic.Diagnostic.Severity == DiagnosticSeverity.Warning) warnings++;
            if (diagnostic.Diagnostic.Severity == DiagnosticSeverity.Error) errors++;
            if (items.Count == 4) { truncated = true; continue; }
            items.Add(Bound(new(diagnostic.Diagnostic.Severity.ToString(), diagnostic.Diagnostic.Code,
                diagnostic.Diagnostic.Message, diagnostic.InstanceId.Value, diagnostic.Stage), ref truncated));
        }
        return new(snapshot.Count, warnings, errors, truncated, [.. items]);
    }
    private static GpuStreamDiagnosticItem Bound(GpuStreamDiagnosticItem item, ref bool truncated) =>
        new(item.Severity, Cut(item.Code, 64, ref truncated), Cut(item.Message, 192, ref truncated),
            item.SourceId is null ? null : Cut(item.SourceId, 96, ref truncated),
            item.Stage is null ? null : Cut(item.Stage, 48, ref truncated));
    private static string Cut(string value, int length, ref bool truncated)
    {
        if (value.Length <= length) return value;
        truncated = true;
        // Keep UTF-16 surrogate pairs intact before JSON escaping.
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }
}
