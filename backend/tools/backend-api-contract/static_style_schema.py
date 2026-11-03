"""Reviewed static-style wire unions; every narrowing is checked against CLR facts."""
import copy
import sys


def ref(name):
    return {"$ref": "#/components/schemas/" + name}


def obj(properties, required=None):
    return {"type": "object", "properties": properties,
            "required": list(properties) if required is None else required, "additionalProperties": False}


# Canonical property identity, reflected positional record, CLR value type, nullable value.
CHANGES = [
    ("typography.fontFamilyName", "StyleFontFamilyChangeDto", "String", True),
    ("typography.fallbackFamilyNames", "StyleFallbackFamiliesChangeDto", "String[]", False),
    ("typography.fontSizePx", "StyleFontSizeChangeDto", "Single", False),
    ("typography.fontWeight", "StyleFontWeightChangeDto", "Int32", False),
    ("typography.isBold", "StyleBoldChangeDto", "Boolean", False),
    ("typography.isItalic", "StyleItalicChangeDto", "Boolean", False),
    ("typography.letterSpacingPx", "StyleLetterSpacingChangeDto", "Single", False),
    ("unsungFill.material", "StyleUnsungMaterialChangeDto", "StyleMaterialDto", False),
    ("sungFill.material", "StyleSungMaterialChangeDto", "StyleMaterialDto", False),
    ("stroke", "StyleStrokeChangeDto", "StyleStrokeDto", True),
    ("stroke.material", "StyleStrokeMaterialChangeDto", "StyleMaterialDto", False),
    ("stroke.widthPx", "StyleStrokeWidthChangeDto", "Single", False),
    ("stroke.join", "StyleStrokeJoinChangeDto", "StyleStrokeJoin", False),
    ("stroke.position", "StyleStrokePositionChangeDto", "StyleStrokePosition", False),
    ("shadow", "StyleShadowChangeDto", "StyleShadowDto", True),
    ("shadow.color", "StyleShadowColorChangeDto", "StyleColorDto", False),
    ("shadow.opacity", "StyleShadowOpacityChangeDto", "Single", False),
    ("shadow.angleDegrees", "StyleShadowAngleChangeDto", "Double", False),
    ("shadow.distancePx", "StyleShadowDistanceChangeDto", "Single", False),
    ("shadow.blurRadiusPx", "StyleShadowBlurChangeDto", "Single", False),
    ("verticalCenterRatio", "StyleVerticalCenterChangeDto", "Double", False),
    ("horizontalAlignment", "StyleAlignmentChangeDto", "StyleHorizontalAlignment", False),
    ("opacity", "StyleOpacityChangeDto", "Single", False),
    ("scale", "StyleScaleChangeDto", "Single", False),
]
LEGACY_CHANGES = CHANGES.copy()
CHANGES += [
    ("glow", "StyleGlowChangeDto", "StyleGlowDto", True),
    ("glow.color", "StyleGlowColorChangeDto", "StyleColorDto", False),
    ("glow.opacity", "StyleGlowOpacityChangeDto", "Single", False),
    ("glow.blurRadiusPx", "StyleGlowBlurChangeDto", "Single", False),
    ("glow.spreadPx", "StyleGlowSpreadChangeDto", "Single", False),
]
TOKEN_PROPERTIES = [p for p, *_ in CHANGES if not p.startswith("typography.")
                    and p not in ("verticalCenterRatio", "horizontalAlignment", "scale")]
ENUMS = {
    "StyleGradientKind": ["linear", "radial"],
    "StyleGradientCoordinateSpace": ["canvas", "line", "glyph", "progress"],
    "StyleGradientColorSpace": ["linearRgb", "hsv", "oklch"],
    "StyleGradientHuePath": ["shorter", "longer", "increasing", "decreasing"],
    "StyleStrokeJoin": ["miter", "round", "bevel"],
    "StyleStrokePosition": ["outside"],
    "StyleHorizontalAlignment": ["left", "center", "right"],
}
TARGETS = [("project", "ProjectStyleTargetDto", []), ("branch", "BranchStyleTargetDto", ["branchId"]),
           ("line", "LineStyleTargetDto", ["branchId", "lineId"]),
           ("token", "TokenStyleTargetDto", ["branchId", "lineId", "tokenId"])]


