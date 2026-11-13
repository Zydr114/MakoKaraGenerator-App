using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Backend.Hosting;

internal sealed record RetainedPreparedScriptInput(WorkerProcessProxy Owner, string OpenKind, ScriptPreparedInputLease Input,
    EffectPackageProfile[] RequiredPackageProfiles, bool GraphPixelChecks = false);

public sealed partial class WorkerProcessProxy
{
    private async Task<StaticWorkerSessionHandle> OpenPreparedScriptSessionAsync(string kind, Guid id,
        ReadOnlyMemory<byte> source, TimeSpan timeout, PackageRenderWorkerCapabilities capabilities, CancellationToken token, bool image = false,
        bool graphPixelChecks = false)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        ScriptPreparedInputLease prepared;
        EffectPackageProfile[] requiredProfiles = [];
        try
        {
            prepared = await PrepareScriptInputAsync(source, deadline.Token, image: image, captureProfiles: profiles =>
            {
                WorkerPackageProfileAdmission.RequireInput(capabilities, profiles);
                requiredProfiles = profiles;
            }).ConfigureAwait(false);
        }
        catch (SnapshotContractException failure)
        {
            var translated = new WorkerProtocolException(failure.Message, failure.Code);
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { translated.Data["WorkerCleanupFailure"] = cleanup; }
            throw translated;
        }
        try
        {
            byte[] payload = PackageRenderWorkerContract.EncodeOpen(id, prepared.Envelope);
            string sourceHash = prepared.SourceSha256;
            WorkerMessage request = new(kind, Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(payload), payload,
                sessionId: id.ToString("N"), sourceSha256: sourceHash, frameSequence: 0) { GraphPixelChecks = graphPixelChecks ? true : null };
            await InvokeTimedMessageAsync(request, PackageRenderWorkerContract.SessionResultKind, timeout,
                ValidateEmptyPackageSessionResult, deadline.Token, requirePackageRuntimeGuards: true).ConfigureAwait(false);
            RetainPreparedScriptInput(prepared);
            return new(id, sourceHash) { PreparedInput = new(this, kind, prepared, requiredProfiles, graphPixelChecks) };
        }
        catch (Exception primary)
        {
            if (_process is null)
            {
                try { prepared.Dispose(); }
                catch (Exception cleanup) { RetainPreparedScriptInput(prepared); primary.Data["PreparedInputCleanupFailure"] = cleanup; }
            }
            else RetainPreparedScriptInput(prepared);
            throw;
        }
    }

    internal async Task<StaticWorkerSessionHandle> OpenPreparedSessionAsync(RetainedPreparedScriptInput retained,
        TimeSpan timeout, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(retained);
        lock (_preparedScriptInputs)
            if (!ReferenceEquals(retained.Owner, this) || !_preparedScriptInputs.Contains(retained.Input))
                throw new InvalidOperationException("固定输入已释放或不属于此代理。");
        var capabilities = retained.OpenKind == ProjectRuntimeInputWorkerContract.OpenKind
            ? await GetRuntimeInputCapabilitiesAsync(timeout, token).ConfigureAwait(false)
            : retained.OpenKind == ProjectImagePreviewWorkerContract.OpenKind
                ? (await GetImagePreviewCapabilitiesAsync(timeout, token).ConfigureAwait(false)).PackageCapabilities
                : await GetPackageCapabilitiesAsync(timeout, token).ConfigureAwait(false);
        WorkerPackageProfileAdmission.RequireInput(capabilities, retained.RequiredPackageProfiles);
        Guid id = Guid.NewGuid(); byte[] payload = PackageRenderWorkerContract.EncodeOpen(id, retained.Input.Envelope);
        string sourceHash = retained.Input.SourceSha256;
        WorkerMessage request = new(retained.OpenKind, Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(payload), payload,
            sessionId: id.ToString("N"), sourceSha256: sourceHash, frameSequence: 0) { GraphPixelChecks = retained.GraphPixelChecks ? true : null };
        await InvokeTimedMessageAsync(request, PackageRenderWorkerContract.SessionResultKind, timeout,
            ValidateEmptyPackageSessionResult, token, requirePackageRuntimeGuards: true).ConfigureAwait(false);
        return new(id, sourceHash) { PreparedInput = retained };
    }

    private static readonly Lazy<ScriptCompiler> FallbackScriptCompiler = new(() => new(
        Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"),
        Path.Combine(Path.GetTempPath(), "makokara-host-script-artifacts-" + Environment.ProcessId)));
    private readonly List<ScriptPreparedInputLease> _preparedScriptInputs = [];

    private sealed class PreparedScriptCall(WorkerProcessProxy owner, ScriptPreparedInputLease input, EffectPackageProfile[] requiredProfiles) : IDisposable
    {
        private bool _completed;
        private bool _disposed;
        public byte[] Envelope => input.Envelope;
        public bool HasPackages => requiredProfiles.Length != 0;
        public void RequirePackageProfiles(PackageRenderWorkerCapabilities capabilities)
            => WorkerPackageProfileAdmission.RequireInput(capabilities, requiredProfiles);
        public void Complete() => _completed = true;
        public void Dispose()
        {
            if (_disposed) return;
            if (!_completed && owner._process is not null) owner.RetainPreparedScriptInput(input);
            else
            {
                try { input.Dispose(); }
                catch { owner.RetainPreparedScriptInput(input); throw; }
            }
            _disposed = true;
        }
    }

    private async Task<PreparedScriptCall> PrepareScriptCallAsync(ReadOnlyMemory<byte> source, CancellationToken token,
        ProjectSnapshot? snapshot = null, bool image = false)
    {
        EffectPackageProfile[] profiles = [];
        var input = await PrepareScriptInputAsync(source, token, snapshot, image, values => profiles = values).ConfigureAwait(false);
        return new(this, input, profiles);
    }

    private readonly List<CancellationTokenSource> _scriptPreparations = [];

    private async Task<ScriptPreparedInputLease> PrepareScriptInputAsync(ReadOnlyMemory<byte> source,
        CancellationToken token, ProjectSnapshot? fixedSnapshot = null, bool image = false, Action<EffectPackageProfile[]>? captureProfiles = null)
    {
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _scriptPreparations.Add(preparation);
        }
        try { return await PrepareScriptInputCoreAsync(source, fixedSnapshot, image, captureProfiles, preparation.Token).ConfigureAwait(false); }
        finally { lock (_lifecycleGate) _scriptPreparations.Remove(preparation); }
    }

    private async Task<ScriptPreparedInputLease> PrepareScriptInputCoreAsync(ReadOnlyMemory<byte> source,
        ProjectSnapshot? fixedSnapshot, bool image, Action<EffectPackageProfile[]>? captureProfiles, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ProjectSnapshot snapshot;
        if (fixedSnapshot is not null) snapshot = fixedSnapshot;
        else if (image) snapshot = ProjectImagePreviewSnapshotCodec.Decode(source).Snapshot;
        else if (ProjectRuntimeInputCodec.IsRuntimeInput(source.Span))
        {
            Dictionary<(string, string), EffectPackageContents> packages = [];
            EffectPackageContentUsage usage = new();
            foreach (var grant in ProjectRuntimeInputCodec.ReadPackageGrants(source))
            {
                EnsureScriptGrantNoLinks(grant.DirectoryPath);
                var contents = await EffectPackageStorage.ReadSupportedDirectoryWithUsageAsync(grant.DirectoryPath,
                    null, usage, token).ConfigureAwait(false);
                if (contents.PackageId != grant.PackageId || contents.Revision != grant.Revision)
                    throw new WorkerProtocolException("固定包目录身份已改变。", "ProjectRuntimeSourceChanged");
                packages.Add((contents.PackageId, contents.Revision), contents);
            }
            snapshot = ProjectRuntimeInputCodec.Decode(source, packages).Snapshot;
        }
        else snapshot = ProjectSnapshotSourceProfile.Decode(source);
        captureProfiles?.Invoke([.. snapshot.Effects.Packages.Select(package => package.Profile).Distinct()]);

        var references = SourceMaterialCatalog.CollectForRuntime(snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation, snapshot.PresetApplications);
        List<EffectPackageDefinition> definitions = [];
        foreach (var key in snapshot.Effects.Instances.Where(instance => instance.Enabled).Select(instance => (instance.PackageId, instance.Revision))
            .Concat(references.Select(reference => (reference.Material.PackageId, reference.Material.Revision))).Distinct())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var definition = EffectPackageDefinition.Read(snapshot.Effects.GetPackage(key.PackageId, key.Revision), token);
                if (!definition.Controllers.IsEmpty || definition.Contents.Files.Any(file => file.Role == "csharp")) definitions.Add(definition);
            }
            // Keep existing Worker resource/source-material failure attribution; the original source remains intact.
            catch (EffectPackageException) { }
        }
        IScriptCompilationService? compiler = definitions.Count == 0 ? null : _options.ScriptCompilationService ?? FallbackScriptCompiler.Value;
        ScriptCompilationIdentity identity = compiler?.Identity ?? _options.ScriptCompilationService?.Identity
            ?? ScriptCompilationIdentity.ForDeployment(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        List<(ScriptCompilationInput, ScriptCompilationResult)> compilations = [];
        foreach (var definition in definitions)
        {
            token.ThrowIfCancellationRequested();
            ScriptCompilationInput input;
            try { input = ScriptCompilationInput.Create(EffectPackageSources.Read(definition.Contents), definition.Controllers.Select(controller => controller.EntryType), identity); }
            catch (EffectPackageException) { continue; }
            var result = await compiler!.CompileAsync(input.Sources, input.EntryTypes, token).ConfigureAwait(false);
            compilations.Add((input, result));
        }
        return ScriptPreparedInputCodec.Prepare(source, Path.Combine(Path.GetTempPath(), "makokara-host-script-inputs-" + Environment.ProcessId),
            identity, compilations, token);
    }

    private void RetainPreparedScriptInput(ScriptPreparedInputLease input)
    { lock (_preparedScriptInputs) if (!_preparedScriptInputs.Contains(input)) _preparedScriptInputs.Add(input); }

    private void ReleasePreparedScriptInputs()
    {
        lock (_preparedScriptInputs)
        {
            List<Exception> failures = [];
            for (int index = _preparedScriptInputs.Count - 1; index >= 0; index--)
            {
                try { _preparedScriptInputs[index].Dispose(); _preparedScriptInputs.RemoveAt(index); }
                catch (Exception failure) { failures.Add(failure); }
            }
            if (failures.Count != 0) throw new AggregateException("固定脚本输入清理失败；保留所有权以重试。", failures);
        }
    }

    private static void EnsureScriptGrantNoLinks(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
            if (!directory.Exists || directory.LinkTarget is not null)
                throw new WorkerProtocolException("固定包目录不可用或含符号链接。", "ProjectRuntimeSourceUnavailable");
    }
}
