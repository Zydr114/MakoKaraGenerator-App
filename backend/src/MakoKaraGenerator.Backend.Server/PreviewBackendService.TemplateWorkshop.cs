using System.Globalization;
using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Runtime.Hosting.Authoring;

namespace MakoKaraGenerator.Backend.Server;

internal sealed partial class PreviewBackendService
{
    public Task<BackendOperationResult<TemplatePreviewOptionsResponse>> ReadTemplatePreviewOptionsAsync(
        string client, TemplatePreviewOptionsRequest request, CancellationToken token = default) => MutateAsync(client, async () =>
    {
        var resolved = await ResolveTemplatePreviewSourceAsync(client, request.Source, token).ConfigureAwait(false);
        if (!resolved.Accepted) return BackendOperationResult<TemplatePreviewOptionsResponse>.Rejected(resolved.Error!);
        StyleTemplate template = resolved.Response!;
        CanonicalStyleDto style = ProjectSessionRegistry.ExportFullTemplateStyleForPreview(template);
        TemplateMaterialSlotOptionDto[] slots =
        [
            Slot(TemplateMaterialSlot.UnsungFill, style.UnsungFill.Material, LyricStyleProperty.UnsungFill),
            Slot(TemplateMaterialSlot.SungFill, style.SungFill.Material, LyricStyleProperty.SungFill),
            Slot(TemplateMaterialSlot.Stroke, style.Stroke?.Material, LyricStyleProperty.UnsungStrokes),
        ];
        TemplateSampleOptionDto[] samples =
        [
            Sample(TemplatePreviewLimits.SingleLineSampleId, false),
            Sample(TemplatePreviewLimits.MultiLineSampleId, true),
        ];
        return BackendOperationResult<TemplatePreviewOptionsResponse>.Success(new(TemplateSourceIdentity(request.Source), samples, slots));

        TemplateMaterialSlotOptionDto Slot(TemplateMaterialSlot slot, StyleMaterialDto? material, params LyricStyleProperty[] properties) =>
            new(SlotName(slot), true, properties.Any(template.Properties.Contains), material is not null, material);
        TemplateSampleOptionDto Sample(string id, bool multi)
        {
            Project scene = TemplateSampleScene.WorkshopScene(multi, null);
            return new(id, multi, TemplateSampleScene.DurationUs.ToString(CultureInfo.InvariantCulture),
                [.. TemplateSampleScene.Targets(scene, template).Select(target => new TemplateSampleTargetDto(target.Key,
                    new(BindingName(target.Binding.Kind), target.Binding.Id, [.. target.Binding.Ids], target.Binding.IncludeDescendants)))]);
        }
    }, token);

    private async Task<BackendOperationResult<StyleTemplate>> ResolveTemplatePreviewSourceAsync(string client,
        TemplatePreviewSourceDto? source, CancellationToken token = default)
    {
        if (ValidateTemplateSource(source) is { } error) return BackendOperationResult<StyleTemplate>.Rejected(error);
        if (source!.Kind == "savedTemplate")
            return await _projects.ReadStaticTemplateForPreviewAsync(source.LibraryId!, source.LibraryRevision!,
                source.TemplateId!, source.TemplateRevision!, token).ConfigureAwait(false);
        return source.DefinitionFormat == "full"
            ? await _projects.CreateFullDraftTemplateForPreviewAsync(client, source.DraftRevision!,
                (FullTemplateDefinitionDto)source.Definition!, token).ConfigureAwait(false)
            : ProjectSessionRegistry.CreateDraftTemplateForPreview(source.DraftRevision!, source.Definition!);
    }

    private static PreviewSourceDto TemplateSourceIdentity(TemplatePreviewSourceDto source) => source.Kind == "savedTemplate"
        ? new("template", source.LibraryId + ":" + source.TemplateId, source.LibraryRevision + ":" + source.TemplateRevision)
        : new("template", "draft:" + source.DraftRevision, source.DraftRevision!);

