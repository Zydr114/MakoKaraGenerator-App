"""Contract 1.50.0+ two-state style: forward migration of pre-1.50 slices and new fixtures."""
import copy

from static_style_schema import MARK_PROPERTIES, PROPERTIES as CURRENT_PROPERTIES, TOKEN_PROPERTIES as CURRENT_TOKEN_PROPERTIES

# Ruby layout properties were added in 1.56 and upgraded in ruby_style_fixtures.
PROPERTIES = [p for p in CURRENT_PROPERTIES if not p.startswith("ruby.")]
TOKEN_PROPERTIES = [p for p in CURRENT_TOKEN_PROPERTIES if not p.startswith("ruby.")]

PREFIX = "style-v2-"
FOLLOW = {"kind": "follow"}


class Retired(Exception):
    """The case exercises a surface that no longer exists."""


def leaves(prefix, names):
    return [prefix + "." + name for name in names]


SHADOW_LEAVES = ("color", "opacity", "angleDegrees", "distancePx", "blurRadiusPx")
GLOW_LEAVES = ("color", "opacity", "blurRadiusPx", "spreadPx")
TYPOGRAPHY = ("fontFamilyName", "fallbackFamilyNames", "fontSizePx", "fontWeight", "isBold", "isItalic", "letterSpacingPx")
# Former TemplateProperty names and the paths they selected (the removed LegacyStylePropertyMap.Import).
TEMPLATE_PATHS = {
    **{name: ["typography." + name] for name in TYPOGRAPHY},
    "verticalCenterRatio": ["verticalCenterRatio"], "horizontalAlignment": ["horizontalAlignment"],
    "unsungColor": ["unsung.fill"], "sungColor": ["sung.fill"],
    "unsungGradientSpec": ["unsung.fill"], "sungGradientSpec": ["sung.fill"],
    "strokeColor": ["unsung.strokes", "sung.strokes"], "strokeWidthPx": ["unsung.strokes", "sung.strokes"],
    "strokeSpec": ["unsung.strokes", "sung.strokes"],
    "shadowColor": ["unsung.shadow.color"], "shadowBlurPx": ["unsung.shadow.blurRadiusPx"],
    "shadowSpec": ["unsung.shadow", *leaves("unsung.shadow", SHADOW_LEAVES), "sung.shadow", *leaves("sung.shadow", SHADOW_LEAVES)],
    "opacity": ["opacity"], "scale": ["scale"],
    "glowSpec": ["unsung.glow", *leaves("unsung.glow", GLOW_LEAVES), "sung.glow", *leaves("sung.glow", GLOW_LEAVES)],
    "glowColor": ["unsung.glow.color"], "glowOpacity": ["unsung.glow.opacity"],
    "glowBlurRadiusPx": ["unsung.glow.blurRadiusPx"], "glowSpreadPx": ["unsung.glow.spreadPx"],
}
LEGACY_SOURCES = list(TEMPLATE_PATHS)
LEGACY_TEMPLATE_PROPERTIES = tuple(TEMPLATE_PATHS)
LEGACY_TEMPLATE_TOKEN_PROPERTIES = tuple(p for p in TEMPLATE_PATHS if p not in (*TYPOGRAPHY, "verticalCenterRatio", "horizontalAlignment", "scale"))
# Source of each path in a migrated resolved-style read: the legacy primary property.
SOURCE_OF = {path: legacy for legacy, paths in reversed(list(TEMPLATE_PATHS.items())) for path in paths}
SOURCE_OF.update({"unsung.shadow.color": "shadowColor", "unsung.shadow.blurRadiusPx": "shadowBlurPx",
                  "unsung.glow.color": "glowColor", "unsung.glow.opacity": "glowOpacity",
                  "unsung.glow.blurRadiusPx": "glowBlurRadiusPx", "unsung.glow.spreadPx": "glowSpreadPx",
                  "unsung.fill": "unsungColor", "sung.fill": "sungColor",
                  "unsung.strokes": "strokeSpec", "sung.strokes": "strokeSpec"})
