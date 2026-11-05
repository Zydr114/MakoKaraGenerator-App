using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    private sealed class AuthorAdoptionConfigurationException(string field, string message,
        string failureCode = "AuthorConfigurationInvalid", BackendErrorCode errorCode = BackendErrorCode.InvalidRequest) : ArgumentException(message)
    {
        internal string Field { get; } = field;
        internal string FailureCode { get; } = failureCode;
        internal BackendErrorCode ErrorCode { get; } = errorCode;
    }

    private static AuthorDraftConfigurationDocument NormalizeAdoptionConfiguration(AuthorDraftDocument document,
        string sourceRevision, AuthorPackageConfigurationInputDto input)
    {
        static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string field, string message)
        { if (!condition) throw new AuthorAdoptionConfigurationException("configuration." + field, message); }
        Require(!string.IsNullOrWhiteSpace(input.Name) && input.Name.Length <= 256, "name", "配置名称不能为空且不得超过256字符。");
        Require(!string.IsNullOrWhiteSpace(input.ExportId), "exportId", "配置必须指定完整导出身份。");
        Require(Enum.IsDefined(input.RelativeTarget), "relativeTarget", "配置目标无效。");
        Require(Enum.IsDefined(input.ReadScope), "readScope", "读取范围无效。");
        Require(Enum.IsDefined(input.OverlayLayer), "overlayLayer", "图元层无效。");
        Require(input.ReadScope == TemplateScriptReadScopeDto.BranchInput ? !string.IsNullOrWhiteSpace(input.BranchInput) : input.BranchInput is null,
            "branchInput", "分支读取范围必须提供branchInput，其他范围必须为null。");
        Require(input.FixedSeed is null || input.FixedSeed.Length != 0 && ulong.TryParse(input.FixedSeed, NumberStyles.None, CultureInfo.InvariantCulture, out _),
            "fixedSeed", "fixedSeed必须是UInt64十进制字符串或null。");
        Require(input.ParametersJson is not null, "parametersJson", "参数必须提供完整JSON对象。");
        try
        {
            return NormalizeConfiguration(document, new(input.Name, document.PackageId, sourceRevision, input.ExportId,
                input.ParametersJson, input.RelativeTarget, input.ReadScope, input.BranchInput, input.FixedSeed, input.OverlayLayer));
        }
        catch (EffectPackageException error)
        { throw new AuthorAdoptionConfigurationException("configuration.parametersJson", error.Message, error.Code, BackendErrorCode.CommandRejected); }
        catch (JsonException)
        { throw new AuthorAdoptionConfigurationException("configuration.parametersJson", "参数必须为合法JSON对象。"); }
    }

    private static ScriptConfiguration BuildAdoptionConfiguration(EffectPackageContents package,
        AuthorDraftConfigurationDocument document)
    {
        // Material configuration restrictions have no ParamName in the shared constructor.
        // Attach the exact offending author field while retaining the shared validation itself.
        EffectExportDefinition export;
        try { export = EffectPackageDefinition.Read(package).GetExport(document.ExportId); }
        catch (EffectPackageException error) when (error.Code == "PackageExportMissing")
        { throw new AuthorAdoptionConfigurationException("configuration.exportId", error.Message, error.Code, BackendErrorCode.CommandRejected); }
        if (export is EffectMaterialDefinition)
        {
            string? field = document.RelativeTarget != nameof(ScriptRelativeTarget.Application) ? "relativeTarget"
                : document.ReadScope != nameof(ScriptReadScope.BoundSources) ? "readScope"
                : document.FixedSeed is not null ? "fixedSeed"
                : document.OverlayLayer != nameof(EffectOverlayLayer.Package) ? "overlayLayer" : null;
            if (field is not null) throw new AuthorAdoptionConfigurationException("configuration." + field,
                "源材质只接受Application、BoundSources、空种子与Package图元层。");
        }
        try { return BuildConfiguration(package, document); }
        catch (EffectPackageException error) when (error.Path == "parameters" || error.Path.StartsWith("parameters.", StringComparison.Ordinal)
            || error.Code == "EffectFieldDuplicate")
        { throw new AuthorAdoptionConfigurationException("configuration.parametersJson", error.Message, error.Code, BackendErrorCode.CommandRejected); }
        catch (ArgumentException error) when (error.ParamName is "target" or "overlayLayer" or "branchInput" or "exportId")
        {
            string field = error.ParamName == "target" ? "relativeTarget" : error.ParamName!;
            throw new AuthorAdoptionConfigurationException("configuration." + field, error.Message);
        }
    }
}
