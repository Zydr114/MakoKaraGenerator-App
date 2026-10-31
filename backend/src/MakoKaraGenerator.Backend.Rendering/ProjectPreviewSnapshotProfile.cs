using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering;

/// <summary>公共项目预览专用路由；不扩大静态 codec、测量或正式任务能力。</summary>
public static class ProjectPreviewSnapshotProfile
{
    /// <summary>根据完整固定输入选择来源；输入合法性及预算仍由对应 codec 严格检查。</summary>
    public static string Select(ProjectSnapshot snapshot) => ProjectSnapshotSourceProfile.Select(snapshot);

    /// <summary>无损编码所选预览来源，包和静态合同仍严格互斥。</summary>
    public static byte[] Encode(ProjectSnapshot snapshot) => ProjectSnapshotSourceProfile.Encode(snapshot);
}
