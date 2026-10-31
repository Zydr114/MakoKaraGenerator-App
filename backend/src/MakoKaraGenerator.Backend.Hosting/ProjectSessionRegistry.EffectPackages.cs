using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    /// <summary>固定已授权归档或精确库/模板修订的完整闭包；不编译、执行脚本。</summary>
    public async Task<BackendOperationResult<EffectPackageContents>> ResolveEffectPackageReferenceAsync(string clientId,
        EffectPackageReferenceDto reference, CancellationToken cancellationToken = default)
    {
        if (reference is null || string.IsNullOrWhiteSpace(reference.PackageId) || string.IsNullOrWhiteSpace(reference.PackageRevision))
            return PackageReject<EffectPackageContents>(BackendErrorCode.InvalidRequest, "包引用必须提供身份和精确修订。", "reference");
        if (reference.Kind == "configuration" && !string.IsNullOrWhiteSpace(reference.ConfigurationId)
            && !string.IsNullOrWhiteSpace(reference.ConfigurationRevision)
            && reference.PackageAssetId is null && reference.LibraryId is null && reference.LibraryRevision is null
            && reference.TemplateId is null && reference.TemplateRevision is null)
        {
            if (_scriptConfigurations is null) return PackageReject<EffectPackageContents>(BackendErrorCode.TaskUnavailable, "脚本配置库尚未配置。", "reference.configurationId");
            var library = _scriptConfigurations.Read();
            if (library.Issues.Count != 0) return PackageReject<EffectPackageContents>(BackendErrorCode.ProjectLoadFailed, "脚本配置库无效。", "reference.configurationId");
            if (RevisionText(library.Revision) != reference.ConfigurationRevision)
                return PackageReject<EffectPackageContents>(BackendErrorCode.RevisionConflict, "配置库修订已经改变。", "reference.configurationRevision");
            var config = library.Configurations.FirstOrDefault(item => item.Id == reference.ConfigurationId);
            if (config is null) return PackageReject<EffectPackageContents>(BackendErrorCode.ResourceNotFound, "配置不存在。", "reference.configurationId");
            return CheckPackageIdentity(config.Package, reference);
        }
        if (reference.Kind == "asset" && !string.IsNullOrWhiteSpace(reference.PackageAssetId)
            && reference.LibraryId is null && reference.LibraryRevision is null && reference.TemplateId is null && reference.TemplateRevision is null
            && reference.ConfigurationId is null && reference.ConfigurationRevision is null)
        {
            var result = await ReadEffectPackageAssetAsync(clientId, reference.PackageAssetId, cancellationToken).ConfigureAwait(false);
            if (!result.Accepted) return result;
            return CheckPackageIdentity(result.Response!, reference);
        }
        if (reference.Kind != "library" || reference.PackageAssetId is not null || reference.ConfigurationId is not null || reference.ConfigurationRevision is not null || string.IsNullOrWhiteSpace(reference.LibraryId)
            || string.IsNullOrWhiteSpace(reference.LibraryRevision) || string.IsNullOrWhiteSpace(reference.TemplateId)
            || string.IsNullOrWhiteSpace(reference.TemplateRevision))
            return PackageReject<EffectPackageContents>(BackendErrorCode.InvalidRequest, "包引用来源字段必须与kind一致。", "reference");
        return await ReadTemplateLibraryAsync(reference.LibraryId, reference.LibraryRevision, snapshot =>
        {
            var template = snapshot.Templates.FirstOrDefault(value => value.Id == reference.TemplateId);
            if (template is null) return PackageReject<EffectPackageContents>(BackendErrorCode.ResourceNotFound, "模板不存在。", "reference.templateId");
            if (TemplateSummary(template).TemplateRevision != reference.TemplateRevision)
                return PackageReject<EffectPackageContents>(BackendErrorCode.RevisionConflict, "模板内容修订已经改变。", "reference.templateRevision");
            var package = template.Packages.FirstOrDefault(value => value.PackageId == reference.PackageId && value.Revision == reference.PackageRevision);
            return package is null
                ? PackageReject<EffectPackageContents>(BackendErrorCode.ResourceNotFound, "模板中没有所引用的固定包。", "reference.packageId")
                : BackendOperationResult<EffectPackageContents>.Success(package);
        }, cancellationToken).ConfigureAwait(false);
    }

    public static EffectPackageReferenceDto CreateLibraryEffectPackageReference(string libraryId, string libraryRevision,
        string templateId, string templateRevision, EffectPackageContents package) => new("library", package.PackageId,
            package.Revision, LibraryId: libraryId, LibraryRevision: libraryRevision, TemplateId: templateId, TemplateRevision: templateRevision);

    /// <summary>完整结构检查，语义错误保留原清单并返回结构诊断；不调用编译器。</summary>
    public async Task<BackendOperationResult<EffectPackageInspectResponse>> InspectEffectPackageAsync(string clientId,
        EffectPackageInspectRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || (request.Reference is null) == (request.PackageAssetId is null)
            || request.Reference is not null && request.ExpectedPackageRevision is not null
            || request.PackageAssetId is not null && string.IsNullOrWhiteSpace(request.PackageAssetId))
            return PackageReject<EffectPackageInspectResponse>(BackendErrorCode.InvalidRequest, "必须选择一个包资源或完整引用。", "reference");
        var resolved = request.Reference is not null
            ? await ResolveEffectPackageReferenceAsync(clientId, request.Reference, cancellationToken).ConfigureAwait(false)
            : await ReadEffectPackageAssetAsync(clientId, request.PackageAssetId!, cancellationToken).ConfigureAwait(false);
        if (!resolved.Accepted) return BackendOperationResult<EffectPackageInspectResponse>.Rejected(resolved.Error!);
        var package = resolved.Response!;
        if (request.ExpectedPackageRevision is not null && request.ExpectedPackageRevision != package.Revision)
            return PackageReject<EffectPackageInspectResponse>(BackendErrorCode.RevisionConflict, "效果包内容修订不匹配。", "expectedPackageRevision");
        var reference = request.Reference ?? new EffectPackageReferenceDto("asset", package.PackageId, package.Revision, request.PackageAssetId);
        return BackendOperationResult<EffectPackageInspectResponse>.Success(CreateEffectPackageInspection(package, reference, cancellationToken));
    }

    /// <summary>同一不可变包的只读投影，供完整模板详情与独立inspect复用。</summary>
    public static EffectPackageInspectResponse CreateEffectPackageInspection(EffectPackageContents package,
        EffectPackageReferenceDto reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package); ArgumentNullException.ThrowIfNull(reference);
        using var document = JsonDocument.Parse(package.Manifest.AsMemory());
        var manifest = document.RootElement;
        EffectPackageDefinition? definition = null;
        EffectPackageStructureDiagnosticDto[] diagnostics = [];
        try { definition = EffectPackageDefinition.Read(package, cancellationToken); }
        catch (EffectPackageException error) { diagnostics = [new(error.Code, error.Path, error.Message)]; }
        // Only validated declarations become typed UI schemas; malformed input remains available in Manifest.
        EffectPackageParameterSchemaDto[] parameters = definition is null ? [] : [.. manifest.GetProperty("parameters").EnumerateArray().Select(value =>
            new EffectPackageParameterSchemaDto(value.GetProperty("id").GetString()!, value.GetProperty("type").GetString()!,
                value.GetProperty("label").GetString()!, value.TryGetProperty("unit", out var unit) ? unit.GetString() : null,
                value.GetProperty("default").GetRawText(), value.TryGetProperty("min", out var min) ? min.GetRawText() : null,
                value.TryGetProperty("max", out var max) ? max.GetRawText() : null,
                value.TryGetProperty("values", out var options) ? [.. options.EnumerateArray().Select(option =>
                    new EffectPackageParameterOptionDto(option.GetProperty("value").GetString()!, option.GetProperty("label").GetString()!))] : []))];
        EffectPackageExportSchemaDto[] exports = definition is null ? [] : [.. manifest.GetProperty("exports").EnumerateArray().Select(value =>
            new EffectPackageExportSchemaDto(value.GetProperty("id").GetString()!, value.GetProperty("kind").GetString()!,
                value.GetProperty("name").GetString()!, [.. value.GetProperty("parameters").EnumerateArray().Select(item => item.GetString()!)],
                value.TryGetProperty("bindingKinds", out var kinds) ? [.. kinds.EnumerateArray().Select(item => item.GetString()!)] : [], value.Clone()))];
        List<EffectPackageInputsDto> inputs = [];
        if (definition is not null) foreach (var file in package.Files.Where(value => value.Role == "graph"))
        {
            // The shared semantic reader accepts a UTF-8 BOM; this read-only projection must accept the same files.
            ReadOnlyMemory<byte> content = file.Content.AsMemory();
            if (content.Span.StartsWith("\uFEFF"u8)) content = content[3..];
            using var graph = JsonDocument.Parse(content);
            if (graph.RootElement.TryGetProperty("inputs", out var declarations)) inputs.Add(new(file.Path, declarations.Clone()));
        }
        return new(reference, package.PackageId, package.Revision,
            package.Name, package.Version, manifest.Clone(), manifest.GetRawText(), definition is not null, parameters, exports,
            [.. exports.SelectMany(value => value.BindingKinds).Distinct(StringComparer.Ordinal)], [.. inputs],
            [.. package.Files.Select(file => new EffectPackageFileSummaryDto(file.Path, file.Role, file.Content.Length, file.Sha256))], diagnostics);
    }

    private async Task<BackendOperationResult<EffectPackageContents>> ReadEffectPackageAssetAsync(string clientId, string assetId, CancellationToken token)
    {
        if (!BeginTemplateOperation()) return TemplateClosing<EffectPackageContents>();
        try
        {
            // Acquire first: expiry/collection cannot remove the storage file until its full immutable copy is parsed.
            using var lease = _resources.AcquireReference(clientId, assetId);
            var asset = _resources.GetAuthorized(clientId, assetId);
            var package = await EffectPackageTransfer.ReadArchiveAsync(asset.StoragePath, ProjectEffectsJson.PackageProfile,
                cancellationToken: token).ConfigureAwait(false);
            return BackendOperationResult<EffectPackageContents>.Success(package);
        }
        catch (ResourceStoreException error) { return PackageReject<EffectPackageContents>(error.Code, error.Message, error.Field); }
        catch (EffectPackageException error) { return PackageReject<EffectPackageContents>(BackendErrorCode.CommandRejected, error.Message, error.Path, error.Code); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return PackageReject<EffectPackageContents>(BackendErrorCode.CommandRejected, "无法读取受控效果包资源。", "packageAssetId", "PackageReadFailed"); }
        finally { EndTemplateOperation(); }
    }

    private static BackendOperationResult<EffectPackageContents> CheckPackageIdentity(EffectPackageContents package, EffectPackageReferenceDto reference) =>
        package.PackageId != reference.PackageId || package.Revision != reference.PackageRevision
            ? PackageReject<EffectPackageContents>(BackendErrorCode.RevisionConflict, "包身份或内容修订不匹配。", "reference.packageRevision")
            : BackendOperationResult<EffectPackageContents>.Success(package);
    private static BackendOperationResult<T> PackageReject<T>(BackendErrorCode code, string message, string? field, string? failureCode = null) =>
        BackendOperationResult<T>.Rejected(new(code, message, field, FailureCode: failureCode));
}
