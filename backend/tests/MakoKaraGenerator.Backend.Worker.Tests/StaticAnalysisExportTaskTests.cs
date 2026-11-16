using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Worker.Tests;

public sealed partial class StaticAnalysisExportTaskTests
{
    [Theory]
    [InlineData("available", false)]
    [InlineData("available", true)]
    [InlineData("failed", false)]
    [InlineData("noStream", false)]
    public async Task 原Exporter纯视频保留完整分析和声明且源文件缺失也不探测或降级(string state, bool sourceExists)
    {
        using TemporaryDirectory directory = new();
        string audio = directory.FilePath("declared-source.wav"), candidate = directory.FilePath("candidate.mp4");
        byte[] wave = Wave();
        if (sourceExists) await File.WriteAllBytesAsync(audio, wave);
        ProjectSnapshot snapshot = Snapshot(audio, wave, state);
        WorkerMessage request = Request(snapshot, candidate);
        var factory = new CapturingFactory();
        WorkerMessage response = await StaticExportTask.ExecuteAsync(request, true, _ => Task.CompletedTask, factory, CancellationToken.None);
        Assert.Equal(request.SourceSha256, response.SourceSha256);
        Assert.Equal(WorkerProtocol.ComputeSha256(StaticProjectSnapshotCodec.Encode(snapshot, StaticProjectSnapshotCodec.AnalysisProfile)), response.SourceSha256);
        Assert.Equal(1, factory.Created); Assert.Equal(1, factory.Disposed); Assert.Equal(30, factory.Frames);
        Assert.Equal(snapshot.AudioPath, factory.Snapshot!.AudioPath); Assert.Equal(snapshot.AudioMedia, factory.Snapshot.AudioMedia);
        Assert.Equal(snapshot.AudioAnalysis, factory.Snapshot.AudioAnalysis);
        if (snapshot.AudioAnalysis!.Data is { } data)
            Assert.Equal(AudioAnalysisJson.Encode(data), AudioAnalysisJson.Encode(factory.Snapshot.AudioAnalysis!.Data!));
        ExportReportDto report = WorkerExportContract.DecodeReport(response.Payload, new("opaqueMp4", false, 8, 4, 30, 1, 30, 1_000_000));
        Assert.True(report.StrictlyVerified); Assert.False(report.AudioIncluded); Assert.Null(report.AudioCodec);
        Assert.Null(report.AudioDurationUs); Assert.Null(report.ActualAudioDurationUs); Assert.Empty(report.Warnings);
        Assert.Equal(30, report.DecodedFrames); Assert.Equal(30, report.FramesWritten); Assert.Equal(0, report.FailedFrames);
        Assert.True(File.Exists(candidate));
        Assert.Equal(sourceExists, File.Exists(audio));
    }

    [Fact]
    public async Task 带音轨仅重绑定授权访问路径保原分析媒体显示名和原输入SHA()
    {
        using TemporaryDirectory directory = new(); byte[] wave = Wave();
        string declared = directory.FilePath("declaration-only.wav"), granted = directory.FilePath("actual-grant.wav");
        await File.WriteAllBytesAsync(granted, wave);
        ProjectSnapshot snapshot = Snapshot(declared, wave, "available");
        var grant = new WorkerExportAudioGrant(granted, wave.Length, snapshot.AudioMedia!.Hash);
        WorkerMessage request = Request(snapshot, directory.FilePath("with-audio.mp4"), grant: grant);
        var factory = new CapturingFactory();
        WorkerMessage response = await StaticExportTask.ExecuteAsync(request, true, _ => Task.CompletedTask, factory, CancellationToken.None);
        Assert.Equal(request.SourceSha256, response.SourceSha256); Assert.False(File.Exists(declared));
        Assert.Equal(granted, factory.Snapshot!.AudioPath); Assert.Equal(snapshot.AudioMedia, factory.Snapshot.AudioMedia);
        Assert.Equal("preserved original name.wav", factory.Snapshot.AudioMedia!.OriginalName);
        Assert.Equal(snapshot.AudioAnalysis, factory.Snapshot.AudioAnalysis);
        Assert.Equal(AudioAnalysisJson.Encode(snapshot.AudioAnalysis!.Data!), AudioAnalysisJson.Encode(factory.Snapshot.AudioAnalysis!.Data!));
        ExportReportDto report = WorkerExportContract.DecodeReport(response.Payload, new("opaqueMp4", true, 8, 4, 30, 1, 30, 1_000_000));
        Assert.True(report.StrictlyVerified); Assert.True(report.AudioIncluded); Assert.Equal("aac", report.AudioCodec);
        Assert.Equal(30, report.DecodedFrames); Assert.Equal(1, factory.Created); Assert.Equal(1, factory.Disposed);
    }

