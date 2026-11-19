using System.Text.Json.Serialization;

namespace MakoKaraGenerator.Backend.Contracts;

/// <summary>创建固定来源 GPU 预览流；客户端必须拥有来源。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GpuPreviewStreamCreateRequest(PreviewSourceDto Source, PreviewSizeDto Size,
    PreviewAlphaMode AlphaMode, string Api, string ConsumerId, RequestMetadataDto? Metadata = null);

/// <summary>以精确流修订替换固定来源。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GpuPreviewStreamReplaceRequest(string ExpectedStreamRevision, PreviewSourceDto Source,
    RequestMetadataDto? Metadata = null);

/// <summary>以精确流修订更改输出尺寸。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GpuPreviewStreamResizeRequest(string ExpectedStreamRevision, PreviewSizeDto Size,
    RequestMetadataDto? Metadata = null);

/// <summary>以精确流修订停止并排空。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GpuPreviewStreamCloseRequest(string ExpectedStreamRevision, RequestMetadataDto? Metadata = null);

/// <summary>客户端可见的登记状态；alphaMode 选择背景透明度，纹理采用预乘 alpha，transport 标示实际底层传输（dma-buf 或 shm）。</summary>
public sealed record GpuPreviewStreamState(string StreamId, string StreamRevision, string InputGeneration,
    string RegistrationState, PreviewSourceDto Source, PreviewSizeDto Size, PreviewAlphaMode AlphaMode, string Api,
    string ProducerEpoch, string PoolGeneration, int SlotCount, long AllocationBytes,
    long? LastAppliedControlSeq = null, string? FailureCode = null, string TextureAlphaMode = "premultiplied",
    GpuStreamWorkerState? WorkerState = null, string Transport = "dma-buf");
