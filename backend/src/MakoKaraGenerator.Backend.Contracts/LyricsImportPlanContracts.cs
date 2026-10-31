using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>固定授权歌词资源的导入前信息读取；不创建批准票据或修改工程。</summary>
[JsonConverter(typeof(LyricsImportPlanRequestJsonConverter))]
public sealed record LyricsImportPlanRequest(string? AssetId, LyricsImportOptionsDto? Options = null);

/// <summary>原注册表匹配的格式及稳定标识。</summary>
public sealed record LyricsImportFormatCandidateDto(string ImporterId, string Name, string SourceFormat, string Reason);
/// <summary>现有导入选项支持的编码；不是统计概率。</summary>
public sealed record LyricsImportEncodingCandidateDto(string Name, string Reason);
/// <summary>解析文档的原始来源身份；仅供后续显式选择。</summary>
public sealed record LyricsImportBranchCandidateDto(string Id, string Name, ProjectLyricBranchRole Role, int LineCount);

/// <summary>即时读取结果；预览身份来自源文档，正式导入仍由原命令分配身份。</summary>
public sealed record LyricsImportPlanResponse(string AssetId, string FileName, int ByteLength,
    string? DetectedImporterId, string? DetectionReason, bool IsFormatAmbiguous,
    LyricsImportFormatCandidateDto[] FormatCandidates, string? EncodingName, bool HadByteOrderMark,
    bool IsEncodingAmbiguous, LyricsImportEncodingCandidateDto[] EncodingCandidates,
    LyricsImportBranchCandidateDto[] SourceBranches, string? SelectedSourceBranchId,
    LyricsImportReportDto? Report, ProjectLyricLineDto[] PreviewLines,
    BackendErrorResponse? PreparationError = null, string ProtocolVersion = BackendProtocol.Version);
