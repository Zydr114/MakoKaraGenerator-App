"""CPU/GPU wire examples only; hardware and lifecycle evidence are separate."""
import copy
import json
from pathlib import Path

from render_backend_schema import OPTIONAL_REQUESTS, VALUES, migrate_legacy_cases

PREFIX = "render-backend-"


def fixtures(base_cases=None):
    if base_cases is None:
        base_cases = json.loads((Path(__file__).resolve().parents[2] / "web/examples.json").read_text())["cases"]
    cases = []
    def add(name, schema, value, valid=True):
        cases.append(dict(name=PREFIX + name, schema=schema, value=copy.deepcopy(value), valid=valid))
    metadata = dict(requestId="render-backend-example", hostInstanceId="host", validUntilUtc="2030-01-01T00:00:00Z")
    for schema in OPTIONAL_REQUESTS:
        if schema == "PreviewInputRequest":
            continue
        samples = [case["value"] for case in base_cases
                   if case["schema"] == schema and case["valid"] and not case["name"].startswith(PREFIX)]
        if schema == "ProjectEditPreviewRequest":
            sample = dict(expectedRevision="0", edits=[], size=dict(width=320, height=180), metadata=metadata)
        else:
            if not samples:
                raise ValueError("Missing reviewed base request fixture: " + schema)
            sample = copy.deepcopy(samples[0])
        sample.pop("renderBackend", None)
        add(schema + "-startup-or-current-default", schema, sample)
        for selected in [None, *VALUES]:
            add(schema + "-" + str(selected), schema, {**sample, "renderBackend": selected})
        for label, selected in (("empty", ""), ("space", " "), ("upper", "GPU"), ("native-name", "vulkan"),
                                ("auto", "auto"), ("integer", 1), ("bool", True), ("object", {})):
            add(schema + "-invalid-" + label, schema, {**sample, "renderBackend": selected}, False)
    change = dict(expectedPreviewGeneration="1", renderBackend="gpu", metadata=metadata)
    for selected in VALUES:
        add("switch-" + selected, "PreviewRenderBackendRequest", {**change, "renderBackend": selected})
    for key in change:
        add("switch-missing-" + key, "PreviewRenderBackendRequest", {k: v for k, v in change.items() if k != key}, False)
        add("switch-null-" + key, "PreviewRenderBackendRequest", {**change, key: None}, False)
    for selected in ["", "GPU", "vulkan", "auto", 0, False]:
        add("switch-invalid-" + repr(selected), "PreviewRenderBackendRequest", {**change, "renderBackend": selected}, False)
    add("switch-unknown", "PreviewRenderBackendRequest", {**change, "source": {}}, False)
    for generation in [1, "-1", "01", "", None]:
        add("switch-invalid-generation-" + repr(generation), "PreviewRenderBackendRequest",
            {**change, "expectedPreviewGeneration": generation}, False)
    availability = [dict(renderBackend="cpu", available=True, api="raster", device=dict(device="Skia CPU raster")),
        dict(renderBackend="gpu", available=True, api="vulkan", device=dict(vendor="vendor", device="device", driver="driver", apiVersion="1.3"))]
    capable = dict(defaultBackend="cpu", backends=availability)
    add("capabilities", "RenderBackendsResponse", capable)
    add("capabilities-gpu-default", "RenderBackendsResponse", {**capable, "defaultBackend": "gpu"})
    reason = dict(code="taskUnavailable", message="GPU device unavailable", field="renderBackend",
        protocolVersion="1", failureCode="RenderBackendUnavailable")
    unavailable = dict(renderBackend="gpu", available=False, api="vulkan", unavailableReason=reason)
    add("capabilities-gpu-unavailable", "RenderBackendsResponse", {**capable, "backends": [availability[0], unavailable]})
    for label, records in (("missing-cpu", [availability[1]]), ("duplicate-gpu", [availability[1], availability[1]]),
                           ("unknown-backend", [availability[0], {**availability[1], "renderBackend": "vulkan"}]),
                           ("wrong-gpu-api", [availability[0], {**availability[1], "api": "cpu"}]),
                           ("unavailable-no-reason", [availability[0], {k:v for k,v in unavailable.items() if k != "unavailableReason"}]),
                           ("available-no-device", [availability[0], {k:v for k,v in availability[1].items() if k != "device"}]),
                           ("available-null-device", [availability[0], {**availability[1], "device": None}]),
                           ("available-empty-device-name", [availability[0], {**availability[1], "device": {"device": ""}}]),
                           ("available-reason", [availability[0], {**availability[1], "unavailableReason": reason}]),
                           ("unavailable-device", [availability[0], {**unavailable, "device": availability[1]["device"]}]),
                           ("unavailable-null-reason", [availability[0], {**unavailable, "unavailableReason": None}]),
                           ("wrong-cpu-api", [{**availability[0], "api": "vulkan"}, availability[1]])):
        add("capabilities-invalid-" + label, "RenderBackendsResponse", {**capable, "backends": records}, False)
    for schema in ("BackendJobInputDto", "BackendJobInputDtoRaw", "BackendExportInputDto", "BackendExportInputDtoRaw",
                   "AuthorValidationJobInputDto", "ValidationReportDtoRaw", "ExportReportDtoRaw", "SceneMeasurementDataDto"):
        sample = next((case["value"] for case in base_cases if case["schema"] == schema and case["valid"]
                       and not case["name"].startswith(PREFIX)), None)
        if sample is None:
            continue
        sample = {key:value for key,value in sample.items() if key != "renderBackend"}
        add(schema + "-historical-unknown", schema, sample)
        for selected in [None, *VALUES]:
            add(schema + "-echo-" + str(selected), schema, {**sample, "renderBackend": selected})
        add(schema + "-invalid-echo", schema, {**sample, "renderBackend": "vulkan"}, False)
    event = dict(previewId="preview-1", previewGeneration="1", sourceRevision="0", state="ready", renderBackend="gpu")
    add("preview-event-echo", "PreviewEventDto", event)
    add("preview-event-missing-backend", "PreviewEventDto", {k:v for k,v in event.items() if k != "renderBackend"}, False)
    return {"cases": cases}


def append_to(existing):
    from p4_author_fixtures import append_to as append_p4
    base = {**existing, "cases": migrate_legacy_cases(
        [case for case in existing["cases"] if not case["name"].startswith(PREFIX)], historical=True)}
    base = append_p4(base)
    cases = migrate_legacy_cases(base["cases"])
    return {**base, "cases": cases + fixtures(base["cases"])["cases"]}


if __name__ == "__main__":
    print(json.dumps(fixtures(), ensure_ascii=False, indent=2))