    private async Task<BackendOperationResult<PreviewSessionResponse>> CreateWorkshopTemplatePreviewAsync(string client,
        TemplatePreviewSessionRequest request, StyleTemplate template)
    {
        if (request.MaterialSlot is not null && !Enum.GetValues<TemplateMaterialSlot>()
            .Any(slot => SlotName(slot) == request.MaterialSlot))
            return BackendOperationResult<PreviewSessionResponse>.Rejected(new(BackendErrorCode.InvalidRequest,
                "材质编辑焦点必须是unsungFill、sungFill或stroke。", "materialSlot"));
        bool multiLine = request.SampleId == TemplatePreviewLimits.MultiLineSampleId;
        // Targets are obtained before media preparation; invalid choices never generate media or create sessions.
        TemplateSampleTarget[] targets = TemplateSampleScene.Targets(TemplateSampleScene.WorkshopScene(multiLine, null), template);
        TemplateSampleTarget? target = request.TargetKey is null ? targets.FirstOrDefault()
            : targets.FirstOrDefault(candidate => candidate.Key == request.TargetKey);
        if (target is null)
            return BackendOperationResult<PreviewSessionResponse>.Rejected(new(BackendErrorCode.CommandRejected,
                "当前模板在所选试样中没有匹配的合法目标。", "targetKey", FailureCode: "TemplateSampleTargetUnavailable"));

        await using var media = new TemplateSampleLease(Path.Combine(Path.GetTempPath(), "makokara-template-samples"));
        try
        {
            TemplateSampleResources resources = await media.LoadAsync().ConfigureAwait(false);
            // Only the service-owned generated background enters this grant; caller supplied paths are never accepted.
            await using FileStream background = new(resources.BackgroundPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(background).ConfigureAwait(false));
            await using FileStream audio = new(resources.AudioPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string audioSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(audio).ConfigureAwait(false));
            Project scene = TemplateSampleScene.WorkshopScene(multiLine, resources);
            scene = scene with
            {
                AudioMedia = new(audioSha256, audio.Length, Path.GetFileName(resources.AudioPath)),
                Background = scene.Background with { Media = new(sha256, background.Length, Path.GetFileName(resources.BackgroundPath)) },
            };
            ProjectSnapshot snapshot = ProjectSnapshot.FromProject(TemplateSampleScene.ApplyWorkshop(scene, template, target, null));
            PreviewSourceDto source = TemplateSourceIdentity(request.Source) with
            { TemplateSample = new(request.SampleId, target.Key, request.MaterialSlot) };
            BackendPreviewPinnedInput sample = new(source, snapshot, SelectFactory(_factory!, request.RenderBackend!))
                { RenderBackend = request.RenderBackend! };
            // 工坊试样与作者试样同样经宿主分析固定背景；透明预览固定为透明无媒体输入。
            BackendProjectRuntimeInputLease runtimeInput = BackendProjectRuntimeInputLease.CreateOwnedTemplateSample(snapshot, media.DirectoryPath);
            runtimeInput.MediaFeatures = _projects.MediaFeatures;
            BackendPreviewInputCaptureResult captured = await CompleteCaptureAsync(new(sample, null, RuntimeInput: runtimeInput), null,
                request.AlphaMode == PreviewAlphaMode.Straight).ConfigureAwait(false);
            if (captured.Error is not null) return BackendOperationResult<PreviewSessionResponse>.Rejected(captured.Error);
            BackendPreviewPinnedInput input = captured.Input!;
            PreviewSessionRequest previewRequest = new(source, request.Size, request.AlphaMode,
                Purpose: "template-sample", PreviewKind: "template", Committable: false, RenderBackend: request.RenderBackend);
            BackendPreviewSessionResult result;
            try { result = _previews.Create(client, previewRequest, input); }
            catch { input.RetentionLease?.Dispose(); throw; }
            if (!result.Accepted) input.RetentionLease?.Dispose();
            return result.Accepted ? BackendOperationResult<PreviewSessionResponse>.Success(result.Status!.Session)
                : BackendOperationResult<PreviewSessionResponse>.Rejected(result.Error!);
        }
        catch (SnapshotContractException error)
        { return BackendOperationResult<PreviewSessionResponse>.Rejected(new(BackendErrorCode.CommandNotSupported,
            "工坊试样未通过固定运行输入校验。", "source", FailureCode: error.Code)); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        { return BackendOperationResult<PreviewSessionResponse>.Rejected(new(BackendErrorCode.CommandRejected,
            "工坊试样无法准备或应用。", "source", FailureCode: "TemplateSamplePreparationFailed")); }
    }

    private static string SlotName(TemplateMaterialSlot slot) => slot switch
    {
        TemplateMaterialSlot.UnsungFill => "unsungFill", TemplateMaterialSlot.SungFill => "sungFill", TemplateMaterialSlot.Stroke => "stroke",
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    private static string BindingName(ProjectEffectBindingKind kind) => kind switch
    {
        ProjectEffectBindingKind.Project => "project", ProjectEffectBindingKind.Branch => "branch",
        ProjectEffectBindingKind.Lines => "lines", ProjectEffectBindingKind.Tokens => "tokens",
        ProjectEffectBindingKind.Clips => "clips", ProjectEffectBindingKind.Node => "node",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
