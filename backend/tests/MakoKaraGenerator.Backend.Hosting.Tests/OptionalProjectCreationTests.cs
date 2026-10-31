using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class OptionalProjectCreationTests
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task 四种素材组合均直接发布零修订且收据重放保留最初摘要(bool withAudio, bool withLyrics)
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path });
        StoredAsset audio = await store.UploadAsync("a", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
        StoredAsset lyrics = await store.UploadAsync("a", "song.lrc", "text/plain", new MemoryStream("[00:00.00]hello\n"u8.ToArray()));
        int decodeCalls = 0;
        await using ProjectSessionRegistry registry = new("host", store, new((path, _) =>
        {
            Assert.Equal(audio.StoragePath, path);
            decodeCalls++;
            return Task.FromResult<LoadedWaveform?>(Waveform());
        }));
        RequestMetadataDto metadata = new("create", "host", DateTimeOffset.UtcNow.AddMinutes(1));
        CreateProjectSessionRequest request = new("  optional  ", withAudio ? audio.AssetId : null, withLyrics ? lyrics.AssetId : null,
            new(640, 360), Metadata: metadata);
        ProjectSessionCreateResult created = await registry.CreateAsync("a", request);
        Assert.True(created.Accepted, created.Error?.Message);
        ProjectSession session = created.Session!;
        ProjectSessionSummaryResponse summary = created.Summary!;
        Assert.Equal("optional", summary.Name);
        Assert.Equal("0", summary.ProjectRevision);
        Assert.Equal(withAudio ? audio.AssetId : "", summary.AudioAssetId);
        Assert.Equal(withAudio ? 3_000_000 : 0, summary.AudioDurationUs);
        Assert.Equal(withLyrics ? 1 : 0, summary.LyricsLineCount);
        Assert.True(summary.IsDirty); Assert.False(summary.HasPath); Assert.False(summary.CanUndo); Assert.False(summary.CanRedo);
        Assert.False(registry.TryGet("b", session.ProjectSessionId, out _));
        var branches = (await registry.ReadLyricBranchesAsync("a", session.ProjectSessionId, "0")).Response!;
        Assert.Equal("primary", branches.PrimaryBranchId);
        Assert.Equal("primary", Assert.Single(branches.Branches).Id);
        ProjectWaveformResponse waveform = (await registry.ReadWaveformAsync("a", session.ProjectSessionId)).Response!;
        Assert.Equal(withAudio, waveform.Available);
        if (!withAudio) Assert.Equal("noAudio", waveform.Reason);
        if (!withLyrics) Assert.Empty((await registry.ReadLyricsAsync("a", session.ProjectSessionId, "primary"))!.Lines);
        var settings = (await registry.ReadSettingsAsync("a", session.ProjectSessionId, "0")).Response!;
        Assert.Null(settings.OutputDurationUs);

        ProjectSessionCreateResult replay = await registry.CreateAsync("a", request);
        Assert.Equal(summary, replay.Summary); Assert.Same(session, replay.Session);
        Assert.Single((await registry.ReadClientStateAsync("a")).Sessions);
        Assert.Equal(withAudio ? 1 : 0, decodeCalls);
        Assert.Equal(BackendErrorCode.RequestIdReuse,
            (await registry.CreateAsync("a", request with { Name = "different" })).Error!.Code);
    }

    [Theory]
    [InlineData("{\"name\":\"empty\"}")]
    [InlineData("{\"name\":\"empty\",\"audioAssetId\":null,\"lyricsAssetId\":null}")]
    public async Task JSON省略或显式null素材均保持空领域工程(string body)
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path });
        await using ProjectSessionRegistry registry = new("host", store, new((_, _) => throw new InvalidOperationException("No decode expected.")));
        CreateProjectSessionRequest request = JsonSerializer.Deserialize<CreateProjectSessionRequest>(body, WireJson)!;
        ProjectSessionCreateResult created = await registry.CreateAsync("a", request);
        Assert.True(created.Accepted);
        var branches = (await registry.ReadLyricBranchesAsync("a", created.Session!.ProjectSessionId, "0")).Response!;
        Assert.Equal("主歌词", Assert.Single(branches.Branches).Name);
        Assert.Equal(0, (await registry.ReadSummaryAsync("a", created.Session.ProjectSessionId))!.LyricsLineCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 单一素材仍执行owner授权且失败不发布工程(bool audio)
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path });
        StoredAsset resource = await store.UploadAsync("owner", audio ? "song.wav" : "song.lrc", audio ? "audio/wav" : "text/plain",
            new MemoryStream("[00:00.00]hello\n"u8.ToArray()));
        await using ProjectSessionRegistry registry = new("host", store, new((_, _) => throw new InvalidOperationException("No decode expected.")));
        var rejected = await registry.CreateAsync("other", new("forbidden", audio ? resource.AssetId : null, audio ? null : resource.AssetId));
        Assert.Equal(BackendErrorCode.ResourceUnauthorized, rejected.Error!.Code);
        Assert.Empty((await registry.ReadClientStateAsync("other")).Sessions);
    }

    [Fact]
    public async Task 仅歌词继续拒绝空导入结果且保留真实Importer选项()
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path });
        StoredAsset empty = await store.UploadAsync("a", "empty.txt", "text/plain", new MemoryStream("\r\n \t"u8.ToArray()));
        StoredAsset lyrics = await store.UploadAsync("a", "song.lrc", "text/plain", new MemoryStream("[00:00.00]hello\n"u8.ToArray()));
        await using ProjectSessionRegistry registry = new("host", store, new((_, _) => throw new InvalidOperationException("No decode expected.")));
        var rejected = await registry.CreateAsync("a", new("empty", LyricsAssetId: empty.AssetId));
        Assert.Equal(BackendErrorCode.EmptyLyrics, rejected.Error!.Code);
        var accepted = await registry.CreateAsync("a", new("lyrics", LyricsAssetId: lyrics.AssetId, ImporterId: "lrc.plain", EncodingName: "utf-8"));
        Assert.True(accepted.Accepted, accepted.Error?.Message);
        Assert.Single((await registry.ReadClientStateAsync("a")).Sessions);
        Assert.Equal("hello", Assert.Single((await registry.ReadLyricsAsync("a", accepted.Session!.ProjectSessionId, "primary"))!.Lines).Text);
    }

    private static LoadedWaveform Waveform()
    {
        WaveformPeakBuilder builder = new(); builder.Append(new float[24_000]);
        return new(new AudioStreamInfo(3_000_000, 8_000, 1, "pcm", "wav"), builder.Build(8_000));
    }
}
