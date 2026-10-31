#!/usr/bin/env python3
"""Print only incremental style cases; never modify frozen web/examples.json."""
import copy
import json
import sys

from static_style_schema import CHANGES, TARGETS, TOKEN_PROPERTIES


def fixtures():
    cases = []

    def add(name, schema, value, valid=True):
        cases.append({"name": "style-" + name, "schema": schema, "valid": valid, "value": copy.deepcopy(value)})

    color = {"red": 12, "green": 34, "blue": 56, "alpha": 200}
    solid = {"kind": "solid", "color": color}
    gradient = {"kind": "radial", "angleDegrees": 37.5, "centerX": .25, "centerY": .75,
                "radiusX": .4, "radiusY": .6, "coordinateSpace": "glyph", "colorSpace": "oklch", "huePath": "longer",
                "stops": [{"offset": 0, "color": color}, {"offset": .5, "color": color},
                          {"offset": .5, "color": color}, {"offset": 1, "color": color}]}
    gradient_material = {"kind": "gradient", "gradient": gradient}
    package = {"kind": "package", "packageId": "owned-readonly-package", "revision": "a" * 64, "exportId": "material"}
    stroke = {"material": gradient_material, "widthPx": 2.5, "join": "round", "position": "outside"}
    shadow = {"color": color, "opacity": .75, "angleDegrees": 123.5, "distancePx": 4.25, "blurRadiusPx": 8.5}
    baseline = {"typography": {"fontFamilyName": None, "fallbackFamilyNames": [], "fontSizePx": 72,
                  "fontWeight": 450, "isBold": False, "isItalic": False, "letterSpacingPx": -1.25},
                "unsungFill": {"material": solid}, "sungFill": {"material": gradient_material}, "stroke": None, "shadow": None,
                "verticalCenterRatio": .5, "horizontalAlignment": "center", "opacity": 1, "scale": 1}
    values = [None, ["Noto Sans", "Noto Sans CJK JP"], 80.5, 450, True, False, -1.25,
              solid, gradient_material, stroke, gradient_material, 2.25, "bevel", "outside", shadow,
              color, .5, -123.25, 4.5, 8.25, .6, "right", .75, 1.25]
    assert len(values) == len(CHANGES) == 24
    samples = dict(zip((p for p, *_ in CHANGES), values))
    targets = {kind: {"kind": kind, **{field: field + "-owned" for field in fields}} for kind, _, fields in TARGETS}

    def patch(changes, target=None):
        return {"type": "styles.patch", "targets": [target or targets["line"]], "changes": changes}

    for (prop, name, _, nullable), value in zip(CHANGES, values):
        change = {"property": prop, "value": value}
        add("property-" + prop, "StyleChangeDtoInput", change)
        add("command-property-" + prop, "ProjectCommandDtoInput", patch([change]))
        add("read-property-" + prop, "StyleChangeDto", {"property": prop, "value": None} if prop in ("stroke", "shadow") else change)
        if prop in ("stroke", "shadow"):
            add("read-whole-effect-must-be-null-" + prop, "StyleChangeDto", change, False)
        add("missing-value-" + prop, "StyleChangeDtoInput", {"property": prop}, False)
        add("unknown-member-" + prop, "StyleChangeDtoInput", {**change, "extra": True}, False)
        add("property-case-" + prop, "StyleChangeDtoInput", {"property": prop.upper(), "value": value}, False)
        add("null-value-" + prop, "StyleChangeDtoInput", {"property": prop, "value": None}, nullable)
        if not isinstance(value, (dict, list)) and isinstance(value, (int, float, bool)):
            add("string-value-" + prop, "StyleChangeDtoInput", {"property": prop, "value": str(value).lower()}, False)
        if prop not in TOKEN_PROPERTIES:
            add("token-forbidden-" + prop, "ProjectCommandDtoInput", patch([change], targets["token"]), False)
        else:
            add("token-allowed-" + prop, "ProjectCommandDtoInput", patch([change], targets["token"]))
    for kind, target in targets.items():
        add("target-" + kind, "StyleTargetDtoInput", target)
        add("target-read-" + kind, "StyleTargetDto", target)
        add("target-extra-" + kind, "StyleTargetDtoInput", {**target, "unexpected": "x"}, False)
        add("target-case-" + kind, "StyleTargetDtoInput", {**target, "kind": kind.capitalize()}, False)
        for field in (key for key in target if key != "kind"):
            omitted = copy.deepcopy(target); omitted.pop(field)
            add("target-missing-" + kind + "-" + field, "StyleTargetDtoInput", omitted, False)
            add("target-null-" + kind + "-" + field, "StyleTargetDtoInput", {**target, field: None}, False)
            add("target-blank-" + kind + "-" + field, "StyleTargetDtoInput", {**target, field: " \t"}, False)
    add("target-project-foreign-id", "StyleTargetDtoInput", {"kind": "project", "branchId": "x"}, False)
    add("target-legacy-long-id", "StyleTargetDtoInput", {"kind": "branch", "branchId": "b" * 300})
    for name, value in (("solid", solid), ("gradient", gradient_material), ("package", package)):
        add("material-read-" + name, "StyleMaterialDto", value)
        add("material-input-" + name, "StyleMaterialDtoInput", value, name != "package")
        add("material-unknown-" + name, "StyleMaterialDto", {**value, "parameters": {}}, False)
    add("package-local-read", "StyleChangeDto", {"property": "stroke.material", "value": package})
    add("package-local-write-forbidden", "StyleChangeDtoInput", {"property": "stroke.material", "value": package}, False)
    add("package-stroke-write-forbidden", "StyleChangeDtoInput", {"property": "stroke", "value": {**stroke, "material": package}}, False)
    add("material-null", "StyleMaterialDtoInput", None, False)
    add("material-unknown-kind", "StyleMaterialDtoInput", {"kind": "Solid", "color": color}, False)
    add("solid-double-truth", "StyleMaterialDtoInput", {**solid, "gradient": gradient}, False)
    for field in gradient:
        missing = copy.deepcopy(gradient); missing.pop(field)
        add("gradient-missing-" + field, "StyleGradientDtoInput", missing, False)
        add("gradient-null-" + field, "StyleGradientDtoInput", {**gradient, field: None}, False)
    for field in ("kind", "coordinateSpace", "colorSpace", "huePath"):
        for invalid in (0, "0", gradient[field].upper()):
            add("gradient-enum-" + field + "-" + type(invalid).__name__ + "-" + str(invalid), "StyleGradientDtoInput", {**gradient, field: invalid}, False)
    for field in ("red", "green", "blue", "alpha"):
        for invalid in (-1, 256, .5, "1", None):
            add("color-invalid-" + field + "-" + str(invalid), "StyleColorDtoInput", {**color, field: invalid}, False)
        missing = copy.deepcopy(color); missing.pop(field)
        add("color-missing-" + field, "StyleColorDtoInput", missing, False)
    add("gradient-hard-stops", "StyleGradientDtoInput", gradient)
    add("gradient-linear-unused-geometry-preserved", "StyleGradientDtoInput", {**gradient, "kind": "linear", "centerX": -8, "radiusX": 0})
    add("gradient-radial-invalid-center", "StyleGradientDtoInput", {**gradient, "centerX": -1}, False)
    add("gradient-radial-zero-radius", "StyleGradientDtoInput", {**gradient, "radiusY": 0}, False)
    large_stops = [{"offset": i / 256, "color": color} for i in range(257)]
    add("gradient-legacy-257-read", "StyleGradientDto", {**gradient, "stops": large_stops})
    add("gradient-new-257-write", "StyleGradientDtoInput", {**gradient, "stops": large_stops}, False)
    add("font-legacy-long-read", "StyleChangeDto", {"property": "typography.fontFamilyName", "value": "x" * 300})
    add("font-new-long-write", "StyleChangeDtoInput", {"property": "typography.fontFamilyName", "value": "x" * 257}, False)
    add("font-astral-128", "StyleChangeDtoInput", {"property": "typography.fontFamilyName", "value": "🎵" * 128})
    add("fallback-new-33", "StyleChangeDtoInput", {"property": "typography.fallbackFamilyNames", "value": [str(i) for i in range(33)]}, False)
    add("fallback-legacy-33-read", "StyleChangeDto", {"property": "typography.fallbackFamilyNames", "value": [str(i) for i in range(33)]})
    add("fallback-new-duplicate", "StyleChangeDtoInput", {"property": "typography.fallbackFamilyNames", "value": ["same", "same"]}, False)
    add("fallback-empty-clear", "StyleChangeDtoInput", {"property": "typography.fallbackFamilyNames", "value": []})
    add("float32-overflow", "StyleChangeDtoInput", {"property": "scale", "value": 1e39}, False)
    add("weight-not-required-hundreds", "StyleChangeDtoInput", {"property": "typography.fontWeight", "value": 451})
    add("weight-fraction", "StyleChangeDtoInput", {"property": "typography.fontWeight", "value": 450.5}, False)
    for name, value, component in (("stroke", stroke, "StyleStrokeDtoInput"), ("shadow", shadow, "StyleShadowDtoInput")):
        for field in value:
            missing = copy.deepcopy(value); missing.pop(field)
            add(name + "-missing-" + field, component, missing, False)
            add(name + "-null-" + field, component, {**value, field: None}, False)
        add(name + "-extra", component, {**value, "extra": 1}, False)
    for prop, value in (("stroke.join", "Round"), ("stroke.position", "Outside"), ("horizontalAlignment", "Center")):
        add("enum-case-" + prop, "StyleChangeDtoInput", {"property": prop, "value": value}, False)
        add("enum-number-" + prop, "StyleChangeDtoInput", {"property": prop, "value": 0}, False)
    for member in ("stroke", "shadow"):
        add("disable-" + member, "ProjectCommandDtoInput", patch([{"property": member, "value": None}]))
        leaf = "stroke.widthPx" if member == "stroke" else "shadow.opacity"
        for order, changes in enumerate(([{"property": member, "value": None}, {"property": leaf, "value": samples[leaf]}],
                                         [{"property": leaf, "value": samples[leaf]}, {"property": member, "value": None}])):
            add("parent-child-" + member + str(order), "ProjectCommandDtoInput", patch(changes), False)
        add("restore-parent-child-" + member, "ProjectCommandDtoInput", {"type": "styles.restore", "targets": [targets["line"]], "properties": [member, leaf]}, False)
    change = {"property": "opacity", "value": .5}
    add("patch-empty-changes", "ProjectCommandDtoInput", patch([]), False)
    add("patch-missing-changes", "ProjectCommandDtoInput", {"type": "styles.patch", "targets": [targets["line"]]}, False)
    add("patch-null-changes", "ProjectCommandDtoInput", patch(None), False)
    add("patch-duplicate-property-different-value", "ProjectCommandDtoInput", patch([change, {**change, "value": .75}]), False)
    add("patch-unknown-command-member", "ProjectCommandDtoInput", {**patch([change]), "target": None}, False)
    add("patch-no-target", "ProjectCommandDtoInput", {**patch([change]), "targets": []}, False)
    add("patch-duplicate-target", "ProjectCommandDtoInput", {**patch([change]), "targets": [targets["line"], targets["line"]]}, False)
    add("patch-mixed-target-kind", "ProjectCommandDtoInput", {**patch([change]), "targets": [targets["line"], targets["branch"]]}, False)
    add("patch-project-target", "ProjectCommandDtoInput", patch([change], targets["project"]))
    add("patch-1001-targets", "ProjectCommandDtoInput", {**patch([change]), "targets": [{"kind": "branch", "branchId": str(i)} for i in range(1001)]}, False)
    for kind, target in targets.items():
        add("restore-" + kind, "ProjectCommandDtoInput", {"type": "styles.restore", "targets": [target], "properties": ["opacity"]}, kind != "project")
    for name, properties in (("empty", []), ("duplicate", ["opacity", "opacity"]), ("unknown", ["unsungColor"]), ("case", ["Opacity"])):
        add("restore-" + name, "ProjectCommandDtoInput", {"type": "styles.restore", "targets": [targets["line"]], "properties": properties}, False)
    add("restore-token-forbidden-font", "ProjectCommandDtoInput", {"type": "styles.restore", "targets": [targets["token"]], "properties": ["typography.fontSizePx"]}, False)
    add("baseline-null-effect-and-font", "CanonicalStyleDto", baseline)
    for field in ("stroke", "shadow"):
        missing = copy.deepcopy(baseline); missing.pop(field)
        add("baseline-required-null-" + field, "CanonicalStyleDto", missing, False)
    missing = copy.deepcopy(baseline); missing["typography"].pop("fontFamilyName")
    add("baseline-required-null-font", "CanonicalStyleDto", missing, False)
    rich = copy.deepcopy(baseline); rich.update(stroke=stroke, shadow=shadow)
    rich["unsungFill"]["material"] = package
    add("baseline-readonly-package-with-full-effects", "CanonicalStyleDto", rich)
    response = {"projectSessionId": "project-owned", "projectRevision": "9007199254740993", "target": targets["line"],
                "baseline": baseline, "local": [{"property": "typography.fontFamilyName", "value": None}, {"property": "stroke", "value": None}],
                "staticHierarchy": baseline, "hasPresetApplications": True, "resolution": "staticHierarchy", "protocolVersion": "1"}
    add("read-static-hierarchy-explicit-nulls", "ProjectStyleResponse", response)
    add("read-project-local-empty", "ProjectStyleResponse", {**response, "target": targets["project"], "local": []})
    add("read-project-local-not-overlay", "ProjectStyleResponse", {**response, "target": targets["project"]}, False)
    for field in response:
        missing = copy.deepcopy(response); missing.pop(field)
        add("read-missing-" + field, "ProjectStyleResponse", missing, False)
    add("read-not-current-frame", "ProjectStyleResponse", {**response, "resolution": "effective"}, False)
    add("read-numeric-revision", "ProjectStyleResponse", {**response, "projectRevision": 1}, False)
    add("result-noop", "StyleCommandResultDto", {"changedTargetCount": 0})
    add("result-changed", "StyleCommandResultDto", {"changedTargetCount": 1000})
    add("result-negative", "StyleCommandResultDto", {"changedTargetCount": -1}, False)
    add("result-over-limit", "StyleCommandResultDto", {"changedTargetCount": 1001}, False)
    assert len({c["name"] for c in cases}) == len(cases)
    return cases


if __name__ == "__main__":
    json.dump({"cases": fixtures()}, sys.stdout, ensure_ascii=False, indent=2)
    sys.stdout.write("\n")