    [Fact]
    public async Task 已知但不属于实际快照的metadataProfile在工厂与候选IO前拒绝()
    {
        using TemporaryDirectory directory = new();
        string candidate = directory.FilePath("must-not-exist.mp4");
        ProjectSnapshot snapshot = Snapshot(directory.FilePath("not-read.wav"), Wave(), "available");
        var factory = new CapturingFactory();
        WorkerMessage request = Request(snapshot, candidate, BackendSnapshotProfiles.StaticLyricsScene);
        var error = await Assert.ThrowsAsync<RenderSessionFailureException>(() => StaticExportTask.ExecuteAsync(request, true,
            _ => Task.CompletedTask, factory, CancellationToken.None));
        Assert.Equal("WorkerExportSourceMismatch", error.Code); Assert.Equal(0, factory.Created); Assert.False(File.Exists(candidate));
    }

    [Fact]
    public void 实际metadata加头部与快照共享唯一四MiB预算且未知profile仍拒绝()
    {
        var metadata = Metadata(OperatingSystem.IsWindows() ? @"C:\owned\candidate.mp4" : "/owned/candidate.mp4", BackendSnapshotProfiles.StaticLyricsScene);
        int metadataBytes = WorkerExportContract.EncodeJson(metadata, WorkerExportContract.MaximumMetadataBytes).Length;
        int exact = WorkerProtocol.MaxPayloadBytes - WorkerExportContract.HeaderBytes - metadataBytes;
        Assert.Equal(WorkerProtocol.MaxPayloadBytes, WorkerExportContract.EncodeInput(Guid.NewGuid(), metadata, new byte[exact]).Length);
        var budget = Assert.Throws<WorkerProtocolException>(() => WorkerExportContract.EncodeInput(Guid.NewGuid(), metadata, new byte[exact + 1]));
        Assert.Equal("WorkerSnapshotBudgetExceeded", budget.Code);
        var unknown = Assert.Throws<WorkerProtocolException>(() => WorkerExportContract.EncodeInput(Guid.NewGuid(), metadata with { CodecProfile = "unknown" }, [0]));
        Assert.Equal("WorkerExportProtocolInvalid", unknown.Code);
    }

    [Fact]
    public void Unicode路径实际转义metadata超过预算在封包生成前稳定拒绝()
    {
        string path = OperatingSystem.IsWindows() ? @"C:\" + new string('歌', 4092) : "/" + new string('歌', 4094);
        var metadata = Metadata(path, BackendSnapshotProfiles.StaticLyricsScene) with { FfmpegPath = path, FfprobePath = path };
        var error = Assert.Throws<WorkerProtocolException>(() => WorkerExportContract.EncodeInput(Guid.NewGuid(), metadata, [0]));
        Assert.Equal("WorkerSnapshotBudgetExceeded", error.Code);
    }

