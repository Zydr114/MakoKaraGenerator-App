"""Synthetic strict wire examples for 1.24.0; no HTTP/Worker acceptance claim."""
import copy
import json


def cases():
    out = []

    def add(name, schema, value, valid=True):
        out.append(dict(name="editing-completion-" + name, schema=schema, value=copy.deepcopy(value), valid=valid))

    settings = dict(includeOpening=True, includeLongGaps=True, gapThresholdUs="5000000", durationUs="3000000",
                    indicatorContainerId=None, excludedLineIds=[])
    animation = dict(durationUs="250000", easing="EaseOut", startOpacity=0, startScale=1, startOffsetPx=20)
    color = dict(red=255, green=255, blue=255, alpha=255)
    fill = dict(material=dict(kind="solid", color=color))
    typography = dict(fontFamilyName=None, fallbackFamilyNames=[], fontSizePx=32, fontWeight=400,
                      isBold=False, isItalic=False, letterSpacingPx=0)
    appearance = dict(typography=typography, pendingFill=fill, elapsedFill=fill, stroke=None, shadow=None, opacity=1)
    commands = [
        ("SetOrchestrationLayoutKindCommandDtoInput", dict(type="orchestration.setLayoutKind", containerId="container", kind="Auto", slotIds=[], newSlotCount=0)),
        ("SetLineEnterAnimationCommandDtoInput", dict(type="orchestration.setLineEnterAnimation", branchId="branch", lineId="line", animation=animation)),
        ("SetProjectEnterAnimationCommandDtoInput", dict(type="orchestration.setProjectEnterAnimation", animation=animation)),
        ("SetLeadInGenerationCommandDtoInput", dict(type="orchestration.setLeadInGeneration", branchId="branch", settings=settings)),
        ("PreviewLeadInGenerationCommandDtoInput", dict(type="orchestration.previewLeadInGeneration", branchId="branch", settings=settings)),
        ("ApplyLeadInGenerationCommandDtoInput", dict(type="orchestration.applyLeadInGeneration", branchId="branch", settings=settings, selectedLineIds=["line"])),
        ("DeleteLeadInAndExcludeCommandDtoInput", dict(type="orchestration.deleteLeadInAndExclude", branchId="branch", leadInId="leadIn")),
        ("SetLeadInAttachmentCommandDtoInput", dict(type="orchestration.setLeadInAttachment", branchId="branch", leadInId="leadIn", containerId="countdown", mode="CueTargetText")),
        ("AddOrchestrationMarkCommandDtoInput", dict(type="orchestration.addMark", containerId="countdown", text="3")),
        ("SetOrchestrationMarkTextCommandDtoInput", dict(type="orchestration.setMarkText", nodeId="mark", text="3")),
        ("ReorderOrchestrationMarksCommandDtoInput", dict(type="orchestration.reorderMarks", containerId="countdown", orderedIds=["mark"])),
        ("SpaceOrchestrationMarksCommandDtoInput", dict(type="orchestration.spaceMarks", containerId="countdown")),
        ("SetCountdownAppearanceCommandDtoInput", dict(type="orchestration.setCountdownAppearance", containerId="countdown", appearance=appearance)),
        ("SetMarkAppearanceCommandDtoInput", dict(type="orchestration.setMarkAppearance", nodeId="mark", changes=[dict(property="opacity", value=.5)])),
    ]
    metadata = dict(expectedRevision="0", requestId="fixture", hostInstanceId="host", validUntilUtc="2026-10-18T10:00:00Z")
    for schema, value in commands:
        operation = value["type"].split(".")[-1]
        add(operation, schema, value)
        add(operation + "-union", "ProjectCommandDtoInput", value)
        add(operation + "-envelope", "ProjectCommandRequest", {**metadata, "command": value})
        for field in value:
            bad = copy.deepcopy(value); del bad[field]
            add(operation + "-missing-" + field, schema, bad, False)
        for field, extra in (("unknown", True), ("target", None), ("analysisJobId", None), ("startUs", None), ("endUs", None)):
            add(operation + "-extra-" + field, schema, {**value, field: extra}, False)
        add(operation + "-wrong-type", schema, {**value, "type": "Orchestration." + operation}, False)
        for field in ("branchId", "lineId", "leadInId", "containerId", "nodeId"):
            if field in value:
                for suffix, bad in (("blank", " \t"), ("long", "x" * (4097 if field == "leadInId" else 129)), ("null", None), ("number", 1)):
                    add(operation + "-" + field + "-" + suffix, schema, {**value, field: bad}, False)
    for mode, valid in (("FixedParent", True), ("CueTargetText", True), ("fixedParent", False), (None, False)):
        add("attachment-mode-" + str(mode), commands[7][0], {**commands[7][1], "mode": mode}, valid)
    for schema, command in commands[1:3]:
        add(command["type"] + "-clear", schema, {**command, "animation": None})
    add("set-generation-clear", commands[3][0], {**commands[3][1], "settings": None})
    for schema, command in commands[4:6]:
        add(command["type"] + "-null-settings", schema, {**command, "settings": None}, False)
    for field, bad in (("includeOpening", None), ("durationUs", "0"), ("gapThresholdUs", "-1"),
                       ("indicatorContainerId", " "), ("excludedLineIds", ["line", "line"])):
        add("settings-invalid-" + field, "LeadInGenerationSettingsDtoInput", {**settings, field: bad}, False)
    for field in settings:
        bad = copy.deepcopy(settings); del bad[field]
        add("settings-missing-" + field, "LeadInGenerationSettingsDtoInput", bad, False)
    for field, bad in (("durationUs", "10000001"), ("durationUs", "-1"), ("easing", "easeOut"), ("startOpacity", 2),
                       ("startScale", 0), ("startScale", 4.1), ("startOffsetPx", 10001)):
        add("animation-invalid-" + field + "-" + str(bad), "ProjectLineEnterAnimationDtoInput", {**animation, field: bad}, False)
    for duration in ("0", "+000000", "-0", "10000000"):
        add("animation-duration-" + duration, "ProjectLineEnterAnimationDtoInput", {**animation, "durationUs": duration})
    for field in animation:
        bad = copy.deepcopy(animation); del bad[field]
        add("animation-missing-" + field, "ProjectLineEnterAnimationDtoInput", bad, False)
    layout_schema, layout = commands[0]
    for name, changes, valid in (
        ("sequence-new", dict(kind="SlotSequence", newSlotCount=2), True),
        ("sequence-existing", dict(kind="SlotSequence", slotIds=["slot"], newSlotCount=0), True),
        ("sequence-empty", dict(kind="SlotSequence"), False),
        ("auto-new-slot", dict(newSlotCount=1), False), ("auto-existing-slot", dict(slotIds=["slot"]), False),
        ("sequence-duplicate", dict(kind="SlotSequence", slotIds=["slot", "slot"]), False),
        ("sequence-too-many-new", dict(kind="SlotSequence", newSlotCount=65), False),
        ("wrong-case", dict(kind="auto"), False)):
        add("layout-" + name, layout_schema, {**layout, **changes}, valid)
    for name, command in (("mark", commands[8]), ("mark-text", commands[9])):
        add(name + "-text-max", command[0], {**command[1], "text": "x" * 4096})
        add(name + "-text-over", command[0], {**command[1], "text": "x" * 4097}, False)
    for prop in ("scale", "verticalCenterRatio", "horizontalAlignment"):
        add("mark-disallowed-" + prop, commands[13][0], {**commands[13][1], "changes": [dict(property=prop, value=1)]}, False)
    add("mark-clear-override", commands[13][0], {**commands[13][1], "changes": []})
    for field in appearance:
        bad = copy.deepcopy(appearance); del bad[field]
        add("appearance-missing-" + field, "SceneTextAppearanceDtoInput", bad, False)
    for field, extra in (("scale", 1), ("verticalCenterRatio", .5), ("horizontalAlignment", "center")):
        add("appearance-extra-" + field, "SceneTextAppearanceDtoInput", {**appearance, field: extra}, False)
    for descendants in (False, True):
        add("node-preset-" + str(descendants), "PresetApplicationTargetDtoInput", dict(kind="node", nodeId="node", includeDescendants=descendants))
    node = dict(kind="node", nodeId="node", includeDescendants=True)
    for field in ("nodeId", "includeDescendants"):
        bad = copy.deepcopy(node); del bad[field]
        add("node-preset-missing-" + field, "PresetApplicationTargetDtoInput", bad, False)
        add("node-preset-null-" + field, "PresetApplicationTargetDtoInput", {**node, field: None}, False)
    for field, value in (("branchId", "branch"), ("lineId", "line"), ("tokenId", "token"), ("unknown", None), ("includeDescendants", "true")):
        add("node-preset-invalid-" + field, "PresetApplicationTargetDtoInput", {**node, field: value}, False)
    add("node-preset-null-irrelevant", "PresetApplicationTargetDtoInput", {**node, "branchId": None, "lineId": None, "tokenId": None})
    for kind, fields in (("project", {}), ("branch", dict(branchId="branch")), ("line", dict(branchId="branch", lineId="line")),
                         ("token", dict(branchId="branch", lineId="line", tokenId="token"))):
        add("legacy-preset-null-" + kind, "PresetApplicationTargetDtoInput", dict(kind=kind, nodeId=None, includeDescendants=None, **fields))
        add("legacy-preset-nonnull-node-" + kind, "PresetApplicationTargetDtoInput", dict(kind=kind, nodeId="node", **fields), False)
    request = dict(expectedRevision="0", mode="time", timeUs="0", width=320, height=180, alphaMode="opaque")
    add("measurement-time", "SceneMeasurementRequest", request)
    add("measurement-layout", "SceneMeasurementRequest", {**request, "mode": "layout", "timeUs": None})
    for field in request:
        bad = copy.deepcopy(request); del bad[field]
        add("measurement-missing-" + field, "SceneMeasurementRequest", bad, False)
    for name, changes in (("time-null", dict(timeUs=None)), ("layout-time", dict(mode="layout")), ("mode-case", dict(mode="Time")),
        ("leading-zero", dict(timeUs="00")), ("revision-sign", dict(expectedRevision="+1")), ("negative-time", dict(timeUs="-1")),
        ("zero-width", dict(width=0)), ("width-over", dict(width=1281)), ("height-over", dict(height=721)),
        ("alpha-case", dict(alphaMode="Opaque")), ("metadata", dict(metadata={})), ("extra", dict(unknown=True))):
        add("measurement-invalid-" + name, "SceneMeasurementRequest", {**request, **changes}, False)
    line = dict(projectSessionId="session", projectRevision="0", branchId="branch", lineId="line", localTransform=None,
                localEnterAnimation=None, effectiveEnterAnimation=None, animationSource="none", protocolVersion="1")
    add("line-presentation-none", "ProjectLinePresentationResponse", line)
    add("line-presentation-project", "ProjectLinePresentationResponse", {**line, "effectiveEnterAnimation": animation, "animationSource": "project"})
    for field in ("localTransform", "localEnterAnimation", "effectiveEnterAnimation"):
        bad = copy.deepcopy(line); del bad[field]
        add("line-presentation-missing-" + field, "ProjectLinePresentationResponse", bad, False)
    add("line-presentation-source-invalid", "ProjectLinePresentationResponse", {**line, "animationSource": "default"}, False)
    query = dict(expectedRevision="0", branchId="branch", lineId="line")
    add("line-presentation-query", "ProjectLinePresentationQuery", query)
    for field in query:
        bad = copy.deepcopy(query); del bad[field]
        add("line-presentation-query-missing-" + field, "ProjectLinePresentationQuery", bad, False)
    add("line-presentation-query-extra", "ProjectLinePresentationQuery", {**query, "unknown": "x"}, False)
    program = dict(id="program", branchId="branch", rootContainerId="root", defaultContainerId="container", leadIns=[], leadInGeneration=None)
    orchestration = dict(projectSessionId="session", projectRevision="0", nodes=[], programs=[program], projectEnterAnimation=None, protocolVersion="1")
    add("orchestration-projection", "ProjectOrchestrationResponse", orchestration)
    add("orchestration-empty", "ProjectOrchestrationResponse", {**orchestration, "programs": []})
    bad = copy.deepcopy(orchestration); del bad["projectEnterAnimation"]
    add("orchestration-missing-project-animation", "ProjectOrchestrationResponse", bad, False)
    bad = copy.deepcopy(program); del bad["leadInGeneration"]
    add("program-missing-generation", "ProjectOrchestrationProgramDto", bad, False)
    cue = dict(id="cue", enabled=True, targetLineId="line", indicatorContainerId="countdown", durationUs="3000000", origin="Generated")
    generation = dict(operation="previewLeadInGeneration", branchId="branch", settings=settings, candidates=[], diagnostics=[], selectedLineIds=[], changed=False)
    for status, reason in (("New", "None"), ("Update", "None"), ("Remove", "NotEligible"), ("Preserved", "ExistingManual"), ("Skipped", "MissingLineTime")):
        candidate = dict(targetLineId="line", status=status, reason=reason)
        if status in ("New", "Update"):
            candidate.update(proposed=cue, startUs="0", endUs="3000000", anchor=dict(timeUs="3000000", timingStatus="Confirmed", usesLineTime=True, hasMissingPrefix=False))
        if status in ("Update", "Remove", "Preserved"):
            candidate["existing"] = cue
        add("generation-status-" + status, "LeadInGenerationCommandResultDto", {**generation, "candidates": [candidate]})
    for reason in ("None", "MissingLineTime", "UnknownPrecedingTime", "TooShort", "NotEligible", "ExistingManual", "Disabled", "Excluded", "TimingConflict"):
        add("generation-reason-" + reason, "LeadInGenerationCandidateDto", dict(targetLineId="line", status="Skipped", reason=reason))
    add("generation-invalid-status", "LeadInGenerationCandidateDto", dict(targetLineId="line", status="Suggested", reason="None"), False)
    add("generation-invalid-reason", "LeadInGenerationCandidateDto", dict(targetLineId="line", status="Skipped", reason="Unknown"), False)
    for field in ("candidates", "diagnostics", "selectedLineIds", "changed"):
        bad = copy.deepcopy(generation); del bad[field]
        add("generation-result-missing-" + field, "LeadInGenerationCommandResultDto", bad, False)
    for operation in ("setLayoutKind", "setLineEnterAnimation", "setProjectEnterAnimation", "setLeadInAttachment"):
        add("properties-result-" + operation, "OrchestrationPropertiesCommandResultDto", dict(operation=operation, changed=False))
    element = dict(id="container", kind="container", representative=False, visible=False, opacity=1, geometryStatus="notVisible",
                   worldMatrix=None, pivot=None, worldCorners=[], fixedReference=None, editReference=None)
    measurement = dict(sourceSha256="a" * 64, sampleInputFingerprint="b" * 64, sourceProfile="static-lyrics-scene-v1",
                       samplingProfile="layout-reference-v1", mode="layout", timeUs=None, canvasWidth=1920, canvasHeight=1080,
                       width=320, height=180, alphaMode="transparent", elements=[element], pngSha256="c" * 64, png="iVBORw0KGgo=")
    response = dict(projectSessionId="session", projectRevision="0", measurement=measurement, protocolVersion="1")
    add("measurement-response", "SceneMeasurementResponse", response)
    add("measurement-response-png-array", "SceneMeasurementDataDto", {**measurement, "png": [137, 80, 78, 71]}, False)
    for name, changes in (("time-null", dict(mode="time", samplingProfile="time-v1")),
                          ("profile-mode", dict(samplingProfile="time-v1")), ("private-profile", dict(samplingProfile="line-reference-v1"))):
        add("measurement-response-invalid-" + name, "SceneMeasurementDataDto", {**measurement, **changes}, False)
    for field in ("worldMatrix", "pivot", "fixedReference", "editReference"):
        bad = copy.deepcopy(element); del bad[field]
        add("measurement-element-missing-" + field, "SceneMeasurementElementDto", bad, False)
    for field, bad in (("kind", "token"), ("geometryStatus", "estimated")):
        add("measurement-element-invalid-" + field, "SceneMeasurementElementDto", {**element, field: bad}, False)
    return out


def package_measurement_cases():
    """Contract 1.32.1 correction for already implemented measurement sources."""
    measurement = next(case["value"]["measurement"] for case in cases()
                       if case["name"] == "editing-completion-measurement-response")
    return [{"name": "measurement-source-profile-" + profile, "schema": "SceneMeasurementDataDto",
             "value": {**copy.deepcopy(measurement), "sourceProfile": profile}, "valid": valid}
            for profile, valid in (("package-lyrics-scene-v1", True),
                                   ("package-lyrics-scene-presets-v1", True),
                                   ("package-lyrics-scene-presets-v2", False))]


if __name__ == "__main__":
    print(json.dumps({"cases": cases()}, ensure_ascii=False, indent=2))
