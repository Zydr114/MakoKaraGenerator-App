namespace MakoKaraGenerator.Core.Diagnostics;

/// <summary>
/// 诊断严重程度。
/// </summary>
/// <remarks>
/// 分类含义见 docs/development.md §6.3：
/// <list type="bullet">
///   <item><see cref="Error"/>：无法继续导出；</item>
///   <item><see cref="Warning"/>：允许继续，但可能产生非预期结果；</item>
///   <item><see cref="Info"/>：不影响输出的建议。</item>
/// </list>
/// 同一项检查在不同上下文（打开 / 导入 / 导出前）可能给出不同严重程度，
/// 例如"输出时长未确定"在打开时只是提示，在导出时是阻断性错误。
/// </remarks>
public enum IssueSeverity
{
    /// <summary>提示：不影响输出，只是建议。</summary>
    Info = 0,

    /// <summary>警告：允许继续，但可能产生非预期结果。</summary>
    Warning = 1,

    /// <summary>错误：无法继续导出。</summary>
    Error = 2,
}
