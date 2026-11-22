"""Glow cases and forward wire adaptation for contract verification."""
import copy

GLOW_PROPERTIES = ("glowSpec", "glowColor", "glowOpacity", "glowBlurRadiusPx", "glowSpreadPx")


def migrate_legacy_cases(cases):
    """Ensure pre-1.50 single-state samples carry glow before two-state style migration."""
    result = copy.deepcopy(cases)

    def visit(value):
        if isinstance(value, list):
            for child in value:
                visit(child)
        elif isinstance(value, dict):
            canonical = len({"typography", "unsungFill", "sungFill", "verticalCenterRatio", "horizontalAlignment"} & value.keys()) >= 3
            appearance = len({"typography", "pendingFill", "elapsedFill", "stroke", "shadow"} & value.keys()) >= 4 and ("pendingFill" in value or "elapsedFill" in value)
            if canonical or appearance:
                value.setdefault("glow", None)
            for child in list(value.values()):
                visit(child)
            if "sources" in value and isinstance(value["sources"], list) and value.get("resolution") == "staticPresetHierarchy":
                existing = {item.get("property") for item in value["sources"] if isinstance(item, dict)}
                if not existing.intersection(GLOW_PROPERTIES) and "unsungColor" in existing:
                    value["sources"].extend({"property": name, "sourcePath": "project.baseStyle"} for name in GLOW_PROPERTIES)

    visit(result)
    return result


def fixtures():
    color = {"red": 255, "green": 255, "blue": 255, "alpha": 255}
    glow = {"color": color, "opacity": 1, "blurRadiusPx": 8, "spreadPx": 0}
    values = {"glow": glow, "glow.color": color, "glow.opacity": .5, "glow.blurRadiusPx": 4, "glow.spreadPx": 2}
    cases = []
    for prop, value in values.items():
        change = {"property": prop, "value": value}
        cases.append({"name": "glow-change-" + prop, "schema": "StyleChangeDtoInput", "valid": True, "value": change})
        cases.append({"name": "glow-token-" + prop, "schema": "ProjectCommandDtoInput", "valid": True,
                      "value": {"type": "styles.patch", "targets": [{"kind": "token", "branchId": "b", "lineId": "l", "tokenId": "t"}], "changes": [change]}})
        cases.append({"name": "glow-null-" + prop, "schema": "StyleChangeDtoInput", "valid": prop == "glow", "value": {"property": prop, "value": None}})
    for field in glow:
        cases.append({"name": "glow-missing-" + field, "schema": "StyleGlowDtoInput", "valid": False,
                      "value": {key: value for key, value in glow.items() if key != field}})
    for field, value in (("opacity", 1.1), ("blurRadiusPx", -1), ("spreadPx", -1)):
        cases.append({"name": "glow-invalid-" + field, "schema": "StyleGlowDtoInput", "valid": False, "value": {**glow, field: value}})
    cases.append({"name": "glow-parent-leaf-conflict", "schema": "ProjectCommandDtoInput", "valid": False,
                  "value": {"type": "styles.patch", "targets": [{"kind": "line", "branchId": "b", "lineId": "l"}],
                            "changes": [{"property": "glow", "value": None}, {"property": "glow.opacity", "value": 1}]}})
    return cases
