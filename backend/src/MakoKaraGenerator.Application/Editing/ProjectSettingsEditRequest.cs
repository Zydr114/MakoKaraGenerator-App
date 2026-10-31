using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>画布宽高作为一个字段组验证与提交。</summary>
/// <param name="ExpectedWorkspace">捕获输入的工作区实例。</param>
/// <param name="ExpectedWorkspaceGeneration">捕获输入的工作区代次。</param>
/// <param name="ExpectedWidth">原画布宽度。</param>
/// <param name="ExpectedHeight">原画布高度。</param>
/// <param name="Width">新画布宽度，必须为正整数。</param>
/// <param name="Height">新画布高度，必须为正整数。</param>
public sealed record ProjectCanvasSizeEditRequest(ProjectWorkspace ExpectedWorkspace, long ExpectedWorkspaceGeneration,
    int ExpectedWidth, int ExpectedHeight, int Width, int Height);

/// <summary>有理数输出帧率的原值及新值。</summary>
/// <param name="ExpectedWorkspace">捕获输入的工作区实例。</param>
/// <param name="ExpectedWorkspaceGeneration">捕获输入的工作区代次。</param>
/// <param name="ExpectedFrameRate">原有理帧率。</param>
/// <param name="FrameRate">新有理帧率，分子分母均须为正。</param>
public sealed record ProjectFrameRateEditRequest(ProjectWorkspace ExpectedWorkspace, long ExpectedWorkspaceGeneration,
    FrameRate ExpectedFrameRate, FrameRate FrameRate);

/// <summary>画布底色的原值及新值；背景媒体不属于此字段组。</summary>
/// <param name="ExpectedWorkspace">捕获输入的工作区实例。</param>
/// <param name="ExpectedWorkspaceGeneration">捕获输入的工作区代次。</param>
/// <param name="ExpectedColor">原底色。</param>
/// <param name="Color">新底色。</param>
public sealed record ProjectBackgroundColorEditRequest(ProjectWorkspace ExpectedWorkspace, long ExpectedWorkspaceGeneration,
    RgbaColor ExpectedColor, RgbaColor Color);

/// <summary>输出时长的原值及新值，均为精确整数微秒。</summary>
/// <param name="ExpectedWorkspace">捕获输入的工作区实例。</param>
/// <param name="ExpectedWorkspaceGeneration">捕获输入的工作区代次。</param>
/// <param name="ExpectedDurationUs">原输出时长，空值为未确定。</param>
/// <param name="DurationUs">新输出时长，允许空值或正整数。</param>
public sealed record ProjectOutputDurationEditRequest(ProjectWorkspace ExpectedWorkspace, long ExpectedWorkspaceGeneration,
    long? ExpectedDurationUs, long? DurationUs);

/// <summary>从当前歌词或已准备媒体长度派生时长前的原值门禁。</summary>
/// <param name="ExpectedWorkspace">捕获输入的工作区实例。</param>
/// <param name="ExpectedWorkspaceGeneration">捕获输入的工作区代次。</param>
/// <param name="ExpectedDurationUs">原输出时长。</param>
public sealed record ProjectOutputDurationEditBaseline(ProjectWorkspace ExpectedWorkspace, long ExpectedWorkspaceGeneration,
    long? ExpectedDurationUs);
