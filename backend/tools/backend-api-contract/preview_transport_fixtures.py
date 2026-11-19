"""Contract 1.57 multi-device & multi-transport preview stream additions with reversible 1.56 example upgrades."""
import copy

PREFIX = "preview-transport-"


def transform(case, upgrade=True):
    if case.get("schema") != "GpuPreviewStreamState" or not isinstance(case.get("value"), dict):
        return case
    value = copy.deepcopy(case["value"])
    if upgrade:
        if "transport" not in value:
            value["transport"] = "dma-buf"
    else:
        if value.get("transport") == "dma-buf":
            del value["transport"]
    return {**case, "value": value}


def migrate_cases(cases):
    return [transform(case, upgrade=True) for case in cases]


def restore_cases(cases):
    return [transform(case, upgrade=False) for case in cases]


def fixtures():
    cases = []
    metadata = dict(requestId="preview-transport-example", hostInstanceId="host", validUntilUtc="2030-01-01T00:00:00Z")
    source = dict(kind="project", sourceId="project", sourceRevision="1")
    base_create = dict(source=source, size=dict(width=1280, height=720), alphaMode="opaque", api="cpu",
                       consumerId="a" * 32, metadata=metadata)
    base_state = dict(streamId="a" * 32, streamRevision="1", inputGeneration="1", registrationState="ready",
                      source=source, size=dict(width=1280, height=720), alphaMode="opaque", api="cpu",
                      producerEpoch="1", poolGeneration="1", slotCount=3, allocationBytes=11059200,
                      textureAlphaMode="premultiplied", transport="shm")

    def add(name, schema, value, valid=True):
        cases.append(dict(name=PREFIX + name, schema=schema, value=copy.deepcopy(value), valid=valid))

    add("create-cpu", "GpuPreviewStreamCreateRequest", base_create, True)
    add("create-invalid-api-metal", "GpuPreviewStreamCreateRequest", {**base_create, "api": "metal"}, False)
    add("state-cpu-shm", "GpuPreviewStreamState", base_state, True)
    add("state-vulkan-shm", "GpuPreviewStreamState", {**base_state, "api": "vulkan", "transport": "shm"}, True)
    add("state-missing-transport", "GpuPreviewStreamState", {k: v for k, v in base_state.items() if k != "transport"}, False)
    add("state-invalid-transport", "GpuPreviewStreamState", {**base_state, "transport": "pipe"}, False)
    add("state-invalid-api-metal", "GpuPreviewStreamState", {**base_state, "api": "metal"}, False)
    return cases