    [Fact]
    public void Package无音轨导出封包使用专用codec并可往返且保留完整包源()
    {
        ProjectSnapshot snapshot = PackageSnapshot();
        Assert.Equal(PackageProjectSnapshotCodec.Profile, ProjectPreviewSnapshotProfile.Select(snapshot));
        byte[] source = PackageProjectSnapshotCodec.Encode(snapshot);
        ProjectSnapshot decoded = PackageProjectSnapshotCodec.Decode(source);
        Assert.Equal(source, PackageProjectSnapshotCodec.Encode(decoded));
        var metadata = new WorkerExportMetadata(WorkerExportContract.ExecutionProfile,
            PackageProjectSnapshotCodec.Profile, "opaqueMp4", false,
            OperatingSystem.IsWindows() ? @"C:\owned\package-candidate.mp4" : "/owned/package-candidate.mp4", null, TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe);
        byte[] payload = WorkerExportContract.EncodeInput(Guid.NewGuid(), metadata, source);
        WorkerExportInput input = WorkerExportContract.DecodeInput(payload, capabilities: false);
        Assert.Equal(PackageProjectSnapshotCodec.Profile, input.Metadata.CodecProfile);
        Assert.False(input.Metadata.IncludeAudio);
        Assert.Equal(source, input.SnapshotBytes);
    }

    [Fact]
    public void Package带音轨在Worker封包边界成功往返()
    {
        var metadata = new WorkerExportMetadata(WorkerExportContract.ExecutionProfile,
            PackageProjectSnapshotCodec.Profile, "opaqueMp4", true,
            OperatingSystem.IsWindows() ? @"C:\owned\must-not-exist.mp4" : "/owned/must-not-exist.mp4",
            new(OperatingSystem.IsWindows() ? @"C:\owned\audio.wav" : "/owned/audio.wav", 44, new string('a', 64)),
            TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe);
        byte[] payload = WorkerExportContract.EncodeInput(Guid.NewGuid(), metadata, [1]);
        WorkerExportInput input = WorkerExportContract.DecodeInput(payload, capabilities: false);
        Assert.True(input.Metadata.IncludeAudio);
        Assert.Equal(PackageProjectSnapshotCodec.Profile, input.Metadata.CodecProfile);
        Assert.Equal([1], input.SnapshotBytes);
    }

    [Fact]
    public async Task Worker拒绝把package快照冒充静态来源且不进入Runtime工厂()
    {
        using TemporaryDirectory directory = new();
        ProjectSnapshot snapshot = PackageSnapshot();
        byte[] bytes = PackageProjectSnapshotCodec.Encode(snapshot); Guid id = Guid.NewGuid();
        var metadata = new WorkerExportMetadata(WorkerExportContract.ExecutionProfile,
            BackendSnapshotProfiles.StaticLyricsScene, "opaqueMp4", false,
            directory.FilePath("must-not-exist.mp4"), null, TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe);
        byte[] payload = WorkerExportContract.EncodeInput(id, metadata, bytes);
        WorkerMessage request = new(WorkerExportContract.RequestKind, Guid.NewGuid().ToString("N"),
            WorkerProtocol.ComputeSha256(payload), payload, taskId: id.ToString("N"),
            sourceSha256: WorkerProtocol.ComputeSha256(bytes), exportFormat: "opaqueMp4", exportIncludeAudio: false);
        var factory = new CapturingFactory();
        var failure = await Assert.ThrowsAsync<RenderSessionFailureException>(() => StaticExportTask.ExecuteAsync(
            request, true, _ => Task.CompletedTask, factory, CancellationToken.None));
        Assert.Equal("WorkerExportSourceMismatch", failure.Code);
        Assert.Equal(0, factory.Created);
        Assert.False(File.Exists(metadata.CandidatePath));
    }

