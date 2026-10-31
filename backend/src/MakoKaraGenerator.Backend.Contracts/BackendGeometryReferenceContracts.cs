namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>固定容器参考读取的输入预算；超限整批拒绝，不截断树。</summary>
public static class ProjectGeometryReferenceLimits
{
    public const int MaxNodes = 1024;
    public const int MaxDepth = 64;
    public const int MaxInputItems = 20000;
    public const int MaxInputIdBytes = 65536;
    public const int MaxTargetIdBytes = 1024;
}

/// <summary>画布像素坐标中的点；不是最终绘制像素边界。</summary>
public sealed record ProjectGeometryPointDto(double X, double Y);

/// <summary>x'=A*x+C*y+Tx，y'=B*x+D*y+Ty；父矩阵乘子矩阵。</summary>
public sealed record ProjectGeometryMatrixDto(double A, double B, double C, double D, double Tx, double Ty);

/// <summary>Core 固定参考矩形及父编辑坐标；不包含文本测量、自动布局、动画或效果变换。</summary>
public sealed record ProjectGeometryReferenceDto(double Width, double Height, ProjectGeometryMatrixDto WorldMatrix,
    ProjectGeometryPointDto Pivot, ProjectGeometryPointDto[] WorldCorners);

/// <summary>精确修订下单节点固定参考。四角依次为左上、右上、右下、左下；不代表 SceneEditorGeometry 或最终 Runtime 像素几何。</summary>
public sealed record ProjectGeometryReferenceResponse(string ProjectSessionId, string ProjectRevision, string NodeId,
    string? ParentId, int CanvasWidth, int CanvasHeight, ProjectGeometryReferenceDto Reference,
    ProjectGeometryReferenceDto ParentReference, string ReferenceKind = "fixedContainerReferenceV1",
    bool FinalPixelGeometry = false, string ProtocolVersion = BackendProtocol.Version);
