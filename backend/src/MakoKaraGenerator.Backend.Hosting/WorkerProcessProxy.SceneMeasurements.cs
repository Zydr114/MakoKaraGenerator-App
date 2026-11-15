using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class WorkerProcessProxy
{
    /// <summary>同一新建Worker进程内先验证执行能力与引擎身份，再进行有界测量。</summary>
    internal Task<WorkerSceneMeasurementOutput> MeasureSceneAsync(WorkerSceneMeasurementInput input, byte[] source,
        CancellationToken cancellationToken) =>
        MeasureSceneAsync(input, source, ProjectRuntimeInputCodec.Decode(source).Snapshot, cancellationToken);

    internal async Task<WorkerSceneMeasurementOutput> MeasureSceneAsync(WorkerSceneMeasurementInput input, byte[] source,
        ProjectSnapshot snapshot, CancellationToken cancellationToken)
    {
        // The Host already pinned this snapshot under the project Writer. Validate
        // its encoded identity without opening Worker-owned media/font grants.
        string sourceProfile = ProjectRuntimeInputCodec.ValidateEnvelope(source);
        ProjectRuntimeInputCodec.ValidateSnapshot(source, snapshot);
        bool runtimeInput = ProjectRuntimeInputCodec.IsRuntimeInput(source);
        if (runtimeInput) await GetRuntimeInputCapabilitiesAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        byte[] capability = WorkerSceneMeasurementContract.CapabilitiesRequest();
        var response = await InvokeMessageAsync(new(WorkerSceneMeasurementContract.CapabilitiesKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(capability), capability), cancellationToken, requirePackageRuntimeGuards: runtimeInput).ConfigureAwait(false);
        var capabilities = DecodeMeasurement<WorkerSceneMeasurementCapabilities>(response.Payload);
        if (capabilities is null || capabilities.Profile != SceneMeasurementContract.Profile || capabilities.EngineIdentity != StaticProjectSnapshotCodec.EngineIdentity
            || capabilities.SourceProfiles is null || !new[] {
                StaticProjectSnapshotCodec.Profile, StaticProjectSnapshotCodec.AnalysisProfile, StaticProjectSnapshotCodec.PresetsProfile,
                PackageProjectSnapshotCodec.Profile, PackagePresetProjectSnapshotCodec.Profile }.All(profile => capabilities.SourceProfiles.Contains(profile, StringComparer.Ordinal))
            || capabilities.SourceProfiles.Any(profile => profile is not (StaticProjectSnapshotCodec.Profile or StaticProjectSnapshotCodec.AnalysisProfile
                or StaticProjectSnapshotCodec.PresetsProfile or PackageProjectSnapshotCodec.Profile or PackagePresetProjectSnapshotCodec.Profile or ProjectRuntimeInputWorkerContract.Profile))
            || runtimeInput && !capabilities.SourceProfiles.Contains(ProjectRuntimeInputWorkerContract.Profile, StringComparer.Ordinal))
            throw new WorkerProtocolException("Worker没有匹配的同帧测量能力。", "SceneMeasurementUnavailable");
        // This proxy belongs to one operation and is never restarted between capability and invocation.
        var process = _process;
        byte[] originalPayload = WorkerSceneMeasurementContract.Encode(input, source);
        using var prepared = await PrepareScriptCallAsync(originalPayload, cancellationToken, snapshot).ConfigureAwait(false);
        byte[] payload = prepared.Envelope;
        response = await InvokeMessageAsync(new(WorkerSceneMeasurementContract.RequestKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload), cancellationToken, validateResponse: _ => {
                if (!ReferenceEquals(process, _process)) throw new WorkerProtocolException("测量Worker代次改变。", "SceneMeasurementUnavailable");
            }, expectedProcess: process, requirePackageRuntimeGuards: runtimeInput).ConfigureAwait(false);
        prepared.Complete();
        if (response.Payload.Length is <= 0 or > SceneMeasurementContract.MaximumResponseBytes)
            throw new WorkerProtocolException("完整测量回执大小无效。", "SceneMeasurementBudgetExceeded");
        var result = DecodeMeasurement<WorkerSceneMeasurementOutput>(response.Payload)
            ?? throw new WorkerProtocolException("测量回执为空。");
        var data = result.Measurement;
        if (data is null || data.SourceSha256 != input.SourceSha256 || data.SampleInputFingerprint != input.SampleInputFingerprint
            || data.Width != input.Request.Width || data.Height != input.Request.Height || data.AlphaMode != input.Request.AlphaMode
            || data.Mode != input.Request.Mode || data.Elements is null || data.Elements.Length > SceneMeasurementContract.MaximumElements
            || data.Elements.Any(element => element is null || string.IsNullOrWhiteSpace(element.Id) || element.WorldCorners is null)
            || data.Elements.Sum(element => (long)element.WorldCorners.Length) > SceneMeasurementContract.MaximumCorners
            || data.Png is null || data.PngSha256 != WorkerProtocol.ComputeSha256(data.Png)
            || !BackendFallbackDiagnosticsContract.IsValid(data.FallbackDiagnostics))
            throw new WorkerProtocolException("测量回执不属于当前固定输入。");
        var renderSnapshot = input.Request.Mode == "layout" ? MakoKaraGenerator.Rendering.LayoutReferenceScene.CreateSnapshot(snapshot) : snapshot;
        var definitions = MakoKaraGenerator.Rendering.SceneDefinitionBuilder.Build(renderSnapshot).Elements.ToDictionary(element => element.Id, StringComparer.Ordinal);
        var originalLines = snapshot.Lyrics.AllLines.Select(line => line.Id).ToHashSet(StringComparer.Ordinal);
        var representativeLines = renderSnapshot.Lyrics.AllLines.Where(line => !originalLines.Contains(line.Id)).Select(line => line.Id).ToHashSet(StringComparer.Ordinal);
        var projected = renderSnapshot.LyricPresentation.WithDefaultPrograms(renderSnapshot.Lyrics);
        var persistentIds = projected.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var slotIds = projected.Nodes.Where(node => node.Layout is not null).SelectMany(node => node.Layout!.SlotIds).ToHashSet(StringComparer.Ordinal);
        string ExpectedKind(MakoKaraGenerator.Rendering.SceneElementDefinition definition) => persistentIds.Contains(definition.Id)
            ? definition.Content is MakoKaraGenerator.Core.Projects.TextMarkContent ? "mark" : slotIds.Contains(definition.Id) ? "slot" : "container"
            : definition.SourceBinding.RubyId is null ? "line" : "ruby";
        string expectedSampling = input.Request.Mode == "layout" ? SceneMeasurementContract.LayoutProfile
            : input.EditingLineId is null ? SceneMeasurementContract.TimedProfile : "line-reference-v1";
        string? expectedTime = input.Request.TimeUs;
        if (input.EditingLineId is { } selected && input.EditingCueId is { } cueId)
        {
            var cue = snapshot.LyricPresentation.Programs.SelectMany(program => program.LeadIns).Single(cue => cue.Id == cueId);
            var line = snapshot.Lyrics.AllLines.Single(line => line.Id == selected);
            expectedTime = MakoKaraGenerator.Core.Projects.LeadInTiming.Range(line, cue.DurationUs)?.StartUs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        static bool Point(ProjectGeometryPointDto? point) => point is not null && double.IsFinite(point.X) && double.IsFinite(point.Y);
        static bool Matrix(ProjectGeometryMatrixDto? matrix) => matrix is not null
            && new[] { matrix.A,matrix.B,matrix.C,matrix.D,matrix.Tx,matrix.Ty }.All(double.IsFinite)
            && new MakoKaraGenerator.Core.Projects.SceneMatrix(matrix.A,matrix.B,matrix.C,matrix.D,matrix.Tx,matrix.Ty).TryInverse(out _);
        static bool Reference(ProjectGeometryReferenceDto? reference) => reference is null || double.IsFinite(reference.Width) && reference.Width > 0
            && double.IsFinite(reference.Height) && reference.Height > 0 && Matrix(reference.WorldMatrix) && Point(reference.Pivot)
            && reference.WorldCorners is { Length: 4 } && reference.WorldCorners.All(Point);
        if (data.SourceProfile != sourceProfile || data.SamplingProfile != expectedSampling
            || data.TimeUs != expectedTime || data.CanvasWidth != snapshot.Composition.CanvasWidth || data.CanvasHeight != snapshot.Composition.CanvasHeight
            || data.Elements.Select(element => element.Id).Distinct(StringComparer.Ordinal).Count() != data.Elements.Length
            || !definitions.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(data.Elements.Select(element => element.Id))
            || data.Elements.Any(element => !definitions.TryGetValue(element.Id, out var definition)
                || element.BranchId != definition.SourceBinding.BranchId || element.LineId != definition.SourceBinding.LineId
                || element.TokenId != definition.SourceBinding.TokenId || element.RubyId != definition.SourceBinding.RubyId
                || element.ParentId is not null && !definitions.ContainsKey(element.ParentId)
                || element.Kind != ExpectedKind(definition)
                || element.Representative != (input.Request.Mode == "layout" && definition.SourceBinding.LineId is { } sourceLine && representativeLines.Contains(sourceLine))
                || element.SlotId is not null && (!slotIds.Contains(element.SlotId) || element.Kind != "line" || element.ParentId != element.SlotId)
                || element.GeometryStatus is not ("notVisible" or "emptyReference" or "measuredInk")
                || !double.IsFinite(element.Opacity) || element.Opacity is < 0 or > 1
                || element.WorldMatrix is not null && !Matrix(element.WorldMatrix) || element.Pivot is not null && !Point(element.Pivot)
                || element.WorldCorners.Any(point => !Point(point)) || !Reference(element.FixedReference) || !Reference(element.EditReference)
                || element.Visible && (element.WorldMatrix is null || element.Pivot is null)
                || element.GeometryStatus == "measuredInk" && element.WorldCorners.Length < 4)
            || !SceneMeasurementContract.IsValidPng(data.Png, data.Width, data.Height))
            throw new WorkerProtocolException("测量回执几何、身份或PNG内容无效。");
        if (result.Attachment is { } attachment && (attachment.ContainerId != input.AttachmentContainerId || attachment.LineId != input.EditingLineId
            || attachment.Previous is null || attachment.FixedParent is null || attachment.TargetText is null || attachment.TargetAboveStart is null
            || !Reference(attachment.Previous) || !Reference(attachment.FixedParent) || !Reference(attachment.TargetText) || !Reference(attachment.TargetAboveStart)))
            throw new WorkerProtocolException("附着测量回执不属于当前输入。");
        return result;
    }
    private static T? DecodeMeasurement<T>(byte[] payload)
    {
        try { return JsonSerializer.Deserialize<T>(payload, SceneMeasurementContract.JsonOptions); }
        catch (JsonException error) { throw new WorkerProtocolException("测量Worker返回非法JSON：" + error.Message, "SceneMeasurementProtocolInvalid"); }
    }

}