# Former patch paths. Whole stroke became the unsung layer list; its leaves were retired.
CHANGE_PATHS = {"unsungFill.material": "unsung.fill", "sungFill.material": "sung.fill", "stroke": "unsung.strokes",
                "shadow": "unsung.shadow", **{"shadow." + n: "unsung.shadow." + n for n in SHADOW_LEAVES},
                "glow": "unsung.glow", **{"glow." + n: "unsung.glow." + n for n in GLOW_LEAVES},
                **{path: path for path in ("verticalCenterRatio", "horizontalAlignment", "opacity", "scale", *("typography." + n for n in TYPOGRAPHY))}}
RETIRED_PATHS = {"stroke.material", "stroke.widthPx", "stroke.join", "stroke.position"}
# Negative cases whose violation the 1.50.0 shape no longer has: a whole stroke in a local
# read (stroke lists are always whole), a stroke without position, and a template using a
# patch path while template properties were a separate enum.
RETIRED_NAMES = {"style-read-whole-effect-must-be-null-stroke", "style-stroke-missing-position",
                 "template-library-properties-invalid-['typography.fontSizePx']"}
# Contract 1.50.0 admits the sung stroke as material diagnostic slot 3.
VALIDITY = {"package-preview-source-owner-slot-3": True}
assert set(SOURCE_OF) == set(PROPERTIES) - {p for p in PROPERTIES if p.startswith("wipe.")}


def fill(value):
    if not isinstance(value, dict) or set(value) != {"material"}:
        raise Retired("fill wrapper")
    return convert(value["material"])


def strokes(value):
    if value is None:
        return []
    if not isinstance(value, dict):
        return [value]
    if set(value) != {"material", "widthPx", "join", "position"} or value["position"] != "outside":
        raise Retired("stroke position")
    return [{"material": convert(value["material"]), "widthPx": value["widthPx"], "join": value["join"]}]


def appearance(value, *, fill_key, sung_key, outer):
    """Old single-state object -> unsung/pending side plus a sung/elapsed side following it."""
    result = {}
    if "typography" in value:
        result["typography"] = convert(value["typography"])
    unsung = {}
    if fill_key in value:
        unsung["fill"] = fill(value[fill_key])
    if "stroke" in value:
        unsung["strokes"] = strokes(value["stroke"])
    if "shadow" in value:
        unsung["shadow"] = convert(value["shadow"])
    unsung["glow"] = convert(value.get("glow"))
    sung = {}
    if sung_key in value:
        sung["fill"] = fill(value[sung_key])
    sung.update(strokes=dict(FOLLOW), shadow=dict(FOLLOW), glow=dict(FOLLOW))
    result[outer[0]], result[outer[1]] = unsung, sung
    if outer[0] == "unsung":
        result["wipe"] = {"featherRatio": 0, "highlight": None}
    for key in ("verticalCenterRatio", "horizontalAlignment", "opacity", "scale"):
        if key in value:
            result[key] = value[key]
    known = {"typography", fill_key, sung_key, "stroke", "shadow", "glow", "verticalCenterRatio", "horizontalAlignment", "opacity", "scale"}
    result.update({key: convert(item) for key, item in value.items() if key not in known})
    return result


def change(value):
    old = value["property"]
    if old in RETIRED_PATHS:
        raise Retired("stroke leaf")
    if old not in CHANGE_PATHS:
        return {key: convert(item) for key, item in value.items()}
    result = {}
    for key, item in value.items():
        if key == "property":
            result[key] = CHANGE_PATHS[old]
        elif key == "value":
            result[key] = strokes(item) if old == "stroke" else convert(item)
        else:
            result[key] = convert(item)
    return result


def template_paths(names):
    result, seen = [], set()
    for name in names:
        for path in TEMPLATE_PATHS.get(name, [name]):
            # A repeated legacy name stays a duplicate; expansions are de-duplicated.
            if path not in result or name in seen:
                result.append(path)
        seen.add(name)
    return result


