"""Independent positive/negative G21-B/G22-A/G23/G24 fixtures; no Server execution."""
import copy
import json


def cases():
    out = []
    def add(name, schema, value, valid=True):
        out.append(dict(name="basic-orchestration-" + name, schema=schema, valid=valid, value=copy.deepcopy(value)))
    placement = dict(x=-2, y=3, anchor="RightCenter", rotationDegrees=-45, scale=1.5)
    channel = dict(opacity=1, scale=1, offsetXPx=0, offsetYPx=-12, rotationDegrees=0)
    animation = dict(revealMode="Progress", revealProgress=.5, revealDelayUs="0", revealLeadUs="9223372036854775807",
        maxVisibleLines=2, conflictPolicy="QueueUntilSlotFree", retentionPolicy="UntilLineEnd", enterDurationUs="300000",
        exitDurationUs="250000", enterFrom=channel, hold=channel, exitTo=channel, exitEasing="EaseIn", easing="EaseOut")
    transform = dict(offsetXRatio=-2, offsetYRatio=3, rotationDegrees=-30, scale=1.5)
    commands = [
        ("SetOrchestrationNodeGeometryCommandDtoInput", dict(type="orchestration.setNodeGeometry", nodeId="node", placement=placement, referenceRect=dict(widthRatio=2, heightRatio=.5))),
        ("SetOrchestrationLayoutSettingsCommandDtoInput", dict(type="orchestration.setLayoutSettings", containerId="node", safeArea=dict(left=0, top=.1, right=1, bottom=.9), maxWidthRatio=.8)),
        ("SetOrchestrationSlotAnimationCommandDtoInput", dict(type="orchestration.setSlotAnimation", containerId="node", slotAnimation=animation)),
        ("SetOrchestrationProgramAnimationPolicyCommandDtoInput", dict(type="orchestration.setProgramAnimationPolicy", branchId="primary", animationPolicy="Karaoke")),
        ("CreateOrchestrationCountdownCommandDtoInput", dict(type="orchestration.createCountdown", parentId="root", name=None, count=3)),
        ("CreateOrchestrationLeadInCommandDtoInput", dict(type="orchestration.createLeadIn", branchId="primary", leadInId="cue", name=None, targetLineId="line", indicatorContainerId="countdown", durationUs="1000000", enabled=True)),
        ("UpdateOrchestrationLeadInCommandDtoInput", dict(type="orchestration.updateLeadIn", branchId="primary", leadInId="cue", name="next", targetLineId="line", indicatorContainerId="countdown", durationUs="1000000", enabled=False)),
        ("DeleteOrchestrationLeadInCommandDtoInput", dict(type="orchestration.deleteLeadIn", branchId="primary", leadInId="cue")),
        ("SetLineLocalTransformCommandDtoInput", dict(type="orchestration.setLineLocalTransform", branchId="primary", lineId="line", transform=transform)),
    ]
    meta = dict(expectedRevision="0", requestId="fixture", hostInstanceId="host", validUntilUtc="2026-10-18T10:00:00Z")
    for schema, command in commands:
        name = command["type"].split(".")[-1]
        add(name, schema, command)
        add(name + "-envelope", "ProjectCommandRequest", {**meta, "command": command})
        for field in command:
            changed = copy.deepcopy(command); del changed[field]
            add(name + "-missing-" + field, schema, changed, False)
            if field not in ("name", "slotAnimation", "animationPolicy", "transform"):
                changed = copy.deepcopy(command); changed[field] = None
                add(name + "-null-" + field, schema, changed, False)
        for field, value in (("unknown", True), ("target", None), ("startUs", None), ("endUs", "0"), ("analysisJobId", "job")):
            add(name + "-extra-" + field, schema, {**command, field: value}, False)
        for field in ("slotAnimation", "animationPolicy", "transform"):
            if field in command: add(name + "-clear", schema, {**command, field: None})
    for field in animation:
        changed = copy.deepcopy(animation); del changed[field]
        add("animation-missing-" + field, "ProjectOrchestrationSlotAnimationDtoInput", changed, False)
        changed = copy.deepcopy(animation); changed[field] = None
        add("animation-null-" + field, "ProjectOrchestrationSlotAnimationDtoInput", changed, False)
    for field in ("revealDelayUs", "revealLeadUs", "enterDurationUs", "exitDurationUs"):
        for suffix, value in (("number", 0), ("negative", "-1"), ("overflow", "9223372036854775808"), ("decimal", "0.5")):
            add("animation-" + field + "-" + suffix, "ProjectOrchestrationSlotAnimationDtoInput", {**animation, field: value}, False)
    for field in ("enterFrom", "hold", "exitTo"):
        for part in channel:
            changed = copy.deepcopy(animation); del changed[field][part]
            add("animation-" + field + "-missing-" + part, "ProjectOrchestrationSlotAnimationDtoInput", changed, False)
    for name, schema, value, changes in [
        ("animation", "ProjectOrchestrationSlotAnimationDtoInput", animation, {"revealProgress": 1.1, "maxVisibleLines": 0, "revealMode": "unknown", "conflictPolicy": 0, "retentionPolicy": "unknown", "easing": "unknown", "exitEasing": "unknown"}),
        ("channel", "ProjectOrchestrationSceneTransformDtoInput", channel, {"opacity": 1.1, "scale": 0, "offsetXPx": "0", "extra": 1}),
        ("transform", "ProjectLineLocalTransformDtoInput", transform, {"scale": 0, "rotationDegrees": None, "extra": 1}),
        ("placement", "ProjectOrchestrationPlacementDtoInput", placement, {"anchor": "BottomCenter", "scale": 0, "x": None}),
    ]:
        for field, bad in changes.items(): add(name + "-bad-" + field, schema, {**value, field: bad}, False)
    for anchor in ("Center", "TopLeft", "TopCenter", "BottomRight", "LeftCenter", "RightCenter"):
        add("anchor-" + anchor, "ProjectOrchestrationPlacementDtoInput", {**placement, "anchor": anchor})
    for count in (1, 64): add("countdown-count-" + str(count), commands[4][0], {**commands[4][1], "count": count})
    for count in (0, 65, 1.1, "3", None): add("countdown-invalid-" + str(count), commands[4][0], {**commands[4][1], "count": count}, False)
    for duration in ("0", "-1", "9223372036854775808", 1000):
        add("lead-in-duration-" + str(duration), commands[5][0], {**commands[5][1], "durationUs": duration}, False)
    reference = dict(width=1920, height=1080, worldMatrix=dict(a=1, b=0, c=0, d=1, tx=0, ty=0), pivot=dict(x=0, y=0),
                     worldCorners=[dict(x=0, y=0), dict(x=1920, y=0), dict(x=1920, y=1080), dict(x=0, y=1080)])
    geometry = dict(projectSessionId="session", projectRevision="0", nodeId="root", canvasWidth=1920, canvasHeight=1080,
        reference=reference, parentReference=reference, referenceKind="fixedContainerReferenceV1", finalPixelGeometry=False, protocolVersion="1")
    add("geometry-root", "ProjectGeometryReferenceResponse", geometry)
    add("geometry-child", "ProjectGeometryReferenceResponse", {**geometry, "parentId": "root"})
    for field, value in (("referenceKind", "finalPixelGeometry"), ("finalPixelGeometry", True), ("canvasWidth", 0)):
        add("geometry-bad-" + field, "ProjectGeometryReferenceResponse", {**geometry, field: value}, False)
    changed = copy.deepcopy(geometry); changed["reference"]["worldCorners"].pop()
    add("geometry-missing-corner", "ProjectGeometryReferenceResponse", changed, False)
    for field in geometry:
        changed = copy.deepcopy(geometry); del changed[field]
        add("geometry-missing-" + field, "ProjectGeometryReferenceResponse", changed, False)
    for suffix, value in (("set", transform), ("clear", None)):
        response = dict(projectSessionId="session", projectRevision="0", branchId="primary", lineId="line", transform=value, protocolVersion="1")
        add("local-read-" + suffix, "ProjectLineLocalTransformResponse", response)
        add("local-result-" + suffix, "LineLocalTransformCommandResultDto", dict(branchId="primary", lineId="line", transform=value, changed=True))
        del response["transform"]
        add("local-read-missing-transform-" + suffix, "ProjectLineLocalTransformResponse", response, False)
    add("local-result-missing-transform", "LineLocalTransformCommandResultDto", dict(branchId="primary", lineId="line", changed=True), False)
    add("properties-result", "OrchestrationPropertiesCommandResultDto", dict(operation="setNodeGeometry", nodeId="node", changed=True))
    add("policy-result", "OrchestrationPropertiesCommandResultDto", dict(operation="setProgramAnimationPolicy", branchId="primary", changed=False))
    add("countdown-result", "OrchestrationLeadInCommandResultDto", dict(operation="createCountdown", indicatorContainerId="countdown", markNodeIds=["mark"], changed=True))
    add("cue-result", "OrchestrationLeadInCommandResultDto", dict(operation="createLeadIn", branchId="primary", leadInId="cue", indicatorContainerId="countdown", changed=True))
    lead = dict(id="cue", enabled=True, targetLineId="line", indicatorContainerId="countdown", durationUs="1000000", origin="Manual", startUs="0", endUs="1000000")
    add("lead-in-projection", "ProjectOrchestrationLeadInDto", lead)
    add("lead-in-unknown-time", "ProjectOrchestrationLeadInDto", {k:v for k,v in lead.items() if k not in ("startUs", "endUs")})
    layout = dict(kind="Regular", safeArea=dict(left=0, top=0, right=1, bottom=1), maxWidthRatio=1, slotIds=[], slotAnimation=None)
    add("layout-null-animation", "ProjectOrchestrationLayoutDto", layout)
    add("layout-full-animation", "ProjectOrchestrationLayoutDto", {**layout, "slotAnimation": animation})
    del layout["slotAnimation"]; add("layout-missing-animation", "ProjectOrchestrationLayoutDto", layout, False)
    node = dict(id="countdown", placement=placement, referenceRect=dict(widthRatio=1, heightRatio=1), contentKind="countdownSequence", content=dict(markNodeIds=["mark"], attachmentMode="FollowTarget"))
    program = dict(id="program", branchId="primary", rootContainerId="root", defaultContainerId="regular", clips=[], leadIns=[lead])
    add("orchestration-projection", "ProjectOrchestrationResponse", dict(projectSessionId="session", projectRevision="0", nodes=[node], programs=[{**program, "leadInGeneration": None}], projectEnterAnimation=None, protocolVersion="1"))
    for schema, query in (("ProjectGeometryReferenceQuery", dict(expectedRevision="0", nodeId="root")),
                          ("ProjectLineLocalTransformQuery", dict(expectedRevision="0", branchId="primary", lineId="line"))):
        add(schema + "-valid", schema, query)
        for field in query:
            changed = copy.deepcopy(query); del changed[field]
            add(schema + "-missing-" + field, schema, changed, False)
            add(schema + "-null-" + field, schema, {**query, field: None}, False)
        add(schema + "-unknown", schema, {**query, "unknown": "x"}, False)
        add(schema + "-bad-revision", schema, {**query, "expectedRevision": "9223372036854775808"}, False)
    return out


if __name__ == "__main__":
    print(json.dumps({"cases": cases()}, ensure_ascii=False, indent=2))
