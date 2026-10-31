"""Reviewed GPU stream wire shapes; does not establish runtime availability."""
import copy

PREFIX = "gpu-stream-"


def append_to(document):
    document = copy.deepcopy(document)
    cases = [item for item in document["cases"] if not item["name"].startswith(PREFIX)]
    metadata = dict(requestId="gpu-example", hostInstanceId="host", validUntilUtc="2030-01-01T00:00:00Z")
    source = dict(kind="project", sourceId="project", sourceRevision="1")
    create = dict(source=source, size=dict(width=1280, height=720), alphaMode="opaque", api="vulkan",
                  consumerId="a" * 32, metadata=metadata)
    def add(name, schema, value, valid=True):
        cases.append(dict(name=PREFIX + name, schema=schema, value=copy.deepcopy(value), valid=valid))
    add("create", "GpuPreviewStreamCreateRequest", create)
    for key in create:
        add("create-missing-" + key, "GpuPreviewStreamCreateRequest", {k: v for k, v in create.items() if k != key}, False)
    for label, size in [("small", dict(width=63, height=720)), ("large", dict(width=2561, height=720))]:
        add("create-size-" + label, "GpuPreviewStreamCreateRequest", {**create, "size": size}, False)
    for label, value in [("api", {**create, "api": "gpu"}), ("consumer", {**create, "consumerId": "BAD"}),
                         ("enum", {**create, "alphaMode": 1}), ("unknown", {**create, "extra": True})]:
        add("create-invalid-" + label, "GpuPreviewStreamCreateRequest", value, False)
    add("replace", "GpuPreviewStreamReplaceRequest", dict(expectedStreamRevision="1", source=source, metadata=metadata))
    add("resize", "GpuPreviewStreamResizeRequest", dict(expectedStreamRevision="1", size=create["size"], metadata=metadata))
    add("close", "GpuPreviewStreamCloseRequest", dict(expectedStreamRevision="1", metadata=metadata))
    state = dict(streamId="a" * 32, streamRevision="1", inputGeneration="1", registrationState="ready",
                 source=source, size=create["size"], alphaMode="opaque", api="vulkan", producerEpoch="1",
                 poolGeneration="1", slotCount=3, allocationBytes=11059200, textureAlphaMode="premultiplied")
    add("state", "GpuPreviewStreamState", state)
    add("state-1440p", "GpuPreviewStreamState", {**state, "size": dict(width=2560, height=1440),
        "allocationBytes": 44236800})
    add("state-excess-allocation", "GpuPreviewStreamState", {**state, "allocationBytes": 67108865}, False)
    add("state-alpha", "GpuPreviewStreamState", {**state, "textureAlphaMode": "straight"}, False)
    document["cases"] = cases
    return document
