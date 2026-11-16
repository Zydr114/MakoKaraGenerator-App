using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class ProjectImageExportWorkerContractTests
{
    [Fact]
    public void KIE1精确总边界含动态metadata并固定完整envelope哈希且与旧任务隔离()
    {
        Guid id = Guid.NewGuid();
        ProjectImageExportWorkerMetadata metadata = Metadata();
        byte[] small = ProjectImageExportWorkerContract.EncodeInput(id, metadata, [3]);
        int metadataBytes = BinaryPrimitives.ReadInt32LittleEndian(small.AsSpan(24, 4));
        byte[] source = new byte[WorkerProtocol.MaxPayloadBytes - ProjectImageExportWorkerContract.HeaderBytes - metadataBytes];
        source[0] = 17; source[^1] = 23;
        byte[] payload = ProjectImageExportWorkerContract.EncodeInput(id, metadata, source);
        Assert.Equal(WorkerProtocol.MaxPayloadBytes, payload.Length);
        ProjectImageExportWorkerInput decoded = ProjectImageExportWorkerContract.DecodeInput(payload);
        Assert.Equal(id, decoded.TaskId); Assert.Equal(metadata, decoded.Metadata); Assert.Equal(source, decoded.SnapshotBytes);
        Assert.Equal(WorkerProtocol.ComputeSha256(source), decoded.SourceSha256);
        Assert.NotEqual(WorkerProtocol.ComputeSha256(payload.AsSpan(32)), decoded.SourceSha256);
        Assert.Equal("WorkerSnapshotBudgetExceeded", Assert.Throws<WorkerProtocolException>(() =>
            ProjectImageExportWorkerContract.EncodeInput(id, metadata, new byte[source.Length + 1])).Code);
        Assert.Equal("WorkerSnapshotBudgetExceeded", Assert.Throws<WorkerProtocolException>(() =>
            ProjectImageExportWorkerContract.DecodeInput(new byte[WorkerProtocol.MaxPayloadBytes + 1])).Code);
        Assert.Throws<WorkerProtocolException>(() => WorkerExportContract.DecodeInput(payload, false));
        Assert.Throws<WorkerProtocolException>(() => ProjectImageExportWorkerContract.DecodeInput(
            WorkerExportContract.EncodeInput(id, new(WorkerExportContract.ExecutionProfile, BackendSnapshotProfiles.StaticLyricsScene,
                "opaqueMp4", false, metadata.CandidatePath, null, metadata.FfmpegPath, metadata.FfprobePath), [1])));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void 能力任务形状必须编码解码对称(bool capabilities)
    {
        ProjectImageExportWorkerMetadata metadata = capabilities ? Metadata() with { CandidatePath = null } : Metadata();
        byte[] source = capabilities ? [] : [1];
        byte[] payload = ProjectImageExportWorkerContract.EncodeInput(Guid.NewGuid(), metadata, source, capabilities);
        Assert.Equal(source, ProjectImageExportWorkerContract.DecodeInput(payload, capabilities).SnapshotBytes);
        Assert.Throws<WorkerProtocolException>(() => ProjectImageExportWorkerContract.DecodeInput(payload, !capabilities));
        Assert.Throws<WorkerProtocolException>(() => ProjectImageExportWorkerContract.EncodeInput(Guid.NewGuid(), metadata,
            capabilities ? [1] : [], capabilities));
    }

    [Theory]
    [InlineData("magic")] [InlineData("version")] [InlineData("reserved")] [InlineData("zero-id")]
    [InlineData("negative-meta")] [InlineData("overflow-meta")] [InlineData("missing-meta")] [InlineData("short")]
    public void 封包头与metadata边界畸形拒绝(string mode)
    {
        byte[] payload = ProjectImageExportWorkerContract.EncodeInput(Guid.NewGuid(), Metadata(), [1]);
        switch (mode)
        {
            case "magic": payload[0] = 0; break;
            case "version": payload[4] = 2; break;
            case "reserved": payload[28] = 1; break;
            case "zero-id": payload.AsSpan(8, 16).Clear(); break;
            case "negative-meta": BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24), -1); break;
            case "overflow-meta": BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24), payload.Length); break;
            case "missing-meta": BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24), 0); break;
            case "short": payload = [1, 2, 3]; break;
        }
        Assert.Equal("WorkerImageExportInputRejected", Assert.Throws<WorkerProtocolException>(() =>
            ProjectImageExportWorkerContract.DecodeInput(payload)).Code);
    }

    [Theory]
    [InlineData("maximumInputBytes")] [InlineData("maximumImageBytes")] [InlineData("maximumDimension")]
    [InlineData("maximumDecodedBytes")] [InlineData("maximumBaseSnapshotBytes")] [InlineData("maximumSourceValues")]
    [InlineData("maximumReportBytes")] [InlineData("maximumMediaBytes")] [InlineData("maximumAudioBytes")]
    [InlineData("packageDiagnosticsVersion")] [InlineData("imageDecoderProfile")] [InlineData("taskProfile")]
    [InlineData("baseProfiles")] [InlineData("packageCapabilities")] [InlineData("exportCapabilities")]
    public void 能力每个必填预算解码器及基础来源缺失均拒绝(string field)
    {
        JsonObject node = JsonNode.Parse(ProjectImageExportWorkerContract.EncodeCapabilities(Capabilities()))!.AsObject();
        Assert.True(node.Remove(field));
        Assert.Equal("WorkerImageExportCapabilitiesInvalid", Assert.Throws<WorkerProtocolException>(() =>
            ProjectImageExportWorkerContract.DecodeCapabilities(Encoding.UTF8.GetBytes(node.ToJsonString()))).Code);
    }

    [Theory]
    [InlineData("maximumSourceValues")] [InlineData("maximumReportBytes")]
    [InlineData("maximumInputBytes")] [InlineData("maximumBaseSnapshotBytes")]
    public void 能力预算更改不能通过结构合法JSON(string field)
    {
        JsonObject node = JsonNode.Parse(ProjectImageExportWorkerContract.EncodeCapabilities(Capabilities()))!.AsObject();
        node[field] = 1;
        Assert.Throws<WorkerProtocolException>(() => ProjectImageExportWorkerContract.DecodeCapabilities(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Theory]
    [InlineData("maximumMediaBytes")] [InlineData("maximumAudioBytes")]
    public void 宿主媒体预算允许正数和null但不允许零(string field)
    {
        JsonObject node = JsonNode.Parse(ProjectImageExportWorkerContract.EncodeCapabilities(Capabilities()))!.AsObject();
        node[field] = 1;
        ProjectImageExportWorkerContract.DecodeCapabilities(Encoding.UTF8.GetBytes(node.ToJsonString()));
        node[field] = null;
        ProjectImageExportWorkerContract.DecodeCapabilities(Encoding.UTF8.GetBytes(node.ToJsonString()));
        node[field] = 0;
        Assert.Throws<WorkerProtocolException>(() => ProjectImageExportWorkerContract.DecodeCapabilities(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 通用导出器旧四项及当前六项来源均保留图片三项基础合同(bool expanded)
    {
        var capabilities = Capabilities();
        if (expanded)
            capabilities = capabilities with { ExportCapabilities = capabilities.ExportCapabilities with
            { SourceProfiles = [.. capabilities.ExportCapabilities.SourceProfiles!,
                BackendSnapshotProfiles.PackageLyricsScenePresets, BackendSnapshotProfiles.RuntimeProjectInput] } };

        var decoded = ProjectImageExportWorkerContract.DecodeCapabilities(
            ProjectImageExportWorkerContract.EncodeCapabilities(capabilities));

        Assert.Equal(capabilities.ExportCapabilities.SourceProfiles, decoded.ExportCapabilities.SourceProfiles);
        Assert.Equal([BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis,
            BackendSnapshotProfiles.PackageLyricsScene], decoded.BaseProfiles);
    }

    [Theory]
    [InlineData(BackendSnapshotProfiles.StaticLyricsScene)]
    [InlineData(BackendSnapshotProfiles.StaticLyricsSceneAnalysis)]
    [InlineData(BackendSnapshotProfiles.StaticLyricsScenePresets)]
    [InlineData(BackendSnapshotProfiles.PackageLyricsScene)]
    [InlineData("duplicate")] [InlineData("unknown")]
    public void 通用导出器缺必需重复或未知来源不能通过当前图片能力合同(string mutation)
    {
        JsonObject node = JsonNode.Parse(ProjectImageExportWorkerContract.EncodeCapabilities(Capabilities()))!.AsObject();
        var profiles = node["exportCapabilities"]!["sourceProfiles"]!.AsArray();
        profiles.Add(BackendSnapshotProfiles.PackageLyricsScenePresets);
        profiles.Add(BackendSnapshotProfiles.RuntimeProjectInput);
        if (mutation == "duplicate") profiles[4] = BackendSnapshotProfiles.PackageLyricsScene;
        else if (mutation == "unknown") profiles[5] = "unknown-export-source-v1";
        else profiles.RemoveAt(profiles.Select(value => value!.GetValue<string>()).ToList().IndexOf(mutation));

        Assert.Equal("WorkerImageExportCapabilitiesInvalid", Assert.Throws<WorkerProtocolException>(() =>
            ProjectImageExportWorkerContract.DecodeCapabilities(Encoding.UTF8.GetBytes(node.ToJsonString()))).Code);
    }

    [Theory]
    [InlineData(BackendSnapshotProfiles.StaticLyricsScenePresets)]
    [InlineData(BackendSnapshotProfiles.PackageLyricsScenePresets)]
    [InlineData(BackendSnapshotProfiles.RuntimeProjectInput)]
    public void 通用导出器新增来源不能进入图片基础来源列表(string profile)
    {
        JsonObject node = JsonNode.Parse(ProjectImageExportWorkerContract.EncodeCapabilities(Capabilities()))!.AsObject();
        node["baseProfiles"]![2] = profile;

        Assert.Equal("WorkerImageExportCapabilitiesInvalid", Assert.Throws<WorkerProtocolException>(() =>
            ProjectImageExportWorkerContract.DecodeCapabilities(Encoding.UTF8.GetBytes(node.ToJsonString()))).Code);
    }

    [Fact]
    public void 能力重复字段及package带音轨均按当前合同处理()
    {
        string json = Encoding.UTF8.GetString(ProjectImageExportWorkerContract.EncodeCapabilities(Capabilities()));
        Assert.Throws<WorkerProtocolException>(() => ProjectImageExportWorkerContract.DecodeCapabilities(Encoding.UTF8.GetBytes(
            json.Replace("\"maximumInputBytes\":", "\"MaximumInputBytes\":1,\"maximumInputBytes\":", StringComparison.Ordinal))));
        ProjectImageExportWorkerMetadata packageAudio = Metadata() with
        { BaseProfile = BackendSnapshotProfiles.PackageLyricsScene, IncludeAudio = true, Audio = new(HostingTestPlatformHelpers.CrossPlatformRootedPath("/audio.wav"), 44, new string('a', 64)) };
        byte[] payload = ProjectImageExportWorkerContract.EncodeInput(Guid.NewGuid(), packageAudio, [1]);
        Assert.Equal([1], ProjectImageExportWorkerContract.DecodeInput(payload).SnapshotBytes);
    }

    private static ProjectImageExportWorkerMetadata Metadata() => new(ProjectImageExportWorkerContract.ExportProfile,
        BackendSnapshotProfiles.StaticLyricsScene, WorkerExportContract.ExecutionProfile, "opaqueMp4", false,
        HostingTestPlatformHelpers.CrossPlatformRootedPath("/candidate.mp4"), null, HostingTestPlatformHelpers.ContractFfmpegPath, HostingTestPlatformHelpers.ContractFfprobePath);

    internal static ProjectImageExportWorkerCapabilities Capabilities() => new("engine", ProjectImageExportWorkerContract.ExportProfile,
        [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.PackageLyricsScene],
        ProjectImagePreviewWorkerContract.ImageDecoderProfile, ProjectImageExportWorkerContract.MaximumInputBytes,
        ProjectImagePreviewWorkerContract.MaximumImageBytes, ProjectImagePreviewWorkerContract.MaximumDimension,
        ProjectImagePreviewWorkerContract.MaximumDecodedBytes, ProjectImagePreviewWorkerContract.MaximumBaseSnapshotBytes,
        ProjectImagePreviewWorkerContract.MaximumSourceValues, WorkerExportContract.MaximumReportBytes,
        WorkerExportContract.MaximumMediaBytes, WorkerExportContract.MaximumAudioBytes, WorkerPackageDiagnosticsContract.Version,
        new("engine", BackendSnapshotProfiles.PackageLyricsScene, 1, "sdk", "shader", new string('a', 64), 1),
        new(true, true, true, EngineIdentity: "engine", SourceProfiles: [BackendSnapshotProfiles.StaticLyricsScene,
            BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.StaticLyricsScenePresets, BackendSnapshotProfiles.PackageLyricsScene],
            AudioProfiles: [BackendAudioInputProfiles.Pcm16Wave, BackendAudioInputProfiles.Mp3, BackendAudioInputProfiles.Flac,
                BackendAudioInputProfiles.Aac, BackendAudioInputProfiles.Mov, BackendAudioInputProfiles.Ogg],
            ExecutionProfiles: [WorkerExportContract.ExecutionProfile, WorkerExportContract.CompressedExecutionProfile]));
}
