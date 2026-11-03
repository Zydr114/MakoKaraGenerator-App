"""Reviewed G20–G24 completion wire overlays. Fixtures do not prove Runtime behavior."""
import copy
from orchestration_properties_schema import int64_pattern
from static_style_schema import CHANGES


def ref(name):
    return {"$ref": "#/components/schemas/" + name}


def obj(properties):
    return {"type": "object", "properties": properties, "required": list(properties), "additionalProperties": False}


def nullable(value):
    return {"anyOf": [copy.deepcopy(value), {"type": "null"}]}


def extend_editing_completion_schemas(schemas, metadata, schema_for, variants, base_command):
    target_fields = {"kind": "String", "branchId": "String", "lineId": "String", "tokenId": "String", "nodeId": "String", "includeDescendants": "Boolean", "clipIds": "String[]"}
    target = metadata["PresetApplicationTargetDto"]
    if {p["name"]: p["type"] for p in target["properties"]} != target_fields or any(
            p["nullable"] != (p["name"] != "kind") or p["optional"] != (p["name"] != "kind")
            or p["defaultValue"] is not None for p in target["properties"]):
        raise ValueError("Unreviewed Node preset target CLR fields/types/nullability/defaults")
    stable = {"type": "string", "minLength": 1, "maxLength": 128, "pattern": r"\S"}
    ids = {"type": "array", "items": stable, "maxItems": 1000, "uniqueItems": True}
    time = {"type": "string", "pattern": "^[+]?" + int64_pattern()[1:] + "|^-0+$", "x-int64-string": True}
    positive_time = {**time, "pattern": "^[+]?" + int64_pattern(True)[1:]}
    cue_identity = {**stable, "maxLength": 4096}
    mark_text = {"type": "string", "minLength": 1, "maxLength": 4096, "pattern": r"\S"}

    def dto(name, properties, mode="request"):
        render_field = name == "SceneMeasurementRequest" and any(p["name"] == "renderBackend" for p in metadata[name]["properties"])
        if render_field:
            properties = {**properties, "renderBackend": nullable({"type": "string", "enum": ["cpu", "gpu"]})}
        if set(properties) != {p["name"] for p in metadata[name]["properties"]}:
            raise ValueError("Unreviewed editing DTO fields: " + name)
        expected = {
            "ProjectLineEnterAnimationDto": {"durationUs": "String", "easing": "String", "startOpacity": "Single", "startScale": "Single", "startOffsetPx": "Single"},
            "LeadInGenerationSettingsDto": {"includeOpening": "Boolean", "includeLongGaps": "Boolean", "gapThresholdUs": "String", "durationUs": "String", "indicatorContainerId": "String", "excludedLineIds": "String[]"},
            "SceneTextAppearanceDto": {"typography": "StyleTypographyDto", "pendingFill": "StyleFillDto", "elapsedFill": "StyleFillDto", "stroke": "StyleStrokeDto", "shadow": "StyleShadowDto", "opacity": "Single", "glow": "StyleGlowDto"},
            "SceneMeasurementRequest": {"expectedRevision": "String", "mode": "String", "timeUs": "String", "width": "Int32", "height": "Int32", "alphaMode": "String", "previewId": "String", "expectedPreviewGeneration": "String", "candidateRevision": "String"},
        }[name]
        nullable_fields = {"LeadInGenerationSettingsDto": {"indicatorContainerId"}, "SceneTextAppearanceDto": {"stroke", "shadow", "glow"}, "SceneMeasurementRequest": {"timeUs", "previewId", "expectedPreviewGeneration", "candidateRevision"}}.get(name, set())
        if render_field:
            expected["renderBackend"] = "String"
            nullable_fields.add("renderBackend")
        if any(p["type"] != expected[p["name"]] or p["nullable"] != (p["name"] in nullable_fields)
               or p["optional"] != ((name == "SceneMeasurementRequest" and p["name"] in {"previewId", "expectedPreviewGeneration", "candidateRevision", "renderBackend"}) or (name == "SceneTextAppearanceDto" and p["name"] == "glow"))
               or p["defaultValue"] is not None for p in metadata[name]["properties"]):
            raise ValueError("Unreviewed editing DTO types/nullability/defaults: " + name)
        component = schema_for(name, mode)["$ref"].split("/")[-1]
        schemas[component] = {**obj(copy.deepcopy(properties)), "x-clr-type": "MakoKaraGenerator.Backend.Contracts." + name}
        return ref(component)

    animation_properties = {
        "durationUs": {"type": "string", "pattern": r"^[+]?(?:0*[0-9]{1,7}|0*10000000)$|^-0+$", "x-int64-string": True,
                       "description": "0..10000000微秒；原解析允许前导符号及零，服务canonical化。"},
        "easing": {"type": "string", "enum": ["Linear", "EaseIn", "EaseOut", "EaseInOut"]},
        "startOpacity": {"type": "number", "minimum": 0, "maximum": 1},
        "startScale": {"type": "number", "exclusiveMinimum": 0, "maximum": 4},
        "startOffsetPx": {"type": "number", "minimum": -10000, "maximum": 10000}}
    animation = dto("ProjectLineEnterAnimationDto", animation_properties)
    dto("ProjectLineEnterAnimationDto", animation_properties, "response")
    settings_properties = {"includeOpening": {"type": "boolean"}, "includeLongGaps": {"type": "boolean"},
        "gapThresholdUs": time, "durationUs": positive_time, "indicatorContainerId": nullable(stable), "excludedLineIds": ids}
    settings = dto("LeadInGenerationSettingsDto", settings_properties)
    dto("LeadInGenerationSettingsDto", settings_properties, "response")
    typography = copy.deepcopy(schemas["StyleTypographyDto"])
    typography.pop("x-clr-type", None)
    typography["additionalProperties"] = False
    typography["properties"]["fontFamilyName"] = nullable({"type": "string", "maxLength": 256})
    typography["properties"]["fallbackFamilyNames"].update(maxItems=32, items={"type": "string", "maxLength": 256})
    appearance = dto("SceneTextAppearanceDto", {
        "typography": typography, "pendingFill": obj({"material": ref("StyleMaterialDtoInput")}),
        "elapsedFill": obj({"material": ref("StyleMaterialDtoInput")}),
        "stroke": nullable(ref("StyleStrokeDtoInput")), "shadow": nullable(ref("StyleShadowDtoInput")), "glow": nullable(ref("StyleGlowDtoInput")),
        "opacity": {"type": "number", "minimum": 0, "maximum": 1}})
    dto("SceneTextAppearanceDto", {"typography": ref("StyleTypographyDto"), "pendingFill": ref("StyleFillDto"),
        "elapsedFill": ref("StyleFillDto"), "stroke": nullable(ref("StyleStrokeDto")), "shadow": nullable(ref("StyleShadowDto")), "glow": nullable(ref("StyleGlowDto")),
        "opacity": {"type": "number", "minimum": 0, "maximum": 1}}, "response")
    mark_changes = [ref(clr + "Input") for prop, clr, *_ in CHANGES if prop not in ("scale", "verticalCenterRatio", "horizontalAlignment")]
    schemas["MarkAppearanceChangeInput"] = {"oneOf": mark_changes}
    schemas["MarkAppearanceChange"] = {"oneOf": [ref(clr) for prop, clr, *_ in CHANGES if prop not in ("scale", "verticalCenterRatio", "horizontalAlignment")]}
    schemas["ProjectOrchestrationContentDto"]["properties"]["appearanceOverride"] = {"type": "array", "items": ref("MarkAppearanceChange")}
    commands = [
        ("setLayoutKind", "SetOrchestrationLayoutKindCommandDto", {"containerId": stable, "kind": {"type": "string", "enum": ["Auto", "SlotSequence"]},
            "slotIds": {**ids, "maxItems": 64}, "newSlotCount": {"type": "integer", "minimum": 0, "maximum": 64}}),
        ("setLineEnterAnimation", "SetLineEnterAnimationCommandDto", {"branchId": stable, "lineId": stable, "animation": nullable(animation)}),
        ("setProjectEnterAnimation", "SetProjectEnterAnimationCommandDto", {"animation": nullable(animation)}),
        ("setLeadInGeneration", "SetLeadInGenerationCommandDto", {"branchId": stable, "settings": nullable(settings)}),
        ("previewLeadInGeneration", "PreviewLeadInGenerationCommandDto", {"branchId": stable, "settings": settings}),
        ("applyLeadInGeneration", "ApplyLeadInGenerationCommandDto", {"branchId": stable, "settings": settings, "selectedLineIds": ids}),
        ("deleteLeadInAndExclude", "DeleteLeadInAndExcludeCommandDto", {"branchId": stable, "leadInId": cue_identity}),
        ("setLeadInAttachment", "SetLeadInAttachmentCommandDto", {"branchId": stable, "leadInId": cue_identity, "containerId": stable,
            "mode": {"type": "string", "enum": ["FixedParent", "CueTargetText"]}}),
        ("addMark", "AddOrchestrationMarkCommandDto", {"containerId": stable, "text": mark_text}),
        ("setMarkText", "SetOrchestrationMarkTextCommandDto", {"nodeId": stable, "text": mark_text}),
        ("reorderMarks", "ReorderOrchestrationMarksCommandDto", {"containerId": stable, "orderedIds": {**ids, "maxItems": 64}}),
        ("spaceMarks", "SpaceOrchestrationMarksCommandDto", {"containerId": stable}),
        ("setCountdownAppearance", "SetCountdownAppearanceCommandDto", {"containerId": stable, "appearance": appearance}),
        ("setMarkAppearance", "SetMarkAppearanceCommandDto", {"nodeId": stable, "changes": {"type": "array", "maxItems": 26,
            "items": ref("MarkAppearanceChangeInput"), "description": "完整本层override；空数组清除，property不得重复。"}}),
    ]
    for operation, clr, fields in commands:
        info = metadata[clr]
        if info.get("baseType") != "ProjectCommandDto" or {p["name"] for p in info["properties"]} != set(base_command["properties"]) | set(fields):
            raise ValueError("Unreviewed editing command fields/base: " + clr)
        field_types = {"newSlotCount": "Int32", "slotIds": "String[]", "orderedIds": "String[]", "selectedLineIds": "String[]",
                       "animation": "ProjectLineEnterAnimationDto", "settings": "LeadInGenerationSettingsDto",
                       "appearance": "SceneTextAppearanceDto", "changes": "StyleChangeDto[]"}
        if any(p["type"] != field_types.get(p["name"], "String") or not p["nullable"] or p["optional"]
               or p["defaultValue"] is not None for p in info["properties"] if p["name"] in fields):
            raise ValueError("Unreviewed editing command types/nullability/defaults: " + clr)
        component = schema_for(clr, "request")["$ref"].split("/")[-1]
        schemas[component] = {**obj({"type": {"type": "string", "const": "orchestration." + operation}, **copy.deepcopy(fields)}),
            "x-clr-type": "MakoKaraGenerator.Backend.Contracts." + clr,
            "x-capability": "project-sessions.commands.orchestration." + operation,
            "x-completion-limits": {"maxIntentBytes": 16384 if operation in ("setLayoutKind", "setLineEnterAnimation", "setProjectEnterAnimation") else 65536,
                "maxNodes": 2000, "maxDepth": 64, "maxInputItems": 20000, "maxInputCharacters": 1048576,
                "maxReadBytes": 1048576, "maxReadItems": 20000, "maxAnalyzedLines": 1000,
                "maxMarks": 64, "maxTextUtf16Units": 4096, "receipt": "完整回执提交前按实际配置验证，默认64KiB；无截断。"}}
        if operation == "setLayoutKind":
            schemas[component]["oneOf"] = [{"properties": {"kind": {"const": "Auto"}, "slotIds": {"maxItems": 0}, "newSlotCount": {"const": 0}}},
                {"properties": {"kind": {"const": "SlotSequence"}}, "anyOf": [
                    {"properties": {"slotIds": {"minItems": 1}}}, {"properties": {"newSlotCount": {"minimum": 1}}}]}]
            schemas[component]["description"] = "保留旧节点；Auto清布局slot引用，SlotSequence要求slotIds数量+newSlotCount为1..64，现有slot须唯一直属纯节点。跨字段总数由服务校验。"
        variants.append(ref(component))
    schemas["OrchestrationPropertiesCommandResultDto"]["properties"]["operation"]["enum"] += ["setLayoutKind", "setLineEnterAnimation", "setProjectEnterAnimation", "setLeadInAttachment"]
    schemas["OrchestrationLeadInCommandResultDto"]["properties"]["operation"]["enum"] += [op for op, _, _ in commands if op not in (
        "setLayoutKind", "setLineEnterAnimation", "setProjectEnterAnimation", "setLeadInGeneration", "previewLeadInGeneration", "applyLeadInGeneration", "setLeadInAttachment")]
    schemas["ProjectCommandResponse"]["properties"]["propertiesResult"]["description"] = "G21布局/属性及G24进入动画的真实反馈；canonical值用精确修订读模型读取。"
    schemas["ProjectCommandResponse"]["properties"]["leadInResult"]["description"] = "手工前奏、附着、mark与提示组外观操作的真实反馈。"
    result = schemas["LeadInGenerationCommandResultDto"]
    result["properties"]["operation"]["enum"] = ["setLeadInGeneration", "previewLeadInGeneration", "applyLeadInGeneration"]
    result["properties"]["settings"] = nullable(ref("LeadInGenerationSettingsDto"))
    candidate = schemas["LeadInGenerationCandidateDto"]["properties"]
    candidate["status"]["enum"] = ["New", "Update", "Remove", "Preserved", "Skipped"]
    candidate["reason"]["enum"] = ["None", "MissingLineTime", "UnknownPrecedingTime", "TooShort", "NotEligible", "ExistingManual", "Disabled", "Excluded", "TimingConflict"]
    schemas["LeadInGenerationAnchorDto"]["properties"]["timingStatus"]["enum"] = ["Missing", "Imported", "Estimated", "Edited", "Confirmed", "Invalid"]
    for component, fields in (("ProjectOrchestrationResponse", {"projectEnterAnimation": "ProjectLineEnterAnimationDto"}),
            ("ProjectOrchestrationProgramDto", {"leadInGeneration": "LeadInGenerationSettingsDto"}),
            ("ProjectLinePresentationResponse", {"localTransform": "ProjectLineLocalTransformDto", "localEnterAnimation": "ProjectLineEnterAnimationDto", "effectiveEnterAnimation": "ProjectLineEnterAnimationDto"})):
        for field, typ in fields.items():
            schemas[component]["properties"][field] = nullable(ref(typ))
            if field not in schemas[component]["required"]:
                schemas[component]["required"].append(field)
    schemas["ProjectLinePresentationResponse"]["properties"]["animationSource"]["enum"] = ["line", "project", "none"]
    schemas["ProjectLinePresentationQuery"] = obj({"expectedRevision": {"type": "string", "pattern": int64_pattern(), "x-int64-string": True}, "branchId": stable, "lineId": stable})
    # byte[] uses System.Text.Json's base64 wire, not an array of byte numbers.
    canonical_time = {"type": "string", "pattern": "^(?=0$|[1-9])" + int64_pattern()[1:], "x-int64-string": True,
                      "description": "canonical非负Int64字符串，不接受符号或前导零；Int64上限由服务验证。"}
    measurement = dto("SceneMeasurementRequest", {"expectedRevision": canonical_time, "mode": {"type": "string", "enum": ["time", "layout"]},
        "timeUs": nullable(canonical_time), "width": {"type": "integer", "minimum": 1, "maximum": 1280},
        "height": {"type": "integer", "minimum": 1, "maximum": 720}, "alphaMode": {"type": "string", "enum": ["opaque", "transparent"]},
        "previewId": {"type": "string", "minLength": 1, "maxLength": 256, "pattern": r"\S"},
        "expectedPreviewGeneration": canonical_time,
        "candidateRevision": {"type": "string", "minLength": 1, "maxLength": 256, "pattern": r"\S"}})
    request_schema = schemas[measurement["$ref"].split("/")[-1]]
    candidate_fields = ["previewId", "expectedPreviewGeneration", "candidateRevision"]
    request_schema["required"] = [field for field in request_schema["required"] if field not in [*candidate_fields, "renderBackend"]]
    request_schema["allOf"] = [{"oneOf": [
        {"not": {"anyOf": [{"required": [field]} for field in candidate_fields]}},
        {"required": candidate_fields}]}]
    request_schema["description"] = "固定工程测量或固定编辑候选测量；候选三字段必须同时提供且均非null，不能混用owner/工程/generation/candidateRevision。基础expectedRevision仍须匹配当前工程；该请求只读，不创建预览或历史。"
    schemas[measurement["$ref"].split("/")[-1]]["oneOf"] = [
        {"properties": {"mode": {"const": "time"}, "timeUs": canonical_time}},
        {"properties": {"mode": {"const": "layout"}, "timeUs": {"type": "null"}}}]
    data = schemas["SceneMeasurementDataDto"]["properties"]
    data["png"] = {"type": "string", "contentEncoding": "base64", "contentMediaType": "image/png"}
    for field in ("sourceSha256", "sampleInputFingerprint", "pngSha256"):
        data[field] = {"type": "string", "pattern": "^[0-9a-f]{64}$"}
    data["samplingProfile"]["enum"] = ["layout-reference-v1", "time-v1"]
    data["sourceProfile"]["enum"] = ["static-lyrics-scene-v1", "static-lyrics-scene-analysis-v1", "static-lyrics-scene-presets-v1",
                                         "package-lyrics-scene-v1", "package-lyrics-scene-presets-v1"]
    data["mode"]["enum"] = ["time", "layout"]
    data["alphaMode"]["enum"] = ["opaque", "transparent"]
    data["elements"]["maxItems"] = 4096
    data["timeUs"] = nullable(canonical_time)
    if "timeUs" not in schemas["SceneMeasurementDataDto"]["required"]:
        schemas["SceneMeasurementDataDto"]["required"].append("timeUs")
    schemas["SceneMeasurementDataDto"]["oneOf"] = [
        {"properties": {"mode": {"const": "time"}, "timeUs": canonical_time, "samplingProfile": {"const": "time-v1"}}},
        {"properties": {"mode": {"const": "layout"}, "timeUs": {"type": "null"}, "samplingProfile": {"const": "layout-reference-v1"}}}]
    element = schemas["SceneMeasurementElementDto"]
    element["properties"]["kind"]["enum"] = ["container", "slot", "line", "ruby", "mark"]
    element["properties"]["geometryStatus"]["enum"] = ["notVisible", "emptyReference", "measuredInk"]
    for field, typ in (("worldMatrix", "ProjectGeometryMatrixDto"), ("pivot", "ProjectGeometryPointDto"),
                       ("fixedReference", "ProjectGeometryReferenceDto"), ("editReference", "ProjectGeometryReferenceDto")):
        element["properties"][field] = nullable(ref(typ))
        if field not in element["required"]:
            element["required"].append(field)


def preset_target_union():
    """Known irrelevant nullable ctor slots remain accepted for all existing kinds."""
    stable = {"type": "string", "minLength": 1, "pattern": r"\S"}
    variants = []
    for kind, required in (("project", []), ("branch", ["branchId"]), ("line", ["branchId", "lineId"]),
                           ("token", ["branchId", "lineId", "tokenId"]), ("node", ["nodeId", "includeDescendants"])):
        props = {"kind": {"type": "string", "const": kind}, **{field: {"type": "null"} for field in ("branchId", "lineId", "tokenId", "nodeId", "includeDescendants")}}
        for field in required:
            props[field] = {"type": "boolean"} if field == "includeDescendants" else {**stable, **({"maxLength": 128} if field == "nodeId" else {})}
        variant = obj(props); variant["required"] = ["kind", *required]; variants.append(variant)
    return {"oneOf": variants, "x-clr-type": "MakoKaraGenerator.Backend.Contracts.PresetApplicationTargetDto",
            "description": "node须显式nodeId/includeDescendants；其他已知无关字段仅允许null以保留旧wire兼容。node仅应用完整静态模板，无effects/source/branchInputs。"}
