using System.Buffers.Binary;
using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 真HTTP真实Worker替换坏源无音轨原子失败且双音轨只准备首轨()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new();
        (byte[] firstBytes, LoadedWaveform firstWaveform) = BasicWave(8000, 8000, 31);
        (byte[] secondBytes, _) = BasicWave(16000, 32000, 47);
        string firstPath = directory.FilePath("first.wav"), secondPath = directory.FilePath("second.wav");
        await File.WriteAllBytesAsync(firstPath, firstBytes); await File.WriteAllBytesAsync(secondPath, secondBytes);
        string dualPath = directory.FilePath("dual.mov"), videoPath = directory.FilePath("video.mov");
        await BasicFfmpeg(["-v", "error", "-i", firstPath, "-i", secondPath, "-map", "0:a:0", "-map", "1:a:0", "-c:a", "pcm_s16le",
            "-disposition:a:0", "0", "-disposition:a:1", "default", dualPath]);
        await BasicFfmpeg(["-v", "error", "-f", "lavfi", "-i", "color=c=black:s=16x16:r=1:d=1", "-an", "-c:v", "mpeg4", videoPath]);
        await using Fixture f = await Fixture.Create(analysisBytes: firstBytes, waveformOverride: firstWaveform);
        f.Http.Timeout = TimeSpan.FromSeconds(30);
        WorkerBackendAudioPreparationExecutor executor = new(new(FindWorkerApphost()), TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe);
        f.Projects.ConfigureAudioPreparation(executor, await executor.ProbeAsync());
        string route = "/api/v1/project-sessions/" + f.Project.ProjectSessionId;
        string original = f.Project.AudioAssetId;
        StoredAsset corrupt = await BasicUpload(f, "bad.wav", [1, 2, 3, 4]);
        using (HttpResponseMessage failed = await f.Http.PostAsJsonAsync(route + "/commands", BasicMediaRequest(f, "0", new ReplaceProjectAudioAssetCommandDto(corrupt.AssetId)), Json))
            await Error(failed, HttpStatusCode.UnprocessableEntity, BackendErrorCode.AudioPreparationFailed);
        StoredAsset noAudio = await BasicUpload(f, "video.mov", await File.ReadAllBytesAsync(videoPath));
        using (HttpResponseMessage failed = await f.Http.PostAsJsonAsync(route + "/commands", BasicMediaRequest(f, "0", new ReplaceProjectAudioAssetCommandDto(noAudio.AssetId)), Json))
            await Error(failed, HttpStatusCode.UnprocessableEntity, BackendErrorCode.NoAudioStream);
        Assert.Equal(original, f.Project.AudioAssetId);
        Assert.Equal("0", (await f.Http.GetFromJsonAsync<ProjectHistoryResponse>(route + "/history", Json))!.ProjectRevision);
        StoredAsset dual = await BasicUpload(f, "dual.mov", await File.ReadAllBytesAsync(dualPath));
        Assert.True((await BasicMediaCommand(f, "0", new ReplaceProjectAudioAssetCommandDto(dual.AssetId))).Changed);
        ProjectWaveformResponse metadata = await AssertBasicWaveform(f, route, firstWaveform);
        Assert.Equal(8000, metadata.SampleRate); Assert.Equal(1, metadata.SourceChannels); Assert.Equal("1000000", metadata.DurationUs);
        Assert.Equal(dual.Sha256, metadata.SourceSha256);
    }

    [Fact]
    public async Task HTTP断流只结束等待同回执继续准备且重放只提交一次音频历史()
    {
        await using Fixture f = await Fixture.Create(); PreparationExecutor executor = new() { Block = true };
        f.Projects.ConfigureAudioPreparation(executor, new(true, true));
        StoredAsset audio = await BasicUpload(f, "new.wav", [1, 2, 4]);
        string route = "/api/v1/project-sessions/" + f.Project.ProjectSessionId;
        ProjectCommandRequest input = BasicMediaRequest(f, "0", new ReplaceProjectAudioAssetCommandDto(audio.AssetId));
        using CancellationTokenSource caller = new();
        Task<HttpResponseMessage> pending = f.Http.PostAsJsonAsync(route + "/commands", input, Json, caller.Token);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8)); caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.False(executor.Token.IsCancellationRequested);
        executor.Release.TrySetResult();
        using HttpResponseMessage replay = await f.Http.PostAsJsonAsync(route + "/commands", input, Json);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        ProjectCommandResponse accepted = (await replay.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
        Assert.True(accepted.Changed); Assert.Equal("1", accepted.ProjectRevision); Assert.Equal(1, executor.Calls);
        using HttpResponseMessage repeated = await f.Http.PostAsJsonAsync(route + "/commands", input, Json);
        Assert.Equal(JsonSerializer.Serialize(accepted, Json), JsonSerializer.Serialize(await repeated.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json), Json));
        Assert.Equal(1, (await f.Http.GetFromJsonAsync<ProjectHistoryResponse>(route + "/history", Json))!.UndoCount);
    }

    [Fact]
    public async Task 真HTTP真实Worker替换清除恢复音频字节完整波形分析及保存重开和租约释放()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        (byte[] oldBytes, LoadedWaveform oldWaveform) = BasicWave(8000, 8000, 3);
        (byte[] newBytes, LoadedWaveform newWaveform) = BasicWave(16000, 32000, 11);
        WaveformCache cache = new();
        await using Fixture f = await Fixture.Create(analysisFixture: true, analysisBytes: oldBytes,
            waveformOverride: oldWaveform, waveformCache: cache);
        f.Http.Timeout = TimeSpan.FromSeconds(30);
        WorkerBackendAudioPreparationExecutor executor = new(new(FindWorkerApphost()), TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe);
        BackendAudioPreparationCapabilities capabilities = await executor.ProbeAsync();
        Assert.True(capabilities.GuardAvailable, capabilities.FailureCode); Assert.True(capabilities.ToolStartupVerified, capabilities.FailureCode);
        f.Projects.ConfigureAudioPreparation(executor, capabilities);
        string route = "/api/v1/project-sessions/" + f.Project.ProjectSessionId;
        string oldId = f.Project.AudioAssetId;
        ProjectWaveformResponse oldMetadata = (await f.Http.GetFromJsonAsync<ProjectWaveformResponse>(route + "/waveform", Json))!;
        JobResponse analysis = await CompleteAnalysis(f);
        Assert.True((await BasicMediaCommand(f, "0", new ProjectCommandDto("audio.analysis.apply", AnalysisJobId: analysis.JobId))).Changed);
        PreviewSessionResponse oldPreview = (await f.Service.CreateAsync(f.Client.ClientId,
            f.CreateRequest() with { Source = f.Source with { SourceRevision = "1" } })).Response!;
        Assert.True((await BasicMediaCommand(f, "1", new RenameProjectCommandDto("  renamed  "))).Changed);
        StoredAsset replacement = await BasicUpload(f, "new.wav", newBytes);
        ProjectCommandResponse replaced = await BasicMediaCommand(f, "2", new ReplaceProjectAudioAssetCommandDto(replacement.AssetId));
        Assert.Equal("3", replaced.ProjectRevision); Assert.True(replaced.Changed);
        ProjectSessionSummaryResponse summary = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(route, Json))!;
        Assert.Equal(replacement.AssetId, summary.AudioAssetId); Assert.Equal(2000000, summary.AudioDurationUs); Assert.Null(summary.AudioAnalysis);
        ProjectSettingsResponse settings = (await f.Http.GetFromJsonAsync<ProjectSettingsResponse>(route + "/settings?expectedRevision=3", Json))!;
        Assert.Equal("3000000", settings.OutputDurationUs); Assert.Equal("renamed", settings.Name);
        Assert.Equal(oldBytes, await f.Http.GetByteArrayAsync("/api/v1/assets/" + oldId + "/content"));
        Assert.Equal(newBytes, await f.Http.GetByteArrayAsync("/api/v1/assets/" + replacement.AssetId + "/content"));
        ProjectWaveformResponse newMetadata = await AssertBasicWaveform(f, route, newWaveform);
        Assert.NotEqual(oldMetadata.WaveformId, newMetadata.WaveformId);
        ProjectCommandResponse undone = await BasicMediaCommand(f, "3", new ProjectCommandDto("history.undo"));
        Assert.True(undone.Changed); Assert.Contains("audio", undone.ChangeSet.Areas); Assert.Contains("waveform", undone.ChangeSet.Areas);
        Assert.Contains("audioAnalysis", undone.ChangeSet.Areas); Assert.True(undone.ChangeSet.RequiresFullRefresh);
        BackendEventResponse restoredEvent = Assert.Single(f.Projects.ReadEvents(f.Client.ClientId, replaced.EventCursor).Events,
            item => item.Type == "project.changed");
        Assert.Equal(undone.ProjectRevision, restoredEvent.Summary!.ProjectRevision);
        Assert.Equal(oldId, restoredEvent.Summary.AudioAssetId); Assert.Equal(undone.ChangeSet.Areas, restoredEvent.ChangeSet!.Areas);
        summary = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(route, Json))!;
        Assert.Equal(oldId, summary.AudioAssetId); Assert.Equal(1000000, summary.AudioDurationUs);
        Assert.Equal(analysis.AnalysisResult!.ContentHash, summary.AudioAnalysis!.ContentHash);
        Assert.Equal(oldMetadata.WaveformId, (await AssertBasicWaveform(f, route, oldWaveform)).WaveformId);
        ProjectCommandResponse noOp = await BasicMediaCommand(f, "4", new ReplaceProjectAudioAssetCommandDto(oldId));
        Assert.False(noOp.Changed); Assert.Equal("4", noOp.ProjectRevision); Assert.True(noOp.CanRedo);
        Assert.Empty(noOp.ChangeSet.Areas); Assert.Empty(f.Projects.ReadEvents(f.Client.ClientId, undone.EventCursor).Events);
        ProjectCommandResponse redone = await BasicMediaCommand(f, "4", new ProjectCommandDto("history.redo"));
        Assert.True(redone.Changed); Assert.Contains("audio", redone.ChangeSet.Areas); Assert.Contains("waveform", redone.ChangeSet.Areas);
        Assert.Equal(2000000, (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(route, Json))!.AudioDurationUs);
        Assert.Equal(newMetadata.WaveformId, (await AssertBasicWaveform(f, route, newWaveform)).WaveformId);
        Assert.True((await BasicMediaCommand(f, "5", new ClearProjectAudioCommandDto())).Changed);
        summary = (await f.Http.GetFromJsonAsync<ProjectSessionSummaryResponse>(route, Json))!;
        Assert.Equal(string.Empty, summary.AudioAssetId); Assert.Equal(0, summary.AudioDurationUs); Assert.Null(summary.AudioAnalysis);
        Assert.Equal("noAudio", (await f.Http.GetFromJsonAsync<ProjectWaveformResponse>(route + "/waveform", Json))!.Reason);
        Assert.False((await BasicMediaCommand(f, "6", new ClearProjectAudioCommandDto())).Changed);
        Assert.True((await BasicMediaCommand(f, "6", new ProjectCommandDto("history.undo"))).Changed);
        ProjectResourcesResponse resources = (await f.Http.GetFromJsonAsync<ProjectResourcesResponse>(route + "/resources?expectedRevision=7", Json))!;
        ProjectResourceDto audio = Assert.Single(resources.Resources);
        Assert.Equal(replacement.Sha256, audio.Sha256); Assert.Equal(newBytes.Length.ToString(CultureInfo.InvariantCulture), audio.SizeBytes);
        Assert.Equal("available", audio.Status); Assert.Equal("prepared", audio.PreparationStatus); Assert.Equal("available", audio.WaveformStatus);
        Assert.Equal(newBytes, await f.Http.GetByteArrayAsync(audio.ContentUrl));
        Assert.DoesNotContain(replacement.StoragePath, JsonSerializer.Serialize(resources, Json), StringComparison.Ordinal);
        using HttpResponseMessage stale = await f.Http.GetAsync(route + "/resources?expectedRevision=6");
        await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        FileHandleResponse handle = (await f.Projects.CreateFileHandleAsync(f.Client.ClientId,
            new("preview-test-root", "basic-audio", Metadata: f.Metadata()))).Response!;
        using HttpResponseMessage saved = await f.Http.PostAsJsonAsync(route + "/save-jobs", new SaveProjectRequest("7", handle.HandleId, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Accepted, saved.StatusCode); await BasicMediaTerminal(f, (await saved.Content.ReadFromJsonAsync<JobResponse>(Json))!);
        using HttpResponseMessage closed = await f.Http.PostAsJsonAsync(route + "/close", new CloseProjectRequest("7", Metadata: f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Accepted, closed.StatusCode); await BasicMediaTerminal(f, (await closed.Content.ReadFromJsonAsync<JobResponse>(Json))!);
        Assert.Equal(0, cache.RetainedBytes);
        f.Clock.Advance(10); f.Resources.CollectExpired();
        Assert.True(f.Resources.TryGetAuthorized(f.Client.ClientId, oldId, out _)); // fixed old preview remains protected
        Assert.True((await f.Service.CloseAsync(f.Client.ClientId, oldPreview.PreviewId,
            new(oldPreview.PreviewGeneration, "1", f.Metadata()))).Accepted);
        f.Clock.Advance(10); f.Resources.CollectExpired();
        Assert.False(f.Resources.TryGetAuthorized(f.Client.ClientId, oldId, out _));
        Assert.False(f.Resources.TryGetAuthorized(f.Client.ClientId, replacement.AssetId, out _));
        using HttpResponseMessage opened = await f.Http.PostAsJsonAsync("/api/v1/project-sessions/open-jobs", new OpenProjectSessionRequest(handle.HandleId, Metadata: f.Metadata()), Json);
        JobResponse reopened = await BasicMediaTerminal(f, (await opened.Content.ReadFromJsonAsync<JobResponse>(Json))!);
        Assert.Equal("renamed", reopened.Result!.Name); Assert.Null(reopened.Result.AudioAnalysis);
        string reopenedRoute = "/api/v1/project-sessions/" + reopened.ProjectSessionId;
        Assert.Equal(newMetadata.WaveformId, (await AssertBasicWaveform(f, reopenedRoute, newWaveform)).WaveformId);
        Assert.Equal(newBytes, await f.Http.GetByteArrayAsync("/api/v1/assets/" + reopened.Result.AudioAssetId + "/content"));
        using HttpResponseMessage cleared = await f.Http.PostAsJsonAsync(reopenedRoute + "/commands", BasicMediaRequest(f, "0", new ClearProjectAudioCommandDto()), Json);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        using HttpResponseMessage saveClear = await f.Http.PostAsJsonAsync(reopenedRoute + "/save-jobs", new SaveProjectRequest("1", Metadata: f.Metadata()), Json);
        await BasicMediaTerminal(f, (await saveClear.Content.ReadFromJsonAsync<JobResponse>(Json))!);
        using HttpResponseMessage closeClear = await f.Http.PostAsJsonAsync(reopenedRoute + "/close", new CloseProjectRequest("1", Metadata: f.Metadata()), Json);
        await BasicMediaTerminal(f, (await closeClear.Content.ReadFromJsonAsync<JobResponse>(Json))!);
        using HttpResponseMessage openClear = await f.Http.PostAsJsonAsync("/api/v1/project-sessions/open-jobs", new OpenProjectSessionRequest(handle.HandleId, Metadata: f.Metadata()), Json);
        JobResponse withoutAudio = await BasicMediaTerminal(f, (await openClear.Content.ReadFromJsonAsync<JobResponse>(Json))!);
        Assert.Equal(string.Empty, withoutAudio.Result!.AudioAssetId); Assert.Equal(0, withoutAudio.Result.AudioDurationUs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HTTP音频准备在Writer外且并发改名或关闭均原子拒绝迟到候选(bool close)
    {
        await using Fixture f = await Fixture.Create(); PreparationExecutor executor = new() { Block = true };
        f.Projects.ConfigureAudioPreparation(executor, new(true, true));
        string route = "/api/v1/project-sessions/" + f.Project.ProjectSessionId;
        string original = f.Project.AudioAssetId;
        StoredAsset audio = await BasicUpload(f, "new.wav", [3, 2, 1]);
        Task<HttpResponseMessage> pending = f.Http.PostAsJsonAsync(route + "/commands", BasicMediaRequest(f, "0", new ReplaceProjectAudioAssetCommandDto(audio.AssetId)), Json);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        if (close)
        {
            using HttpResponseMessage closing = await f.Http.PostAsJsonAsync(route + "/close", new CloseProjectRequest("0", "discard", Metadata: f.Metadata()), Json);
            await BasicMediaTerminal(f, (await closing.Content.ReadFromJsonAsync<JobResponse>(Json))!);
        }
        else Assert.True((await BasicMediaCommand(f, "0", new RenameProjectCommandDto("concurrent"))).Changed);
        executor.Release.TrySetResult();
        using HttpResponseMessage rejected = await pending;
        await Error(rejected, HttpStatusCode.Conflict, close ? BackendErrorCode.SessionClosing : BackendErrorCode.RevisionConflict);
        Assert.Equal(original, f.Project.AudioAssetId);
        if (!close)
        {
            ProjectHistoryResponse history = (await f.Http.GetFromJsonAsync<ProjectHistoryResponse>(route + "/history", Json))!;
            Assert.Equal(1, history.UndoCount); Assert.Equal("1", history.ProjectRevision);
        }
        f.Clock.Advance(10); f.Resources.CollectExpired();
        Assert.False(f.Resources.TryGetAuthorized(f.Client.ClientId, audio.AssetId, out _));
    }

    [Fact]
    public async Task HTTP音频失败跨client严格wire及资源修订分页均不改历史()
    {
        await using Fixture f = await Fixture.Create(); PreparationExecutor executor = new() { NoStream = true };
        f.Projects.ConfigureAudioPreparation(executor, new(true, true));
        string route = "/api/v1/project-sessions/" + f.Project.ProjectSessionId;
        string original = f.Project.AudioAssetId;
        StoredAsset noStream = await BasicUpload(f, "none.wav", [1, 2, 4]);
        using HttpResponseMessage rejected = await PostRawEditing(f, "0", $$"""{"type":"audio.replaceAsset","assetId":"{{noStream.AssetId}}"}""");
        await Error(rejected, HttpStatusCode.UnprocessableEntity, BackendErrorCode.NoAudioStream);
        StoredAsset other = await f.Resources.UploadAsync("other", "other.wav", "audio/wav", new MemoryStream([1, 2, 3]));
        using HttpResponseMessage foreign = await PostRawEditing(f, "0", $$"""{"type":"audio.replaceAsset","assetId":"{{other.AssetId}}"}""");
        await Error(foreign, HttpStatusCode.Unauthorized, BackendErrorCode.ResourceUnauthorized);
        foreach (string command in new[] { "{\"type\":\"audio.clear\",\"assetId\":null}", "{\"type\":\"audio.replaceAsset\",\"assetId\":1}",
            "{\"type\":\"project.rename\",\"name\":\"\"}", "{\"type\":\"project.rename\",\"name\":null}", "{\"type\":\"project.rename\",\"name\":\"a\",\"target\":null}" })
        {
            using HttpResponseMessage invalid = await PostRawEditing(f, "0", command);
            await Error(invalid, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        }
        ProjectResourcesResponse resources = (await f.Http.GetFromJsonAsync<ProjectResourcesResponse>(route + "/resources?expectedRevision=0&limit=1", Json))!;
        Assert.Single(resources.Resources); Assert.Equal(original, resources.Resources[0].AssetId);
        Assert.DoesNotContain(resources.Resources, item => item.AssetId == other.AssetId || item.AssetId == noStream.AssetId);
        foreach (string query in new[] { "", "?expectedRevision=0&expectedRevision=0", "?expectedRevision=0&limit=101", "?expectedRevision=0&offset=2", "?expectedRevision=0&credential=x" })
        { using HttpResponseMessage invalid = await f.Http.GetAsync(route + "/resources" + query); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        Assert.Equal(original, f.Project.AudioAssetId);
        ProjectHistoryResponse history = (await f.Http.GetFromJsonAsync<ProjectHistoryResponse>(route + "/history", Json))!;
        Assert.Equal("0", history.ProjectRevision); Assert.Equal(0, history.UndoCount);
    }

    private static ProjectCommandRequest BasicMediaRequest(Fixture f, string revision, ProjectCommandDto command)
    {
        RequestMetadataDto metadata = f.Metadata();
        return new(revision, command, metadata.RequestId, metadata.HostInstanceId, metadata.ValidUntilUtc);
    }
    private static async Task<ProjectCommandResponse> BasicMediaCommand(Fixture f, string revision, ProjectCommandDto command)
    {
        using HttpResponseMessage response = await f.Http.PostAsJsonAsync("/api/v1/project-sessions/" + f.Project.ProjectSessionId + "/commands", BasicMediaRequest(f, revision, command), Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProjectCommandResponse>(Json))!;
    }
    private static async Task<StoredAsset> BasicUpload(Fixture f, string name, byte[] bytes)
    {
        using ByteArrayContent content = new(bytes); content.Headers.ContentType = new("audio/wav");
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/assets"); request.Content = content;
        RequestMetadataDto metadata = f.Metadata();
        request.Headers.Add("X-Mako-Asset-Name", name);
        request.Headers.Add("X-Mako-Request-Id", metadata.RequestId);
        request.Headers.Add("X-Mako-Host-Instance-Id", metadata.HostInstanceId);
        request.Headers.Add("X-Mako-Valid-Until-Utc", metadata.ValidUntilUtc!.Value.ToString("O", CultureInfo.InvariantCulture));
        using HttpResponseMessage response = await f.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AssetResponse asset = (await response.Content.ReadFromJsonAsync<AssetResponse>(Json))!;
        return f.Resources.GetAuthorized(f.Client.ClientId, asset.AssetId);
    }
    private static async Task<JobResponse> BasicMediaTerminal(Fixture f, JobResponse job)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        do
        {
            job = (await f.Http.GetFromJsonAsync<JobResponse>("/api/v1/jobs/" + job.JobId, Json, deadline.Token))!;
            if (job.State is "succeeded" or "failed" or "cancelled") { Assert.Equal("succeeded", job.State); return job; }
            await Task.Delay(10, deadline.Token);
        } while (true);
    }
    private static async Task<ProjectWaveformResponse> AssertBasicWaveform(Fixture f, string route, LoadedWaveform expected)
    {
        ProjectWaveformResponse metadata = (await f.Http.GetFromJsonAsync<ProjectWaveformResponse>(route + "/waveform", Json))!;
        Assert.True(metadata.Available); Assert.Equal(expected.Peaks.SampleCount.ToString(CultureInfo.InvariantCulture), metadata.SampleCount);
        Assert.Equal(expected.Peaks.LevelCount, metadata.Levels!.Length);
        foreach (WaveformLevelDto level in metadata.Levels)
        {
            byte[] bytes = await f.Http.GetByteArrayAsync(route + "/waveform/peaks?waveformId=" + metadata.WaveformId + "&level=" + level.Level + "&start=0&count=" + level.BucketCount);
            Assert.Equal(level.BucketCount * 8, bytes.Length);
            for (int index = 0; index < level.BucketCount; index++)
            {
                PeakRange peak = expected.Peaks.GetBucketPeak(level.Level, index);
                Assert.Equal(BitConverter.SingleToInt32Bits(peak.Min), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index * 8)));
                Assert.Equal(BitConverter.SingleToInt32Bits(peak.Max), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index * 8 + 4)));
            }
        }
        return metadata;
    }
    private static (byte[] Bytes, LoadedWaveform Waveform) BasicWave(int rate, int sampleCount, int frequency)
    {
        short[] pcm = [.. Enumerable.Range(0, sampleCount).Select(index => (short)(Math.Sin(index * frequency * 2d * Math.PI / rate) * 20000))];
        using MemoryStream stream = new(); using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8); writer.Write(36 + sampleCount * 2); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(sampleCount * 2);
        foreach (short value in pcm) writer.Write(value);
        var peaks = WaveformPeakPyramid.Build(pcm.Select(value => value / 32768f).ToArray(), rate);
        return (stream.ToArray(), new(new(peaks.DurationUs, rate, 1, "pcm_s16le", "wav"), peaks));
    }
    private static async Task BasicFfmpeg(string[] arguments)
    {
        ProcessStartInfo start = new(TestMediaToolPaths.Ffmpeg) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
        Assert.True(process.ExitCode == 0, await error); _ = await output;
    }
}
