namespace MakoKaraGenerator.Rendering;

/// <summary>固定包编译的真实完整源位置；与Runtime发生快照独立。</summary>
public sealed record RenderSourceDiagnostic(string PackageId, string Revision, string? InstanceId, string? ExportId,
    string Code, string Message, bool IsError, string? Path, int Line, int Column);
