"""Contract 1.54.0 fixtures for lead-in vector/SVG shapes, blink-wipe animations and CueTargetAboveStart."""
import copy

LEGACY_CONTRACT = "1.53.0"
PREFIX = "lead-in-presentation-"


def fixtures():
    """Tail examples exercising 1.54.0 lead-in presentation and above-start attachment."""
    out = []

    def add(name, schema, value, valid=True):
        out.append({
            "name": PREFIX + name,
            "schema": schema,
            "valid": valid,
            "value": copy.deepcopy(value),
        })

    circle_shape = {
        "kind": "Circle",
        "barMinHeightRatio": 0.45,
        "svgXml": None,
        "svgColorMode": "SilhouetteStyle",
    }
    blink_wipe = {
        "mode": "BlinkThenWipeFill",
        "blinkRatio": 0.35,
        "blinkCount": 2,
        "featherRatio": 0.06,
    }
    svg_xml = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><circle cx="12" cy="12" r="10"/></svg>'
    custom_svg_shape = {
        "kind": "CustomSvg",
        "barMinHeightRatio": 0.45,
        "svgXml": svg_xml,
        "svgColorMode": "SilhouetteStyle",
    }
    base_cmd = {
        "type": "orchestration.setCountdownPresentation",
        "containerId": "countdown",
        "shape": circle_shape,
        "animation": blink_wipe,
    }
    metadata = {
        "expectedRevision": "0",
        "requestId": "lead-in-presentation-fixture",
        "hostInstanceId": "host",
        "validUntilUtc": "2026-11-15T12:00:00Z",
    }

    # Valid command forms, union, edit union, envelope, shape/animation variants
    add("command", "SetCountdownPresentationCommandDtoInput", base_cmd)
    add("command-union", "ProjectCommandDtoInput", base_cmd)
    add("command-edit-union", "ProjectEditDtoInput", base_cmd)
    add("command-envelope", "ProjectCommandRequest", {**metadata, "command": base_cmd})
    add("command-shape-only", "SetCountdownPresentationCommandDtoInput", {**base_cmd, "animation": None})
    add("command-animation-only", "SetCountdownPresentationCommandDtoInput", {**base_cmd, "shape": None})
    add("command-custom-svg", "SetCountdownPresentationCommandDtoInput", {**base_cmd, "shape": custom_svg_shape})
    add("command-custom-svg-original", "SetCountdownPresentationCommandDtoInput",
        {**base_cmd, "shape": {**custom_svg_shape, "svgColorMode": "OriginalColor"}})

    for kind in ("Text", "Circle", "SteppedBars", "Capsule", "Diamond", "Star"):
        add("shape-kind-" + kind, "CountdownShapeSpecDtoInput", {**circle_shape, "kind": kind})
    add("shape-custom-svg-min", "CountdownShapeSpecDtoInput", {**custom_svg_shape, "barMinHeightRatio": 0.15})
    add("shape-bars-max", "CountdownShapeSpecDtoInput", {**circle_shape, "kind": "SteppedBars", "barMinHeightRatio": 1})

    for mode in ("DiscreteSteps", "BlinkThenWipeFill", "BlinkThenErase"):
        add("animation-mode-" + mode, "CountdownAnimationSpecDtoInput", {**blink_wipe, "mode": mode})
    add("animation-wipe-zero-blink-ratio", "CountdownAnimationSpecDtoInput",
        {"mode": "BlinkThenWipeFill", "blinkRatio": 0, "blinkCount": 0, "featherRatio": 0})
    add("animation-erase-max-bounds", "CountdownAnimationSpecDtoInput",
        {"mode": "BlinkThenErase", "blinkRatio": 0.85, "blinkCount": 16, "featherRatio": 0.5})
    add("animation-discrete-zero-count", "CountdownAnimationSpecDtoInput",
        {"mode": "DiscreteSteps", "blinkRatio": 0.35, "blinkCount": 0, "featherRatio": 0})

    # Command negatives
    add("command-both-null", "SetCountdownPresentationCommandDtoInput",
        {**base_cmd, "shape": None, "animation": None}, False)
    for field in ("type", "containerId", "shape", "animation"):
        bad = copy.deepcopy(base_cmd)
        del bad[field]
        add("command-missing-" + field, "SetCountdownPresentationCommandDtoInput", bad, False)
    add("command-extra-field", "SetCountdownPresentationCommandDtoInput", {**base_cmd, "unknown": True}, False)
    add("command-wrong-type", "SetCountdownPresentationCommandDtoInput",
        {**base_cmd, "type": "Orchestration.setCountdownPresentation"}, False)
    for suffix, container_id in (("blank", " \t"), ("null", None), ("long", "x" * 129)):
        add("command-container-" + suffix, "SetCountdownPresentationCommandDtoInput",
            {**base_cmd, "containerId": container_id}, False)

    # Shape negatives
    add("shape-custom-svg-null-xml", "CountdownShapeSpecDtoInput", {**custom_svg_shape, "svgXml": None}, False)
    add("shape-custom-svg-blank-xml", "CountdownShapeSpecDtoInput", {**custom_svg_shape, "svgXml": "   "}, False)
    add("shape-circle-nonnull-xml", "CountdownShapeSpecDtoInput", {**circle_shape, "svgXml": "<svg/>"}, False)
    add("shape-invalid-kind", "CountdownShapeSpecDtoInput", {**circle_shape, "kind": "Hexagon"}, False)
    add("shape-invalid-color-mode", "CountdownShapeSpecDtoInput",
        {**custom_svg_shape, "svgColorMode": "Tint"}, False)
    add("shape-ratio-too-low", "CountdownShapeSpecDtoInput", {**circle_shape, "barMinHeightRatio": 0.14}, False)
    add("shape-ratio-too-high", "CountdownShapeSpecDtoInput", {**circle_shape, "barMinHeightRatio": 1.01}, False)
    for field in circle_shape:
        bad = copy.deepcopy(circle_shape)
        del bad[field]
        add("shape-missing-" + field, "CountdownShapeSpecDtoInput", bad, False)
    add("shape-extra", "CountdownShapeSpecDtoInput", {**circle_shape, "extra": 1}, False)

    # Animation negatives
    add("animation-invalid-mode", "CountdownAnimationSpecDtoInput", {**blink_wipe, "mode": "Fade"}, False)
    add("animation-blink-ratio-negative", "CountdownAnimationSpecDtoInput", {**blink_wipe, "blinkRatio": -0.01}, False)
    add("animation-blink-ratio-too-high", "CountdownAnimationSpecDtoInput", {**blink_wipe, "blinkRatio": 0.86}, False)
    add("animation-blink-count-negative", "CountdownAnimationSpecDtoInput", {**blink_wipe, "blinkCount": -1}, False)
    add("animation-blink-count-too-high", "CountdownAnimationSpecDtoInput", {**blink_wipe, "blinkCount": 17}, False)
    add("animation-blink-count-zero-with-ratio", "CountdownAnimationSpecDtoInput",
        {"mode": "BlinkThenWipeFill", "blinkRatio": 0.35, "blinkCount": 0, "featherRatio": 0.06}, False)
    add("animation-feather-negative", "CountdownAnimationSpecDtoInput", {**blink_wipe, "featherRatio": -0.01}, False)
    add("animation-feather-too-high", "CountdownAnimationSpecDtoInput", {**blink_wipe, "featherRatio": 0.51}, False)
    for field in blink_wipe:
        bad = copy.deepcopy(blink_wipe)
        del bad[field]
        add("animation-missing-" + field, "CountdownAnimationSpecDtoInput", bad, False)
    add("animation-extra", "CountdownAnimationSpecDtoInput", {**blink_wipe, "extra": 1}, False)

    # SetLeadInAttachment CueTargetAboveStart
    attachment_cmd = {
        "type": "orchestration.setLeadInAttachment",
        "branchId": "branch",
        "leadInId": "leadIn",
        "containerId": "countdown",
        "mode": "CueTargetAboveStart",
    }
    add("attachment-above-start", "SetLeadInAttachmentCommandDtoInput", attachment_cmd)
    add("attachment-above-start-wrong-case", "SetLeadInAttachmentCommandDtoInput",
        {**attachment_cmd, "mode": "cueTargetAboveStart"}, False)

    # Orchestration content response DTO & command result DTO
    content = {
        "markNodeIds": ["m1", "m2", "m3"],
        "attachmentMode": "CueTargetAboveStart",
        "shape": circle_shape,
        "animation": blink_wipe,
    }
    add("content-circle", "ProjectOrchestrationContentDto", content)
    add("content-custom-svg", "ProjectOrchestrationContentDto", {**content, "shape": custom_svg_shape})
    bad_shape = copy.deepcopy(circle_shape)
    del bad_shape["svgXml"]
    add("content-shape-missing-svgXml", "ProjectOrchestrationContentDto", {**content, "shape": bad_shape}, False)

    add("result-setCountdownPresentation", "OrchestrationLeadInCommandResultDto", {
        "operation": "setCountdownPresentation",
        "indicatorContainerId": "countdown",
        "changed": True,
    })

    return out