def audit(metadata, name, fields, *, base=None, nullable=(), optional=()):
    actual = metadata[name]
    if actual.get("kind") != "record" or actual.get("baseType") != base:
        raise ValueError(f"Unreviewed static style base: {name}")
    properties = {p["name"]: p for p in actual["properties"]}
    if {n: p["type"] for n, p in properties.items()} != fields:
        raise ValueError(f"Unreviewed static style fields/types: {name}")
    for field, prop in properties.items():
        if prop["nullable"] != (field in nullable) or prop["optional"] != (field in optional):
            raise ValueError(f"Unreviewed static style presence/nullability: {name}.{field}")
    return actual


def style_query_parameters():
    return [
        {"name": "expectedRevision", "in": "query", "required": True,
         "schema": {"type": "string", "pattern": r"^[0-9]+$", "x-int64-string": True},
         "description": "精确当前Int64修订，必须唯一；不匹配409。"},
        {"name": "kind", "in": "query", "required": True,
         "schema": {"type": "string", "enum": [kind for kind, _, _ in TARGETS]}},
        *[{"name": field, "in": "query", "schema": {"type": "string", "minLength": 1, "pattern": r"\S"},
           "description": "必须按kind完整提供且唯一；Core稳定ID原值，不trim或新增128字符上限。"}
          for field in ("branchId", "lineId", "tokenId")],
    ]


