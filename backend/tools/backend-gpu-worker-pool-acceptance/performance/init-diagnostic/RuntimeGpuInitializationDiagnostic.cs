using MakoKaraGenerator.Rendering.Skia;
namespace MakoKaraGenerator.Runtime.Hosting;
/// <summary>Private diagnostic build bridge; contains no author-source or capability data.</summary>
public static class RuntimeGpuInitializationDiagnostic
{
    /// <summary>记录同一所属进程中业务准备阶段的只读计数快照。</summary>
    public static void Mark(string phase, string? sessionId = null) => GpuInitializationDiagnostic.Mark(phase, sessionId);
}
