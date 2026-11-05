namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>精确作者草稿保留源码的声明检查；不授予可分发包引用，不返回源码或清单原文。</summary>
public sealed record AuthorSourceInspectionResponse(
    string DraftId,
    string DraftRevision,
    string SourceRevision,
    string PackageId,
    string PackageRevision,
    string Name,
    string Version,
    int PackageVersion,
    string SdkVersion,
    string ShaderProfile,
    bool StructureValid,
    EffectPackageParameterSchemaDto[] Parameters,
    EffectPackageExportSchemaDto[] Exports,
    string[] BindingKinds,
    EffectPackageInputsDto[] Inputs,
    EffectPackageFileSummaryDto[] Files,
    EffectPackageStructureDiagnosticDto[] Diagnostics,
    string ProtocolVersion = BackendProtocol.Version);