def restore_paths(names):
    if any(name in RETIRED_PATHS for name in names):
        raise Retired("stroke leaf")
    return [CHANGE_PATHS.get(name, name) for name in names]


def sources(items):
    if [item.get("property") if isinstance(item, dict) else None for item in items] != LEGACY_SOURCES \
            or any(set(item) != {"property", "sourcePath"} for item in items):
        raise Retired("partial legacy source list")
    legacy = {item["property"]: item["sourcePath"] for item in items}
    return [{"property": path, "sourcePath": legacy[SOURCE_OF[path]] if path in SOURCE_OF else "project.baseStyle"} for path in PROPERTIES]


def convert(value):
    if isinstance(value, list):
        return [convert(item) for item in value]
    if not isinstance(value, dict):
        return value
    if "unsungFill" in value or "sungFill" in value:
        return appearance(value, fill_key="unsungFill", sung_key="sungFill", outer=("unsung", "sung"))
    if "pendingFill" in value or "elapsedFill" in value:
        return appearance(value, fill_key="pendingFill", sung_key="elapsedFill", outer=("pending", "elapsed"))
    if value.get("coordinateSpace") == "progress":
        raise Retired("progress gradient")
    if isinstance(value.get("property"), str) and "sourcePath" not in value:
        return change(value)
    result = {}
    for key, item in value.items():
        if key == "properties" and isinstance(item, list) and all(isinstance(name, str) for name in item):
            result[key] = restore_paths(item) if value.get("type") == "styles.restore" else template_paths(item)
        elif key == "sources" and isinstance(item, list) and value.get("resolution") == "staticPresetHierarchy":
            result[key] = sources(item)
        else:
            result[key] = convert(item)
    return result


def migrate_case(case):
    """The 1.50.0 form of one reviewed case, or None when its subject was removed."""
    if case["name"] in RETIRED_NAMES:
        return None
    try:
        value = convert(case["value"])
    except Retired:
        return None
    return {**case, "value": value, "valid": VALIDITY.get(case["name"], case["valid"])}


def migrate_cases(cases):
    return [migrated for migrated in map(migrate_case, cases) if migrated is not None]


