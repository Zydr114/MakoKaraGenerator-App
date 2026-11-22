using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 同步create输入拒绝或无音流仍为原422字段错误且不附执行编号(bool noStream)
    {
        await using Fixture f = await Fixture.Create();
        PreparationExecutor executor = new() { RejectInput = !noStream, NoStream = noStream };
        f.Projects.ConfigureAudioPreparation(executor, new(true, true));
        using HttpResponseMessage response = await f.Http.PostAsJsonAsync("/api/v1/project-sessions", await PreparationRequest(f), Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        BackendErrorResponse error = JsonSerializer.Deserialize<BackendErrorResponse>(body, Json)!;
        Assert.Equal(noStream ? BackendErrorCode.NoAudioStream : BackendErrorCode.AudioPreparationFailed, error.Code);
        Assert.Equal(nameof(NewProjectPreparationRequest.AudioPath), error.Field); Assert.Null(error.FailureCode);
        using JsonDocument wire = JsonDocument.Parse(body);
        Assert.False(wire.RootElement.TryGetProperty("failureCode", out _));
        Assert.DoesNotContain("/private/audio", System.Text.Encoding.UTF8.GetString(body), StringComparison.Ordinal);
        Assert.Single((await f.Projects.ReadClientStateAsync(f.Client.ClientId)).Sessions);
    }

    [Fact]
    public async Task 准备能力尾部disabled与原同步create503无fallback保原已成功会话()
    {
        await using Fixture f = await Fixture.Create(); f.Projects.ConfigureAudioPreparation(null, new(false, false, "WorkerAudioPreparationProfileMismatch"));
        BackendCapabilitiesResponse caps = (await f.Http.GetFromJsonAsync<BackendCapabilitiesResponse>("/api/v1/capabilities", Json))!;
        BackendAudioPreparationCapabilitiesDto prepare = caps.AudioPreparation!; Assert.False(prepare.Enabled);
        Assert.Equal("WorkerAudioPreparationProfileMismatch", prepare.DisabledReason); Assert.Equal("self-contained-native-waveform-v1", prepare.Profile);
        Assert.Equal(["wav", "mp3", "flac", "aac", "mov", "ogg"], prepare.Families); Assert.Equal(4 * 1024 * 1024, prepare.MaxPayloadBytes);
        Assert.Equal(500_000, prepare.MaxBaseBuckets); Assert.Equal(64, prepare.BaseBucketSamples); Assert.Equal(60, prepare.OperationTimeoutSeconds);
        Assert.Equal(2, prepare.CancellationGraceSeconds); Assert.Equal(1, prepare.MaxRunning); Assert.Equal(4, prepare.MaxQueued);
        using HttpResponseMessage rejected = await f.Http.PostAsJsonAsync("/api/v1/project-sessions", await PreparationRequest(f), Json);
        await Error(rejected, HttpStatusCode.ServiceUnavailable, BackendErrorCode.TaskUnavailable);
        Assert.Single((await f.Projects.ReadClientStateAsync(f.Client.ClientId)).Sessions);
    }

    [Theory]
    [InlineData("WorkerAudioPreparationTimedOut")]
    [InlineData("WorkerAudioPreparationBudgetExceeded")]
    [InlineData("WorkerAudioPreparationTransportFailed")]
    public async Task 同步create执行失败HTTP500保公开稳定编号且不泄露可信内部详情(string code)
    {
        await using Fixture f = await Fixture.Create(); PreparationExecutor executor = new() { Failure = new(code, "/private/audio ffmpeg raw arguments") };
        f.Projects.ConfigureAudioPreparation(executor, new(true, true));
        using HttpResponseMessage response = await f.Http.PostAsJsonAsync("/api/v1/project-sessions", await PreparationRequest(f), Json);
        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        BackendErrorResponse result = JsonSerializer.Deserialize<BackendErrorResponse>(responseBytes, Json)!;
        Assert.Equal(BackendErrorCode.TaskExecutionFailed, result.Code);
        Assert.Equal(code, result.FailureCode); Assert.DoesNotContain("/private/audio", System.Text.Encoding.UTF8.GetString(responseBytes), StringComparison.Ordinal);
        Assert.Single((await f.Projects.ReadClientStateAsync(f.Client.ClientId)).Sessions);
    }

    [Fact]
    public async Task 真HTTPcreate断流不取消已接受准备而同回执只发布一个201摘要()
    {
        await using Fixture f = await Fixture.Create(); PreparationExecutor executor = new() { Block = true };
        f.Projects.ConfigureAudioPreparation(executor, new(true, true)); CreateProjectSessionRequest input = await PreparationRequest(f);
        using CancellationTokenSource caller = new(); Task<HttpResponseMessage> original = f.Http.PostAsJsonAsync("/api/v1/project-sessions", input, Json, caller.Token);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8)); caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original);
        Assert.False(executor.Token.IsCancellationRequested); executor.Release.TrySetResult();
        using HttpResponseMessage retry = await f.Http.PostAsJsonAsync("/api/v1/project-sessions", input, Json);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode); ProjectSessionSummaryResponse summary = (await retry.Content.ReadFromJsonAsync<ProjectSessionSummaryResponse>(Json))!;
        using HttpResponseMessage again = await f.Http.PostAsJsonAsync("/api/v1/project-sessions", input, Json);
        Assert.Equal(summary.ProjectSessionId, (await again.Content.ReadFromJsonAsync<ProjectSessionSummaryResponse>(Json))!.ProjectSessionId);
        Assert.Equal(1, executor.Calls); Assert.Equal(2, (await f.Projects.ReadClientStateAsync(f.Client.ClientId)).Sessions.Length);
        Assert.Equal("0", summary.ProjectRevision); Assert.True(summary.IsDirty); Assert.False(summary.HasPath);
    }

    [Fact]
    public async Task 旧错误及能力nullable尾部缺省时wire不增加字段()
    {
        using JsonDocument error = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new BackendErrorResponse(BackendErrorCode.InvalidRequest, "old"), Json));
        Assert.False(error.RootElement.TryGetProperty("failureCode", out _));
        await using Fixture f = await Fixture.Create();
        using HttpResponseMessage caps = await f.Http.GetAsync("/api/v1/capabilities");
        using JsonDocument actual = JsonDocument.Parse(await caps.Content.ReadAsByteArrayAsync()); Assert.True(actual.RootElement.TryGetProperty("audioPreparation", out _));
        Assert.False(actual.RootElement.GetProperty("audioPreparation").GetProperty("enabled").GetBoolean());
    }

    private static async Task<CreateProjectSessionRequest> PreparationRequest(Fixture f)
    {
        StoredAsset lyrics = await f.Resources.UploadAsync(f.Client.ClientId, "managed.lrc", "text/plain", new MemoryStream("[00:00.00]managed\n"u8.ToArray()));
        return new("managed", f.Project.AudioAssetId, lyrics.AssetId, Metadata: f.Metadata());
    }
    private sealed class PreparationExecutor : IBackendAudioPreparationExecutor
    {
        internal int Calls; internal bool Block; internal bool RejectInput; internal bool NoStream;
        internal CancellationToken Token; internal RenderSessionFailureException? Failure;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ValidateInput(BackendAudioPreparationGrant grant) { }
        public async Task<LoadedWaveform?> ExecuteAsync(BackendAudioPreparationGrant grant, CancellationToken cancellationToken = default)
        {
            Calls++; Token = cancellationToken; Entered.TrySetResult(); if (Failure is not null) throw Failure;
            if (RejectInput) throw new IOException("/private/audio ffmpeg raw arguments");
            if (NoStream) return null;
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            WaveformPeakBuilder builder = new(); builder.Append(new float[24000]); return new(new(9_000_000, 8000, 1, "pcm", "wav"), builder.Build(8000));
        }
    }
}
