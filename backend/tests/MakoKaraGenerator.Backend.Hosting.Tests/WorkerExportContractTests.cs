using System.Buffers.Binary;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerExportContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Fact]
    public void 旧WAV音频字段省略保持且未声明质量沿用standard()
    {
        var metadata = new WorkerExportMetadata(WorkerExportContract.ExecutionProfile, BackendSnapshotProfiles.StaticLyricsScene,
            "opaqueMp4", true, "/owned/candidate.mp4", new("/owned/song.wav", 60, new('a', 64)), "/usr/bin/ffmpeg", "/usr/bin/ffprobe");
        byte[] bytes = WorkerExportContract.EncodeInput(Guid.Parse("12345678-1234-4321-1234-123456789abc"), metadata, [1, 2, 3]);
        const string original = """
            {"executionProfile":"static-export-pcmwav-v1","codecProfile":"static-lyrics-scene-v1","format":"opaqueMp4","includeAudio":true,"candidatePath":"/owned/candidate.mp4","audio":{"path":"/owned/song.wav","size":60,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"ffmpegPath":"/usr/bin/ffmpeg","ffprobePath":"/usr/bin/ffprobe","quality":"standard"}
            """;
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(original), bytes.AsSpan(32, bytes.Length - 35).ToArray());
        Assert.Equal("standard", WorkerExportContract.DecodeInput(bytes, false).Metadata.Quality);
        var audio = WorkerExportContract.DecodeInput(bytes, false).Metadata.Audio!;
        Assert.Null(audio.AudioProfile); Assert.True(BackendAudioInputProfiles.TryResolveExportAudioProfile(audio.AudioProfile, out string resolved));
        Assert.Equal(BackendAudioInputProfiles.Pcm16Wave, resolved);
        string legacyAudioWire = "{\"assetId\":\"asset-old\",\"sizeBytes\":60,\"sha256\":\"" + new string('a', 64) + "\"}";
        var legacyAudio = new BackendExportAudioReferenceDto("asset-old", 60, new('a', 64));
        Assert.Equal(legacyAudioWire, JsonSerializer.Serialize(legacyAudio, Json));
        Assert.Equal("{\"format\":\"opaqueMp4\",\"includeAudio\":true,\"audio\":" + legacyAudioWire + ",\"quality\":\"standard\"}",
            JsonSerializer.Serialize(new BackendExportInputDto("opaqueMp4", true, legacyAudio), Json));
        Assert.Equal("{\"format\":\"opaqueMp4\",\"includeAudio\":false,\"audio\":null,\"quality\":\"standard\"}",
            JsonSerializer.Serialize(new BackendExportInputDto("opaqueMp4", false, null), Json));
    }

    [Theory]
    [InlineData(BackendAudioInputProfiles.Mp3)]
    [InlineData(BackendAudioInputProfiles.Flac)]
    public void 压缩来源必须显式格式并与唯一执行profile匹配(string profile)
    {
        var metadata = new WorkerExportMetadata(WorkerExportContract.CompressedExecutionProfile, BackendSnapshotProfiles.StaticLyricsScene,
            "opaqueMp4", true, "/owned/candidate.mp4", new("/owned/source", 64, new('a', 64), profile), "/usr/bin/ffmpeg", "/usr/bin/ffprobe");
        var actual = WorkerExportContract.DecodeInput(WorkerExportContract.EncodeInput(Guid.NewGuid(), metadata, [1]), false);
        Assert.Equal(profile, actual.Metadata.Audio!.AudioProfile);
        Assert.Equal(WorkerExportContract.CompressedExecutionProfile, WorkerExportContract.GetExecutionProfile(profile));
        Assert.Throws<WorkerProtocolException>(() => WorkerExportContract.ValidateMetadata(metadata with { ExecutionProfile = WorkerExportContract.ExecutionProfile }, false));
        Assert.Throws<WorkerProtocolException>(() => WorkerExportContract.ValidateMetadata(metadata with { Audio = metadata.Audio! with { AudioProfile = null } }, false));
        Assert.Throws<WorkerProtocolException>(() => WorkerExportContract.ValidateMetadata(metadata with { Audio = metadata.Audio! with { AudioProfile = "unknown" } }, false));
        Assert.Throws<WorkerProtocolException>(() => WorkerExportContract.ValidateMetadata(metadata with { IncludeAudio = false, Audio = null }, false));
    }

    [Theory]
    [InlineData(BackendAudioInputProfiles.Mp3)]
    [InlineData(BackendAudioInputProfiles.Flac)]
    public void 报告压缩源时长预算必须核真实值而旧WAV不误套300秒(string profile)
    {
        var expected = new WorkerExportReportExpectation("opaqueMp4", true, 320, 180, 30, 1, 30, 1_000_000, profile,
            ResourcePolicy: new WorkerExportResourcePolicy { MaxCompressedAudioDurationUs = 300_000_000 });
        var report = Report() with { AudioIncluded = true, AudioCodec = "aac", AudioDurationUs = "300000000", ActualAudioDurationUs = "1000000" };
        WorkerExportContract.ValidateReport(report, expected);
        report = report with { AudioDurationUs = "300000001" };
        Assert.Throws<WorkerProtocolException>(() => WorkerExportContract.ValidateReport(report, expected));
        WorkerExportContract.ValidateReport(report, expected with { AudioProfile = null });
        Assert.Throws<WorkerProtocolException>(() => WorkerExportContract.ValidateReport(report, expected with { AudioProfile = "future" }));
        Assert.Throws<WorkerProtocolException>(() => WorkerExportContract.ValidateReport(Report(), expected with { IncludeAudio = false }));
    }

    [Fact]
    public void 私有输入完整保护metadata与snapshot且严格拒绝重复字段()
    {
        var metadata = new WorkerExportMetadata(WorkerExportContract.ExecutionProfile, "static-lyrics-scene-v1", "opaqueMp4", false,
            "/owned/candidate.mp4", null, "/usr/bin/ffmpeg", "/usr/bin/ffprobe");
        Guid id = Guid.NewGuid(); byte[] bytes = WorkerExportContract.EncodeInput(id, metadata, [1, 2, 3]);
        var input = WorkerExportContract.DecodeInput(bytes, false);
        Assert.Equal(metadata, input.Metadata); Assert.Equal(id, input.TaskId); Assert.Equal(new byte[] { 1, 2, 3 }, input.SnapshotBytes);
        string hash = WorkerProtocol.ComputeSha256(bytes); bytes[^1] = 5;
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Validate(new(WorkerExportContract.RequestKind, "request", hash, bytes)));
        Assert.Throws<WorkerProtocolException>(() => WorkerExportContract.DecodeJson<WorkerExportCapabilitiesDto>("{\"guardAvailable\":true,\"guardAvailable\":false,\"opaqueMp4\":true,\"transparentProRes4444\":true}"u8, 4096));
    }
    [Theory]
    [InlineData("header")] [InlineData("version")] [InlineData("reserved")] [InlineData("size")]
    public void 导出畸形header统一typed拒绝(string mode)
    {
        var meta = new WorkerExportMetadata(WorkerExportContract.ExecutionProfile, "static-lyrics-scene-v1", "opaqueMp4", false, null, null, "/usr/bin/ffmpeg", "/usr/bin/ffprobe");
        byte[] bytes = WorkerExportContract.EncodeInput(Guid.NewGuid(), meta, []);
        switch (mode) { case "header": bytes[0] = 0; break; case "version": bytes[4] = 2; break;
            case "reserved": bytes[7] = 1; break; case "size": bytes.AsSpan(24, 4).Fill(255); break; }
        Assert.Throws<WorkerProtocolException>(() => WorkerExportContract.DecodeInput(bytes, true));
    }
    [Theory]
    [InlineData("decoded")] [InlineData("fallback")] [InlineData("dropped")] [InlineData("alpha")]
    [InlineData("null-format")] [InlineData("false-strict")] [InlineData("error")]
    [InlineData("requested-quality")] [InlineData("actual-quality")]
    public void 实际报告错误不能伪装候选成功(string mode)
    {
        var expected = new WorkerExportReportExpectation("opaqueMp4", false, 320, 180, 30, 1, 30, 1_000_000);
        var report = Report();
        report = mode switch
        {
            "decoded" => report with { DecodedFrames = 29 }, "fallback" => report with { FailedFrames = 1 }, "dropped" => report with { DroppedFrames = 1 },
            "alpha" => report with { HasAlpha = true }, "null-format" => report with { PixelFormat = null! },
            "false-strict" => report with { StrictlyVerified = false },
            "requested-quality" => report with { RequestedQuality = "high" }, "actual-quality" => report with { ActualQuality = "draft" },
            "error" => report with { Diagnostics = [new("actual", "error", "actual error", null, null, "runtime")] }, _ => throw new ArgumentException(mode),
        };
        Assert.Throws<WorkerProtocolException>(() => WorkerExportContract.ValidateReport(report, expected));
    }
    [Fact]
    public void 原验证标记不被强profile静默改写且完整报告有界()
    {
        var expected = new WorkerExportReportExpectation("opaqueMp4", false, 320, 180, 30, 1, 30, 1_000_000);
        var report = Report() with { IsVerified = false };
        var decoded = WorkerExportContract.DecodeReport(WorkerExportContract.EncodeReport(report, expected), expected);
        Assert.False(decoded.IsVerified); Assert.True(decoded.StrictlyVerified);
        var huge = report with { Warnings = [new('x', WorkerExportContract.MaximumReportBytes)] };
        Assert.Throws<WorkerProtocolException>(() => WorkerExportContract.EncodeReport(huge, expected));
    }
    [Theory]
    [InlineData("float")] [InlineData("channels")] [InlineData("bits")] [InlineData("length")] [InlineData("partial-sample")] [InlineData("playlist")]
    public void 非自闭包PCM16WAV不能从声明获取解码能力(string mode)
    {
        byte[] bytes = Wave();
        switch (mode)
        {
            case "float": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 3); break;
            case "channels": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 3); break;
            case "bits": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 24); break;
            case "length": bytes[4] = 1; break;
            case "partial-sample": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), 15); break;
            case "playlist": bytes = "ffconcat version 1.0\nfile other.wav"u8.ToArray(); break;
        }
        using MemoryStream stream = new(bytes);
        Assert.Equal("WorkerExportAudioUnsupported", Assert.Throws<WorkerProtocolException>(() => WorkerExportPcmWave.Validate(stream, bytes.Length)).Code);
    }
    [Fact]
    public void PCM真实采样时长与流位置保真()
    {
        byte[] bytes = Wave(); using MemoryStream stream = new(bytes); stream.Position = 7;
        Assert.Equal(1000, WorkerExportPcmWave.Validate(stream, bytes.Length)); Assert.Equal(7, stream.Position);
    }
    [Theory]
    [InlineData(70_000)] [InlineData(250_000)]
    public async Task 导出专属实际失败诊断跨原64KiB完整framed往返且保真实发生身份(int messageLength)
    {
        Guid epoch = Guid.NewGuid();
        var snapshot = new WorkerDiagnosticsSnapshotDto([new(epoch, 1, new("actual-instance", "actual-artifact", "Evaluate", 42,
            "Error", "ActualError", new('x', messageLength), new("src/effect.cs", 2, 3), new("pkg", "sha256:" + new string('b', 64), "main")))], true);
        Assert.Throws<WorkerProtocolException>(() => WorkerDiagnosticsSnapshotDto.Validate(snapshot));
        var message = new WorkerMessage("error", "export", errorCode: "WorkerRuntimeDiagnosticError", exportFormat: "opaqueMp4",
            exportDiagnostics: snapshot);
        using MemoryStream stream = new(); await WorkerProtocol.WriteAsync(stream, message); stream.Position = 0;
        var decoded = (await WorkerProtocol.ReadAsync(stream))!;
        var occurrence = Assert.Single(decoded.ExportDiagnostics!.Occurrences);
        Assert.Equal(epoch, occurrence.Epoch); Assert.Equal(1, occurrence.Sequence); Assert.Equal(snapshot.Occurrences[0].Diagnostic, occurrence.Diagnostic);
        Assert.Null(decoded.Diagnostics); Assert.Equal("WorkerRuntimeDiagnosticError", decoded.ErrorCode);
    }
    [Theory]
    [InlineData("bytes")] [InlineData("count")] [InlineData("epoch")] [InlineData("sequence")] [InlineData("flag")]
    public void 导出失败完整诊断超预算或错来源拒绝不伪造截断(string mode)
    {
        Guid epoch = Guid.NewGuid();
        var item = new WorkerRuntimeDiagnosticOccurrenceDto(epoch, 1,
            new("instance", "artifact", "Evaluate", 0, "Error", "ActualError", "actual error", null, null));
        WorkerRuntimeDiagnosticOccurrenceDto[] items = mode switch
        {
            "count" => Enumerable.Range(1, 513).Select(index => item with { Sequence = index }).ToArray(),
            "bytes" => [item with { Diagnostic = item.Diagnostic with { Message = new('x', WorkerExportContract.MaximumReportBytes) } }],
            "epoch" => [item with { Epoch = Guid.Empty }], "sequence" => [item with { Sequence = 2 }], _ => [item],
        };
        var snapshot = new WorkerDiagnosticsSnapshotDto(items, mode != "flag");
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Validate(new("error", "export", exportFormat: "opaqueMp4", exportDiagnostics: snapshot)));
        var primary = new WorkerMessage("error", "export", errorCode: "ActualError", exportFormat: "opaqueMp4",
            exportDiagnosticsFailureCode: "WorkerExportReportBudgetExceeded");
        WorkerProtocol.Validate(primary); Assert.Equal("ActualError", primary.ErrorCode); Assert.Null(primary.ExportDiagnostics);
    }
    private static ExportReportDto Report() => new("opaqueMp4", false, 30, 30, 30, 0, 0, 320, 180, 30, 1, "1000000", "1000000", null,
        "h264", null, "yuv420p", false, 1000, new('a', 64), [], [], true, null, true);
    private static byte[] Wave()
    {
        byte[] bytes = new byte[60]; "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 52);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 8000); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 16000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 2); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), 16); return bytes;
    }
}