def fixtures():
    """New 1.50.0 cases: 43 paths, follow/set unions, stroke layers, wipe style and path lists."""
    cases = []

    def add(name, schema, value, valid=True):
        cases.append({"name": PREFIX + name, "schema": schema, "valid": valid, "value": copy.deepcopy(value)})

    color = {"red": 255, "green": 240, "blue": 200, "alpha": 255}
    solid = {"kind": "solid", "color": color}
    gradient = {"kind": "gradient", "gradient": {"kind": "linear", "angleDegrees": 90, "centerX": .5, "centerY": .5, "radiusX": .5,
        "radiusY": .5, "coordinateSpace": "glyph", "colorSpace": "oklch", "huePath": "shorter",
        "stops": [{"offset": 0, "color": color}, {"offset": 1, "color": {**color, "red": 0}}]}}
    package = {"kind": "package", "packageId": "ink", "revision": "sha256:" + "a" * 64, "exportId": "brush"}
    stroke = {"material": solid, "widthPx": 4, "join": "round"}
    outer = {"material": gradient, "widthPx": 9, "join": "miter"}
    shadow = {"color": color, "opacity": .6, "angleDegrees": 45, "distancePx": 3, "blurRadiusPx": 2}
    glow = {"color": color, "opacity": .8, "blurRadiusPx": 6, "spreadPx": 1}
    highlight = {"color": color, "widthRatio": .25, "opacity": 1, "blend": "screen"}
    typography = {"fontFamilyName": None, "fallbackFamilyNames": [], "fontSizePx": 48, "fontWeight": 400,
                  "isBold": False, "isItalic": False, "letterSpacingPx": 0}
    style = {"typography": typography,
             "unsung": {"fill": solid, "strokes": [stroke, outer], "shadow": shadow, "glow": None},
             "sung": {"fill": gradient, "strokes": {"kind": "set", "value": [stroke]}, "shadow": {"kind": "set", "value": None}, "glow": dict(FOLLOW)},
             "wipe": {"featherRatio": .2, "highlight": highlight},
             "verticalCenterRatio": .85, "horizontalAlignment": "center", "opacity": 1, "scale": 1}
    values = {"typography.fontFamilyName": "Noto Sans", "typography.fallbackFamilyNames": ["Noto Sans JP"],
              "typography.fontSizePx": 56, "typography.fontWeight": 700, "typography.isBold": True, "typography.isItalic": False,
              "typography.letterSpacingPx": 1.5, "unsung.fill": solid, "sung.fill": gradient,
              "unsung.strokes": [stroke, outer], "sung.strokes": {"kind": "set", "value": []},
              "wipe.featherRatio": .2, "wipe.highlight": highlight, "wipe.highlight.color": color,
              "wipe.highlight.widthRatio": .5, "wipe.highlight.opacity": .7, "wipe.highlight.blend": "add",
              "verticalCenterRatio": .5, "horizontalAlignment": "left", "opacity": .9, "scale": 1.25}
    for side in ("unsung", "sung"):
        values.update({side + ".shadow": shadow if side == "unsung" else {"kind": "set", "value": shadow},
                       side + ".shadow.color": color, side + ".shadow.opacity": .5, side + ".shadow.angleDegrees": 30,
                       side + ".shadow.distancePx": 2, side + ".shadow.blurRadiusPx": 4,
                       side + ".glow": glow if side == "unsung" else dict(FOLLOW),
                       side + ".glow.color": color, side + ".glow.opacity": .5, side + ".glow.blurRadiusPx": 8, side + ".glow.spreadPx": 2})
    assert list(values) != PROPERTIES and set(values) == set(PROPERTIES)
    nullable = {"typography.fontFamilyName", "unsung.shadow", "unsung.glow", "wipe.highlight"}
    line = [{"kind": "line", "branchId": "b", "lineId": "l"}]
    token = [{"kind": "token", "branchId": "b", "lineId": "l", "tokenId": "t"}]
    for prop in PROPERTIES:
        change = {"property": prop, "value": values[prop]}
        add("change-" + prop, "StyleChangeDtoInput", change)
        add("token-" + prop, "ProjectCommandDtoInput", {"type": "styles.patch", "targets": token, "changes": [change]}, prop in TOKEN_PROPERTIES)
        add("null-" + prop, "StyleChangeDtoInput", {"property": prop, "value": None}, prop in nullable)
        add("mark-" + prop, "SetMarkAppearanceCommandDtoInput", {"type": "orchestration.setMarkAppearance", "nodeId": "mark", "changes": [change]},
            prop in MARK_PROPERTIES)
    for old in ("unsungFill.material", "sungFill.material", "stroke", "stroke.widthPx", "stroke.position", "shadow", "glow.color"):
        add("legacy-path-" + old, "StyleChangeDtoInput", {"property": old, "value": None}, False)
        add("legacy-restore-" + old, "ProjectCommandDtoInput", {"type": "styles.restore", "targets": line, "properties": [old]}, False)

    # Local reads: whole unsung/wipe effects appear only as null; sung members as follow or set-null.
    for prop in ("unsung.shadow", "unsung.glow", "wipe.highlight"):
        add("read-whole-null-" + prop, "StyleChangeDto", {"property": prop, "value": None})
        add("read-whole-value-" + prop, "StyleChangeDto", {"property": prop, "value": values[prop]}, False)
    for prop, value in (("sung.shadow", shadow), ("sung.glow", glow)):
        add("read-follow-" + prop, "StyleChangeDto", {"property": prop, "value": dict(FOLLOW)})
        add("read-set-null-" + prop, "StyleChangeDto", {"property": prop, "value": {"kind": "set", "value": None}})
        add("read-set-value-" + prop, "StyleChangeDto", {"property": prop, "value": {"kind": "set", "value": value}}, False)
    add("read-sung-strokes-set", "StyleChangeDto", {"property": "sung.strokes", "value": {"kind": "set", "value": [stroke]}})
    add("read-unsung-strokes-package-layer-0", "StyleChangeDto", {"property": "unsung.strokes", "value": [{**stroke, "material": package}, outer]})
    add("read-unsung-strokes-package-layer-1", "StyleChangeDto", {"property": "unsung.strokes", "value": [stroke, {**outer, "material": package}]}, False)

    # Follow/set unions.
    for name, value, null_ok in (("StyleSungStrokesDtoInput", [stroke], False), ("StyleSungShadowDtoInput", shadow, True), ("StyleSungGlowDtoInput", glow, True)):
        add(name + "-follow", name, dict(FOLLOW))
        add(name + "-set", name, {"kind": "set", "value": value})
        add(name + "-set-null", name, {"kind": "set", "value": None}, null_ok)
        add(name + "-follow-with-value", name, {"kind": "follow", "value": value}, False)
        add(name + "-set-missing-value", name, {"kind": "set"}, False)
        add(name + "-unknown-kind", name, {"kind": "inherit"}, False)
        add(name + "-bare-value", name, value, False)

    # Stroke layers: at most four, innermost first; package identities are read-only.
    for count in range(6):
        add("strokes-" + str(count), "StyleChangeDtoInput", {"property": "unsung.strokes", "value": [stroke] * count}, count <= 4)
    add("strokes-position-removed", "StyleStrokeDtoInput", {**stroke, "position": "outside"}, False)
    add("strokes-package-write", "StyleChangeDtoInput", {"property": "unsung.strokes", "value": [{**stroke, "material": package}]}, False)
    add("strokes-negative-width", "StyleChangeDtoInput", {"property": "unsung.strokes", "value": [{**stroke, "widthPx": -1}]}, False)

    # Wipe style.
    for value, valid in ((0, True), (2, True), (-.1, False), (2.1, False)):
        add("feather-" + str(value), "StyleChangeDtoInput", {"property": "wipe.featherRatio", "value": value}, valid)
    for value, valid in ((0, False), (.01, True), (2, True), (2.5, False)):
        add("highlight-width-" + str(value), "StyleWipeHighlightDtoInput", {**highlight, "widthRatio": value}, valid)
    for blend in ("normal", "screen", "add", "multiply"):
        add("highlight-blend-" + blend, "StyleWipeHighlightDtoInput", {**highlight, "blend": blend}, blend != "multiply")
    add("highlight-opacity-over", "StyleWipeHighlightDtoInput", {**highlight, "opacity": 1.5}, False)
    for field in highlight:
        add("highlight-missing-" + field, "StyleWipeHighlightDtoInput", {k: v for k, v in highlight.items() if k != field}, False)
    add("wipe-missing-highlight", "StyleWipeDtoInput", {"featherRatio": 0}, False)
    for space in ("canvas", "line", "glyph", "progress"):
        add("gradient-space-" + space, "StyleGradientDtoInput", {**gradient["gradient"], "coordinateSpace": space}, space != "progress")

    # Batches: duplicates and whole/leaf conflicts are rejected on both patch and restore.
    leaves_only = [p for p in PROPERTIES if p not in ("unsung.shadow", "sung.shadow", "unsung.glow", "sung.glow", "wipe.highlight")]
    add("patch-all-leaves", "ProjectCommandDtoInput", {"type": "styles.patch", "targets": line,
        "changes": [{"property": p, "value": values[p]} for p in leaves_only]})
    add("patch-duplicate", "ProjectCommandDtoInput", {"type": "styles.patch", "targets": line,
        "changes": [{"property": "opacity", "value": .5}, {"property": "opacity", "value": .6}]}, False)
    add("restore-all-leaves", "ProjectCommandDtoInput", {"type": "styles.restore", "targets": line, "properties": leaves_only})
    add("restore-all-paths", "ProjectCommandDtoInput", {"type": "styles.restore", "targets": line, "properties": PROPERTIES}, False)
    add("restore-token-wipe", "ProjectCommandDtoInput", {"type": "styles.restore", "targets": token, "properties": ["wipe.featherRatio", "sung.strokes"]})
    add("restore-token-typography", "ProjectCommandDtoInput", {"type": "styles.restore", "targets": token, "properties": ["typography.fontSizePx"]}, False)
    for parent in ("unsung.shadow", "sung.shadow", "unsung.glow", "sung.glow", "wipe.highlight"):
        child = next(p for p in PROPERTIES if p.startswith(parent + "."))
        add("patch-conflict-" + parent, "ProjectCommandDtoInput", {"type": "styles.patch", "targets": line,
            "changes": [{"property": parent, "value": values[parent]}, {"property": child, "value": values[child]}]}, False)
        add("restore-conflict-" + parent, "ProjectCommandDtoInput", {"type": "styles.restore", "targets": line, "properties": [parent, child]}, False)
    add("patch-sides-independent", "ProjectCommandDtoInput", {"type": "styles.patch", "targets": line,
        "changes": [{"property": "sung.shadow", "value": dict(FOLLOW)}, {"property": "unsung.shadow.color", "value": color}]})

    # Complete reads.
    add("canonical", "CanonicalStyleDto", style)
    add("canonical-package-fill", "CanonicalStyleDto", {**style, "unsung": {**style["unsung"], "fill": package}})
    for field in style:
        add("canonical-missing-" + field, "CanonicalStyleDto", {k: v for k, v in style.items() if k != field}, False)
    for field in ("unsungFill", "sungFill", "stroke", "shadow", "glow"):
        add("canonical-legacy-" + field, "CanonicalStyleDto", {**style, field: None}, False)
    add("canonical-sung-follow-fill", "CanonicalStyleDto", {**style, "sung": {**style["sung"], "fill": dict(FOLLOW)}}, False)
    local = [{"property": "unsung.strokes", "value": [stroke]}, {"property": "sung.shadow", "value": dict(FOLLOW)},
             {"property": "wipe.highlight.blend", "value": "add"}, {"property": "opacity", "value": .5}]
    response = {"projectSessionId": "session", "projectRevision": "3", "target": line[0], "baseline": style, "local": local,
                "staticHierarchy": style, "hasPresetApplications": False, "resolution": "staticHierarchy", "protocolVersion": "1"}
    add("style-response", "ProjectStyleResponse", response)
    add("style-response-legacy-local", "ProjectStyleResponse", {**response, "local": [{"property": "stroke", "value": None}]}, False)
    context = {"branchId": "branch", "lineId": "line", "tokenId": None, "clipId": None, "containerId": "root", "nodeId": "root",
               "textNodeId": "text:line", "nodePath": ["root", "text:line"], "contextKind": "container"}
    sources = [{"property": p, "sourcePath": "project.baseStyle"} for p in PROPERTIES]
    resolved = {"projectSessionId": "session", "projectRevision": "0", "context": context, "style": style, "sources": sources,
                "resolution": "staticPresetHierarchy", "finalPixelAppearance": False, "protocolVersion": "1"}
    add("resolved", "ProjectResolvedStyleResponse", resolved)
    add("resolved-missing-source", "ProjectResolvedStyleResponse", {**resolved, "sources": sources[:-1]}, False)
    add("resolved-source-order", "ProjectResolvedStyleResponse", {**resolved, "sources": sources[::-1]}, False)
    add("resolved-legacy-source", "ProjectResolvedStyleResponse", {**resolved, "sources": [*sources[:-1], {"property": "glowSpec", "sourcePath": "project.baseStyle"}]}, False)

    # Lead-in appearance: two states without wipe style; static materials only.
    appearance = {"typography": typography, "pending": style["unsung"], "elapsed": style["sung"], "opacity": 1}
    add("appearance", "SceneTextAppearanceDtoInput", appearance)
    add("appearance-wipe", "SceneTextAppearanceDtoInput", {**appearance, "wipe": style["wipe"]}, False)
    add("appearance-package-fill", "SceneTextAppearanceDtoInput", {**appearance, "pending": {**style["unsung"], "fill": package}}, False)
    add("appearance-legacy", "SceneTextAppearanceDtoInput", {"typography": typography, "pendingFill": {"material": solid},
        "elapsedFill": {"material": solid}, "stroke": None, "shadow": None, "glow": None, "opacity": 1}, False)
    mark = {"type": "orchestration.setMarkAppearance", "nodeId": "mark"}
    add("mark-all-leaves", "SetMarkAppearanceCommandDtoInput", {**mark, "changes": [{"property": p, "value": values[p]} for p in MARK_PROPERTIES if p in leaves_only]})
    add("mark-duplicate", "SetMarkAppearanceCommandDtoInput", {**mark, "changes": [{"property": "opacity", "value": .5}] * 2}, False)
    add("mark-conflict", "SetMarkAppearanceCommandDtoInput", {**mark, "changes": [{"property": "sung.glow", "value": dict(FOLLOW)},
        {"property": "sung.glow.color", "value": color}]}, False)

    # Template properties are the same paths; kinds keep their restrictions.
    static_style = {**style, "sung": {**style["sung"], "strokes": dict(FOLLOW)}}
    definition = {"name": "两态", "kind": "line", "style": static_style, "properties": ["unsung.fill", "sung.strokes", "wipe.highlight"]}
    add("template", "StaticTemplateDefinitionDtoInput", definition)
    add("template-all-paths", "StaticTemplateDefinitionDtoInput", {**definition, "properties": PROPERTIES})
    add("template-too-many", "StaticTemplateDefinitionDtoInput", {**definition, "properties": [*PROPERTIES, "opacity"]}, False)
    add("template-legacy-name", "StaticTemplateDefinitionDtoInput", {**definition, "properties": ["unsungColor"]}, False)
    add("template-unknown-path", "StaticTemplateDefinitionDtoInput", {**definition, "properties": ["wipe.progress"]}, False)
    add("template-token-wipe", "StaticTemplateDefinitionDtoInput", {**definition, "kind": "token", "properties": ["wipe.featherRatio", "sung.glow"]})
    add("template-token-scale", "StaticTemplateDefinitionDtoInput", {**definition, "kind": "token", "properties": ["scale"]}, False)
    add("template-composite-alignment", "StaticTemplateDefinitionDtoInput", {**definition, "kind": "composite", "properties": ["horizontalAlignment"]}, False)
    add("template-package-stroke", "StaticTemplateDefinitionDtoInput", {**definition, "style": {**static_style,
        "unsung": {**static_style["unsung"], "strokes": [{**stroke, "material": package}]}}}, False)
    full_package = {**package, "parametersJson": "{}"}
    full = {**definition, "effectRecipes": [], "packages": []}
    add("full-package-layer-0", "FullTemplateDefinitionDtoInput", {**full, "style": {**static_style,
        "unsung": {**static_style["unsung"], "strokes": [{**stroke, "material": full_package}, outer]}}})
    add("full-package-layer-1", "FullTemplateDefinitionDtoInput", {**full, "style": {**static_style,
        "unsung": {**static_style["unsung"], "strokes": [stroke, {**outer, "material": full_package}]}}}, False)
    add("full-sung-package-layer-0", "FullTemplateDefinitionDtoInput", {**full, "style": {**static_style,
        "sung": {**static_style["sung"], "strokes": {"kind": "set", "value": [{**stroke, "material": full_package}]}}}})

    # Material diagnostics: slot 3 is the sung stroke.
    identity = {"packageId": "ink", "revision": "sha256:" + "a" * 64, "exportId": "brush"}
    for slot in (3, 4):
        add("diagnostic-slot-" + str(slot), "BackendDiagnosticSourceOwnerDto", {"path": "lyrics.style", "region": "stroke", "slot": slot,
            "sourceMaterial": identity, "parametersJson": "{}"}, slot == 3)
    return cases