def extend_static_style_schemas(schemas, metadata, schema_for, command_variants):
    def typed(name, fields, *, nullable=(), optional=(), modes=("response", "request")):
        audit(metadata, name, fields, nullable=nullable, optional=optional)
        for mode in modes:
            component = schema_for(name, mode)["$ref"].split("/")[-1]
            generated = schemas[component]
            # Complete canonical DTOs retain their semantic nulls on both reads and writes.
            properties = {}
            for field, clr in fields.items():
                value = schema_for(clr, mode)
                if field in nullable:
                    value = {"anyOf": [value, {"type": "null"}]}
                properties[field] = value
            schemas[component] = {**obj(properties), "x-clr-type": generated["x-clr-type"]}

    for name, values in ENUMS.items():
        if metadata[name] != {"kind": "enum", "values": values}:
            raise ValueError(f"Unreviewed static style enum: {name}")
        schema_for(name)
    typed("StyleColorDto", {"red": "Byte", "green": "Byte", "blue": "Byte", "alpha": "Byte"})
    typed("StyleGradientStopDto", {"offset": "Double", "color": "StyleColorDto"})
    typed("StyleGradientDto", {"kind": "StyleGradientKind", "angleDegrees": "Double", "centerX": "Double", "centerY": "Double",
          "radiusX": "Double", "radiusY": "Double", "coordinateSpace": "StyleGradientCoordinateSpace",
          "colorSpace": "StyleGradientColorSpace", "huePath": "StyleGradientHuePath", "stops": "StyleGradientStopDto[]"})
    typed("StyleFillDto", {"material": "StyleMaterialDto"}, modes=("response",))
    typed("StyleStrokeDto", {"material": "StyleMaterialDto", "widthPx": "Single", "join": "StyleStrokeJoin", "position": "StyleStrokePosition"})
    typed("StyleShadowDto", {"color": "StyleColorDto", "opacity": "Single", "angleDegrees": "Double", "distancePx": "Single", "blurRadiusPx": "Single"})
    typed("StyleGlowDto", {"color": "StyleColorDto", "opacity": "Single", "blurRadiusPx": "Single", "spreadPx": "Single"})
    typed("StyleTypographyDto", {"fontFamilyName": "String", "fallbackFamilyNames": "String[]", "fontSizePx": "Single",
          "fontWeight": "Int32", "isBold": "Boolean", "isItalic": "Boolean", "letterSpacingPx": "Single"}, nullable=("fontFamilyName",), modes=("response",))
    typed("CanonicalStyleDto", {"typography": "StyleTypographyDto", "unsungFill": "StyleFillDto", "sungFill": "StyleFillDto",
          "stroke": "StyleStrokeDto", "shadow": "StyleShadowDto", "verticalCenterRatio": "Double",
          "horizontalAlignment": "StyleHorizontalAlignment", "opacity": "Single", "scale": "Single", "glow": "StyleGlowDto"}, nullable=("stroke", "shadow", "glow"), optional=("glow",), modes=("response",))

    audit(metadata, "StyleTargetDto", {"kind": "String"})
    for kind, name, fields in TARGETS:
        audit(metadata, name, {"kind": "String", **{f: "String" for f in fields}}, base="StyleTargetDto")
        for suffix in ("", "Input"):
            schemas[name + suffix] = {**obj({"kind": {"type": "string", "const": kind}, **{
                f: {"type": "string", "minLength": 1, "pattern": r"\S"} for f in fields}}),
                "x-clr-type": "MakoKaraGenerator.Backend.Contracts." + name}
    for suffix in ("", "Input"):
        schemas["StyleTargetDto" + suffix] = {"oneOf": [ref(name + suffix) for _, name, _ in TARGETS]}

    audit(metadata, "StyleMaterialDto", {"kind": "String"})
    materials = [("SolidStyleMaterialDto", "solid", {"color": "StyleColorDto"}),
                 ("GradientStyleMaterialDto", "gradient", {"gradient": "StyleGradientDto"}),
                 ("PackageStyleMaterialDto", "package", {"packageId": "String", "revision": "String", "exportId": "String"})]
    for name, kind, fields in materials:
        audit(metadata, name, {"kind": "String", **fields, **({"parametersJson": "String"} if kind == "package" else {})}, base="StyleMaterialDto", nullable=("parametersJson",) if kind == "package" else (), optional=("parametersJson",) if kind == "package" else ())
        for mode, suffix in (("response", ""), ("request", "Input")):
            # Package identities are output-only, never an accepted input material.
            if mode == "request" and kind == "package":
                continue
            schema = obj({"kind": {"type": "string", "const": kind}, **{field: schema_for(clr, mode) for field, clr in fields.items()}})
            schema["x-clr-type"] = "MakoKaraGenerator.Backend.Contracts." + name
            if kind == "package":
                schema.update(readOnly=True, description="真实只读包身份；不传输参数，不可作为新材质写回。修改未选字段保留原始包及参数。")
            schemas[name + suffix] = schema
    schemas["StyleMaterialDto"] = {"oneOf": [ref(name) for name, _, _ in materials]}
    schemas["StyleMaterialDtoInput"] = {"oneOf": [ref(name + "Input") for name, kind, _ in materials if kind != "package"]}

    # Canonical validation is shared; input-only admission limits must not reject legacy reads.
    for suffix in ("", "Input"):
        schemas["StyleGradientStopDto" + suffix]["properties"]["offset"].update(minimum=0, maximum=1)
        gradient = schemas["StyleGradientDto" + suffix]
        gradient["properties"]["stops"].update(minItems=2)
        gradient["allOf"] = [{"if": {"properties": {"kind": {"const": "radial"}}, "required": ["kind"]},
            "then": {"properties": {"centerX": {"minimum": 0, "maximum": 1}, "centerY": {"minimum": 0, "maximum": 1},
                                    "radiusX": {"exclusiveMinimum": 0}, "radiusY": {"exclusiveMinimum": 0}}}}]
        gradient["description"] = "完整十字段渐变；色标可相等offset，须非降序且首0尾1，关系由Core校验；不排序或归一化。所有数值必须有限。"
        for name, bounds in (("StyleTypographyDto", {"fontSizePx": {"exclusiveMinimum": 0}, "fontWeight": {"minimum": 100, "maximum": 900}}),
                             ("StyleStrokeDto", {"widthPx": {"minimum": 0}}),
                             ("StyleShadowDto", {"opacity": {"minimum": 0, "maximum": 1}, "distancePx": {"minimum": 0}, "blurRadiusPx": {"minimum": 0}}),
                             ("StyleGlowDto", {"opacity": {"minimum": 0, "maximum": 1}, "blurRadiusPx": {"minimum": 0}, "spreadPx": {"minimum": 0}}),
                             ("CanonicalStyleDto", {"verticalCenterRatio": {"minimum": 0, "maximum": 1}, "opacity": {"minimum": 0, "maximum": 1}, "scale": {"exclusiveMinimum": 0}})):
            for field, limits in bounds.items():
                if name + suffix in schemas:
                    schemas[name + suffix]["properties"][field].update(limits)
    schemas["StyleGradientDtoInput"]["properties"]["stops"]["maxItems"] = 256
    font = {"type": "string", "maxLength": 256, "x-max-utf16-code-units": 256,
            "description": "新名称最多256 UTF-16 code units；JSON Schema maxLength仅提供必要的code-point上限。"}

    audit(metadata, "StyleChangeDto", {"property": "String"})
    for property_name, name, clr, nullable in CHANGES:
        audit(metadata, name, {"property": "String", "value": clr}, base="StyleChangeDto", nullable=("value",) if nullable else ())
        for mode, suffix in (("response", ""), ("request", "Input")):
            value = schema_for(clr, mode)
            # Property-level numeric constraints also apply when canonical objects are not sent.
            limits = {"typography.fontSizePx": {"exclusiveMinimum": 0}, "typography.fontWeight": {"minimum": 100, "maximum": 900},
                      "stroke.widthPx": {"minimum": 0}, "shadow.opacity": {"minimum": 0, "maximum": 1},
                      "shadow.distancePx": {"minimum": 0}, "shadow.blurRadiusPx": {"minimum": 0},
                      "glow.opacity": {"minimum": 0, "maximum": 1}, "glow.blurRadiusPx": {"minimum": 0}, "glow.spreadPx": {"minimum": 0},
                      "verticalCenterRatio": {"minimum": 0, "maximum": 1}, "opacity": {"minimum": 0, "maximum": 1}, "scale": {"exclusiveMinimum": 0}}
            value.update(limits.get(property_name, {}))
            if mode == "request" and property_name == "typography.fontFamilyName":
                value = copy.deepcopy(font)
            if mode == "request" and property_name == "typography.fallbackFamilyNames":
                value.update(maxItems=32, uniqueItems=True, items={**copy.deepcopy(font), "minLength": 1, "pattern": r"\S"})
            if nullable:
                value = {"anyOf": [value, {"type": "null"}]}
            if mode == "response" and property_name in ("stroke", "shadow", "glow"):
                value = {"type": "null", "description": "原始local投影仅用whole null关闭效果；非null部分效果按实际存在叶属性展开。"}
            schemas[name + suffix] = {**obj({"property": {"type": "string", "const": property_name}, "value": value}),
                                     "x-clr-type": "MakoKaraGenerator.Backend.Contracts." + name}
    for suffix in ("", "Input"):
        schemas["StyleChangeDto" + suffix] = {"oneOf": [ref(name + suffix) for _, name, _, _ in CHANGES]}
    schemas["StyleTokenChangeInput"] = {"oneOf": [ref(name + "Input") for prop, name, _, _ in CHANGES if prop in TOKEN_PROPERTIES]}

    base_properties = {p["name"]: p["type"] for p in metadata["ProjectCommandDto"]["properties"]}
    for typ, name, extra in (("styles.patch", "PatchStylesCommandDto", {"targets": "StyleTargetDto[]", "changes": "StyleChangeDto[]"}),
                             ("styles.restore", "RestoreStylesCommandDto", {"targets": "StyleTargetDto[]", "properties": "String[]"})):
        audit(metadata, name, {**base_properties, **extra}, base="ProjectCommandDto",
              nullable=("target", "startUs", "endUs", "analysisJobId"), optional=("target", "startUs", "endUs", "analysisJobId"))
        target_kinds = [(kind, target) for kind, target, _ in TARGETS if typ != "styles.restore" or kind != "project"]
        targets = {"oneOf": [{"type": "array", "minItems": 1, "maxItems": 1 if kind == "project" else 1000,
                              "uniqueItems": True, "items": ref(target + "Input")} for kind, target in target_kinds]}
        member = "changes" if typ == "styles.patch" else "properties"
        values = {"type": "array", "minItems": 1, "maxItems": 29,
                  "items": ref("StyleChangeDtoInput") if member == "changes" else {"type": "string", "enum": [p for p, *_ in CHANGES]}}
        if member == "properties":
            values["uniqueItems"] = True
        else:
            values["allOf"] = [{"contains": {"properties": {"property": {"const": prop}}, "required": ["property"]},
                                "minContains": 0, "maxContains": 1} for prop, *_ in CHANGES]
        # Parent/child conflicts are schema-expressible even though array item order is free.
        for parent in ("stroke", "shadow", "glow"):
            selected = lambda condition: {"properties": {"property": condition}, "required": ["property"]} if member == "changes" else condition
            values.setdefault("allOf", []).append({"not": {"allOf": [
                {"contains": selected({"const": parent})}, {"contains": selected({"enum": [p for p, *_ in CHANGES if p.startswith(parent + ".")]})}]}})
        schema = obj({"type": {"type": "string", "const": typ}, "targets": targets, member: values})
        schema["allOf"] = [{"if": {"properties": {"targets": {"contains": {"properties": {"kind": {"const": "token"}}, "required": ["kind"]}}}, "required": ["targets"]},
            "then": {"properties": {member: {"items": ref("StyleTokenChangeInput") if member == "changes" else {"enum": TOKEN_PROPERTIES}}}}}]
        schema["x-clr-type"] = "MakoKaraGenerator.Backend.Contracts." + name
        schema["x-style-editing-limits"] = {"maxIntentBytes": 65536, "maxTargets": 1000, "maxFontUtf16CodeUnits": 256,
            "maxFallbacks": 32, "maxGradientStops": 256, "maxExpandedNewValueBytes": 16777216,
            "maxReadBytes": 1048576, "maxReadItems": 20000,
            "description": "64KiB按完整command DTO canonical JSON计，不含request metadata；patch的完整canonical changes数组字节数×目标数最多16MiB，restore无新增值展开。每个真实改变目标完整GET投影1MiB/20000项且累计16MiB，no-op不复制旧大投影。不是总heap/耗时保证；完整候选及实际回执提交前校验，整批失败不改工程。"}
        schemas[name + "Input"] = schema
        command_variants.append(ref(name + "Input"))

    typed("StyleCommandResultDto", {"changedTargetCount": "Int32"}, modes=("response",))
    schemas["StyleCommandResultDto"]["properties"]["changedTargetCount"].update(minimum=0, maximum=1000)
    style_result = next((p for p in metadata["ProjectCommandResponse"]["properties"] if p["name"] == "styleResult"), {})
    if style_result.get("type") != "StyleCommandResultDto" or not all(style_result.get(key) for key in ("nullable", "optional", "omitNull")):
        raise ValueError("Unreviewed or missing style result response binding")
    schemas["ProjectCommandResponse"]["properties"]["styleResult"]["description"] = "仅styles.patch/restore提供实际changedTargetCount；no-op为0，旧33命令省略。"
    response_fields = {"projectSessionId": "String", "projectRevision": "String", "target": "StyleTargetDto", "baseline": "CanonicalStyleDto",
                       "local": "StyleChangeDto[]", "staticHierarchy": "CanonicalStyleDto", "hasPresetApplications": "Boolean", "resolution": "String", "protocolVersion": "String"}
    audit(metadata, "ProjectStyleResponse", response_fields, optional=("resolution", "protocolVersion"))
    schemas["ProjectStyleResponse"] = {**obj({field: schema_for(clr) for field, clr in response_fields.items()}),
        "x-clr-type": "MakoKaraGenerator.Backend.Contracts.ProjectStyleResponse",
        "description": "baseline完整基线、local仅本层原始存在字段（显式null保留）、staticHierarchy四层静态继承；不是当前帧effective样式。"}
    schemas["ProjectStyleResponse"]["properties"]["resolution"]["const"] = "staticHierarchy"
    schemas["ProjectStyleResponse"]["properties"]["local"]["maxItems"] = 29
    schemas["ProjectStyleResponse"]["allOf"] = [{"if": {"properties": {"target": {"properties": {"kind": {"const": "project"}}, "required": ["kind"]}}, "required": ["target"]},
        "then": {"properties": {"local": {"maxItems": 0}}}}]
    # Format is advisory to most JSON Schema validators. Enforce representable finite CLR ranges
    # only inside the new reviewed style components, leaving all old component bytes unchanged.
    style_components = {"ProjectStyleResponse", "CanonicalStyleDto", "StyleTokenChangeInput"}
    style_components.update(name + suffix for name in metadata if name.startswith(("Style", "SolidStyle", "GradientStyle", "PackageStyle"))
                            or name.endswith("StyleTargetDto") for suffix in ("", "Input"))
    def finite_numbers(node):
        if isinstance(node, dict):
            if node.get("type") == "number" and node.get("format") in ("float", "double"):
                bound = 3.4028234663852886e38 if node["format"] == "float" else sys.float_info.max
                node.setdefault("minimum", -bound)
                node.setdefault("maximum", bound)
            for child in node.values():
                finite_numbers(child)
        elif isinstance(node, list):
            for child in node:
                finite_numbers(child)
    # Set intersection iteration is hash-randomized between Python processes;
    # keep the generated OpenAPI byte-stable so the default drift check is
    # meaningful and reproducible.
    for name in sorted(style_components & schemas.keys()):
        finite_numbers(schemas[name])
