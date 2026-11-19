"""Contract 1.58 continuous preview stream list and quarantine state additions."""
import copy

PREFIX = "gpu-stream-recovery-"


def fixtures():
    cases = []
    source = dict(kind="project", sourceId="project", sourceRevision="1")
    size = dict(width=1280, height=720)
    ready_state = dict(
        streamId="a" * 32,
        streamRevision="1",
        inputGeneration="1",
        registrationState="ready",
        source=source,
        size=size,
        alphaMode="opaque",
        api="vulkan",
        producerEpoch="1",
        poolGeneration="1",
        slotCount=3,
        allocationBytes=11059200,
        textureAlphaMode="premultiplied",
        transport="dma-buf",
    )
    quarantined_state = {
        **ready_state,
        "registrationState": "failed",
        "failureCode": "GpuConsumerCompletionUnknown",
        "quarantined": True,
    }

    def add(name, schema, value, valid=True):
        cases.append(dict(name=PREFIX + name, schema=schema, value=copy.deepcopy(value), valid=valid))

    add("state-quarantined", "GpuPreviewStreamState", quarantined_state, True)
    add("state-invalid-quarantined-type", "GpuPreviewStreamState", {**ready_state, "quarantined": "yes"}, False)
    add("list-empty", "GpuPreviewStreamListResponse", dict(streams=[], protocolVersion="1"), True)
    add("list-active", "GpuPreviewStreamListResponse", dict(streams=[ready_state, quarantined_state], protocolVersion="1"), True)
    add("list-missing-streams", "GpuPreviewStreamListResponse", dict(protocolVersion="1"), False)
    add("list-invalid-protocol", "GpuPreviewStreamListResponse", dict(streams=[], protocolVersion="2"), False)
    return cases
