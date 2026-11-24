"""Contract 1.62+ extended shadow (gradient, spreadPx, blur=0) additions and forward upgrades."""
from static_style_schema import PROPERTIES

PREFIX = "shadow-style-"
SHADOW_EXT_PATHS = ("unsung.shadow.gradient", "unsung.shadow.spreadPx", "sung.shadow.gradient", "sung.shadow.spreadPx")
OLD_PATHS = [path for path in PROPERTIES if path not in SHADOW_EXT_PATHS]
OLD_SHADOW_KEYS = {"color", "opacity", "angleDegrees", "distancePx", "blurRadiusPx"}


def is_legacy_shadow(value):
    return isinstance(value, dict) and OLD_SHADOW_KEYS.issubset(value) and ("spreadPx" not in value or "gradient" not in value)


def transform(value):
    if isinstance(value, list):
        result = [transform(item) for item in value]
        if all(isinstance(item, dict) and set(item) == {"property", "sourcePath"} for item in result):
            paths = [item["property"] for item in result]
            if paths == OLD_PATHS:
                sources = {item["property"]: item for item in result}
                for side in ("unsung", "sung"):
                    base = sources[side + ".shadow.color"]["sourcePath"]
                    sources[side + ".shadow.gradient"] = {"property": side + ".shadow.gradient", "sourcePath": base}
                    sources[side + ".shadow.spreadPx"] = {"property": side + ".shadow.spreadPx", "sourcePath": base}
                return [sources[path] for path in PROPERTIES]
        return result
    if not isinstance(value, dict):
        return value
    result = {key: transform(item) for key, item in value.items()}
    if is_legacy_shadow(result):
        result.setdefault("spreadPx", 0)
        result.setdefault("gradient", None)
    return result


def migrate_cases(cases):
    return [{**case, "value": transform(case["value"])} for case in cases]


def fixtures():
    cases = []
    color = {"red": 255, "green": 32, "blue": 32, "alpha": 255}
    blue = {"red": 32, "green": 80, "blue": 255, "alpha": 255}
    gradient = {
        "kind": "linear", "angleDegrees": 90, "centerX": .5, "centerY": .5, "radiusX": .5, "radiusY": .5,
        "coordinateSpace": "line", "colorSpace": "linearRgb", "huePath": "shorter",
        "stops": [{"offset": 0, "color": color}, {"offset": 1, "color": blue}],
    }
    hard_gradient_shadow = {
        "color": color, "gradient": gradient, "opacity": 1, "angleDegrees": 45,
        "distancePx": 6, "blurRadiusPx": 0, "spreadPx": 2.5,
    }
    line = [{"kind": "line", "branchId": "b", "lineId": "l"}]
    token = [{"kind": "token", "branchId": "b", "lineId": "l", "tokenId": "t"}]

    def add(name, schema, value, valid=True):
        cases.append({"name": PREFIX + name, "schema": schema, "value": value, "valid": valid})

    add("whole-hard-gradient", "StyleShadowDtoInput", hard_gradient_shadow)
    add("whole-negative-spread", "StyleShadowDtoInput", {**hard_gradient_shadow, "spreadPx": -1}, False)
    add("whole-missing-gradient", "StyleShadowDtoInput", {k: v for k, v in hard_gradient_shadow.items() if k != "gradient"}, False)
    add("whole-missing-spread", "StyleShadowDtoInput", {k: v for k, v in hard_gradient_shadow.items() if k != "spreadPx"}, False)

    for side in ("unsung", "sung"):
        g_path = side + ".shadow.gradient"
        s_path = side + ".shadow.spreadPx"
        for target, label in ((line, "line"), (token, "token")):
            add(f"{label}-{g_path}-set", "ProjectCommandDtoInput",
                {"type": "styles.patch", "targets": target, "changes": [{"property": g_path, "value": gradient}]})
            add(f"{label}-{g_path}-null", "ProjectCommandDtoInput",
                {"type": "styles.patch", "targets": target, "changes": [{"property": g_path, "value": None}]})
            add(f"{label}-{s_path}", "ProjectCommandDtoInput",
                {"type": "styles.patch", "targets": target, "changes": [{"property": s_path, "value": 2.5}]})
        add("restore-" + g_path, "ProjectCommandDtoInput", {"type": "styles.restore", "targets": line, "properties": [g_path]})
        add("restore-" + s_path, "ProjectCommandDtoInput", {"type": "styles.restore", "targets": line, "properties": [s_path]})
        add("negative-" + s_path, "StyleChangeDtoInput", {"property": s_path, "value": -0.5}, False)
        add("mark-" + g_path, "SetMarkAppearanceCommandDtoInput",
            {"type": "orchestration.setMarkAppearance", "nodeId": "mark", "changes": [{"property": g_path, "value": gradient}]})
        add("mark-" + s_path, "SetMarkAppearanceCommandDtoInput",
            {"type": "orchestration.setMarkAppearance", "nodeId": "mark", "changes": [{"property": s_path, "value": 2}]})

    return cases
