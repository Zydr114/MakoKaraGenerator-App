using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>新增空翻译来源的歌词原值门禁，不包含外观或展示数据。</summary>
/// <param name="ExpectedWorkspace">捕获输入的工作区。</param>
/// <param name="ExpectedWorkspaceGeneration">捕获输入的工作区代次。</param>
/// <param name="ExpectedLyrics">新增前的完整歌词原记录。</param>
/// <param name="Name">名称，按原新增语义去除两端空白。</param>
/// <param name="Language">语言标签；未知时为空。</param>
public sealed record LyricTranslationBranchAddRequest(ProjectWorkspace ExpectedWorkspace, long ExpectedWorkspaceGeneration,
    LyricsDocument ExpectedLyrics, string Name, string? Language = null);

/// <summary>明确来源的完整原记录，防止删除或配置覆盖晚到的输入。</summary>
/// <param name="ExpectedWorkspace">捕获输入的工作区。</param>
/// <param name="ExpectedWorkspaceGeneration">捕获输入的工作区代次。</param>
/// <param name="BranchId">明确的来源标识。</param>
/// <param name="ExpectedBranch">来源完整原记录。</param>
public sealed record LyricBranchEditBaseline(ProjectWorkspace ExpectedWorkspace, long ExpectedWorkspaceGeneration,
    string BranchId, LyricBranch ExpectedBranch);

/// <summary>可由来源配置修改的元数据及既有高级样式覆盖，不允许替换正文、元素、时间或行结构。</summary>
/// <param name="Name">来源名称。</param>
/// <param name="Language">语言标签；未知时为空。</param>
/// <param name="Role">角色；必须保持唯一主来源。</param>
/// <param name="TimingMode">原有时间策略。</param>
/// <param name="StyleOverride">既有高级分支样式覆盖；为空表示继承。</param>
public sealed record LyricBranchConfiguration(string Name, string? Language, LyricBranchRole Role,
    LyricBranchTimingMode TimingMode, LyricStyleOverride? StyleOverride = null);

/// <summary>与元数据一起提交的单行主来源关联；只允许修改 PrimaryLineId。</summary>
/// <param name="LineId">明确行标识。</param>
/// <param name="ExpectedPrimaryLineId">原关联；未关联时为空。</param>
/// <param name="PrimaryLineId">新关联；空值解除，显式新关联须存在于当前主来源。</param>
public sealed record LyricBranchLineAssociationEdit(string LineId, string? ExpectedPrimaryLineId, string? PrimaryLineId);

/// <summary>来源元数据、可选单行关联与可选展示投影构成同一原子候选。</summary>
/// <param name="Baseline">完整原来源及工作区门禁。</param>
/// <param name="Configuration">受控元数据与样式覆盖。</param>
/// <param name="Association">可选的单行关联；不携带其他行字段。</param>
/// <param name="ExpectedPresentation">显式更新展示时的原有效投影；与新展示一起提供。</param>
/// <param name="Presentation">可选的新展示投影；沿原高级能力保留片段、自定义容器和槽位身份。</param>
public sealed record LyricBranchConfigureEditRequest(LyricBranchEditBaseline Baseline, LyricBranchConfiguration Configuration,
    LyricBranchLineAssociationEdit? Association = null, LyricBranchPresentation? ExpectedPresentation = null,
    LyricBranchPresentation? Presentation = null);

/// <summary>仅修改稳定行的主来源关联，使用当前行其余字段而非捕获时的旧行。</summary>
/// <param name="ExpectedWorkspace">捕获输入的工作区。</param>
/// <param name="ExpectedWorkspaceGeneration">捕获输入的工作区代次。</param>
/// <param name="BranchId">明确的非主来源。</param>
/// <param name="LineId">该来源中的稳定行。</param>
/// <param name="ExpectedPrimaryLineId">捕获时的原关联。</param>
/// <param name="PrimaryLineId">新关联；空值解除。</param>
public sealed record LyricPrimaryLineLinkEditRequest(ProjectWorkspace ExpectedWorkspace, long ExpectedWorkspaceGeneration,
    string BranchId, string LineId, string? ExpectedPrimaryLineId, string? PrimaryLineId);
