namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>统一固定工程输入的私有握手与打开入口；帧与关闭沿用 package 会话。</summary>
public static class ProjectRuntimeInputWorkerContract
{
    public const string Profile = "runtime-project-input-v1";
    public const string CapabilitiesKind = "runtime-project-input-capabilities-v1";
    public const string CapabilitiesResultKind = "runtime-project-input-capabilities-result-v1";
    public const string OpenKind = "runtime-project-input-session-open-v1";
}