    private static ProjectSnapshot Snapshot(string path, byte[] wave, string state)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(wave));
        ProjectAudioAnalysis analysis = state switch
        {
            "failed" => ProjectAudioAnalysis.Failed("OwnedFailure", "preserve complete diagnostic"),
            "noStream" => ProjectAudioAnalysis.NoStream("OwnedNoStream", "preserve unavailable source state"),
            _ => ProjectAudioAnalysis.Available(new(hash, Convert.ToHexStringLower(SHA256.HashData(new byte[48_000 * 4])), 48_000,
                Enumerable.Range(0, 50).Select(index => new AudioAnalysisPoint(index * 20_000L, 0, 0, null, 0)))),
        };
        Project project = Project.Create("owned analysis export", Composition.Create(8, 4, FrameRate.Fps30, outputDurationUs: 1_000_000),
            LyricsDocument.Create([]), audioPath: path) with { AudioMedia = new(hash, wave.Length, "preserved original name.wav"), AudioAnalysis = analysis };
        return ProjectSnapshot.FromProject(project);
    }

    private static ProjectSnapshot PackageSnapshot()
    {
        var package = MixedEffectPackageFixture.Contents();
        var project = Project.Create("package export", Composition.Create(8, 4, FrameRate.Fps30, outputDurationUs: 1_000_000),
            LyricsDocument.Create([])) with
        {
            Effects = new([package], [new()
            {
                Id = "package-instance", Name = "package", PackageId = package.PackageId, Revision = package.Revision,
                ExportId = "controller", Binding = new(ProjectEffectBindingKind.Project), Subscription = new("project"),
            }]),
        };
        return ProjectSnapshot.FromProject(project);
    }

    private static WorkerExportMetadata Metadata(string candidate, string profile, WorkerExportAudioGrant? grant = null) =>
        new(WorkerExportContract.ExecutionProfile, profile, "opaqueMp4", grant is not null, candidate, grant, TestMediaToolPaths.Ffmpeg, TestMediaToolPaths.Ffprobe);

    private static WorkerMessage Request(ProjectSnapshot snapshot, string candidate, string? profile = null, WorkerExportAudioGrant? grant = null)
    {
        byte[] bytes = StaticProjectSnapshotCodec.Encode(snapshot, StaticProjectSnapshotCodec.SelectProfile(snapshot));
        Guid id = Guid.NewGuid();
        byte[] payload = WorkerExportContract.EncodeInput(id, Metadata(candidate, profile ?? StaticProjectSnapshotCodec.AnalysisProfile, grant), bytes);
        return new(WorkerExportContract.RequestKind, Guid.NewGuid().ToString("N"), WorkerProtocol.ComputeSha256(payload), payload,
            taskId: id.ToString("N"), sourceSha256: WorkerProtocol.ComputeSha256(bytes), exportFormat: "opaqueMp4", exportIncludeAudio: grant is not null);
    }

    private static byte[] Wave()
    {
        using MemoryStream stream = new(); using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8); writer.Write(36 + 48_000 * 2); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(48_000); writer.Write(96_000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(48_000 * 2); writer.Write(new byte[48_000 * 2]); return stream.ToArray();
    }

    private sealed class CapturingFactory : IRenderFrameSessionFactory
    {
        public ProjectSnapshot? Snapshot { get; private set; }
        public int Created { get; private set; }
        public int Disposed { get; private set; }
        public int Frames { get; private set; }
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        { Snapshot = snapshot; Created++; return ValueTask.FromResult<IRenderFrameSession>(new Session(this, snapshot)); }
        private sealed class Session(CapturingFactory owner, ProjectSnapshot source) : IRenderFrameSession
        {
            public ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            {
                Assert.Same(source, request.Project); owner.Frames++;
                var target = request.ResolveTarget();
                return ValueTask.FromResult(new RenderFrame(new byte[target.PixelWidth * target.PixelHeight * 4], target,
                    PixelFormat.Bgra8888Premultiplied, request.TimeUs));
            }
            public ValueTask DisposeAsync() { owner.Disposed++; return ValueTask.CompletedTask; }
        }
    }
}
