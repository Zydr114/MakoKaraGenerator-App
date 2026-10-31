namespace MakoKaraGenerator.Core.Validation;

/// <summary>
/// 校验发生的位置。
/// </summary>
/// <remarks>
/// docs/development.md §6.3 要求"打开项目、导入歌词和导出前都应运行统一校验器"，
/// 但同一项检查在不同位置的严重程度不同。例如输出时长未确定：
/// 新建项目时属于正常状态（提示），导出时属于阻断性错误。
/// </remarks>
public enum ValidationContext
{
    /// <summary>打开项目时。</summary>
    Open = 0,

    /// <summary>导入歌词后。</summary>
    Import = 1,

    /// <summary>导出前。</summary>
    Export = 2,
}
