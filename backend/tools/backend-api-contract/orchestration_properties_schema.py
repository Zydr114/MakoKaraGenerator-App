"""Reviewed G21-B/G22-A/G23/G24 wire overlays after current CLR reflection."""
import copy


def ref(name):
    return {"$ref": "#/components/schemas/" + name}


def obj(props, strict=False):
    result = {"type": "object", "properties": props, "required": list(props)}
    if strict:
        result["additionalProperties"] = False
    return result


def nullable(value):
    return {"anyOf": [copy.deepcopy(value), {"type": "null"}]}


def int64_pattern(positive=False):
    limit = "9223372036854775807"
    alternatives = [r"[0-9]{1,18}"]
    for i, digit in enumerate(limit):
        if digit != "0":
            alternatives.append(limit[:i] + ("[0-" + str(int(digit) - 1) + "]" if digit != "1" else "0") + "[0-9]{" + str(len(limit) - i - 1) + "}")
    alternatives.append(limit)
    return (r"^(?=.*[1-9])" if positive else "^") + "0*(?:" + "|".join(alternatives) + ")$"


def extend_orchestration_schemas(schemas, metadata, schema_for, variants, base_command):
    number = {"type": "number", "description": "有限JSON数值；服务拒绝NaN/Infinity。"}
    positive = {**number, "exclusiveMinimum": 0}
    stable = {"type": "string", "minLength": 1, "maxLength": 128, "pattern": r"\S"}
    time = {"type": "string", "pattern": int64_pattern(), "x-int64-string": True,
            "description": "非负Int64十进制微秒字符串；可带前导零，服务canonical化。"}
    duration = {**time, "pattern": int64_pattern(True)}
    placement = copy.deepcopy(schemas["ProjectOrchestrationPlacementDtoInput"])
    definitions = {
        "ProjectOrchestrationReferenceRectDto": {"widthRatio": positive, "heightRatio": positive},
        "ProjectOrchestrationSafeAreaDto": {name: {"type": "number", "minimum": 0, "maximum": 1} for name in ("left", "top", "right", "bottom")},
        "ProjectOrchestrationSceneTransformDto": {"opacity": {"type": "number", "minimum": 0, "maximum": 1},
            "scale": positive, "offsetXPx": number, "offsetYPx": number, "rotationDegrees": number},
        "ProjectLineLocalTransformDto": {"offsetXRatio": number, "offsetYRatio": number, "rotationDegrees": number, "scale": positive},
        "ProjectOrchestrationSlotAnimationDto": {
            "revealMode": {"type": "string", "enum": ["Progress", "TimeAfterPreviousStart", "LeadBeforeNextStart"]},
            "revealProgress": {"type": "number", "minimum": 0, "maximum": 1},
            "revealDelayUs": time, "revealLeadUs": time, "maxVisibleLines": {"type": "integer", "minimum": 1, "maximum": 2147483647},
            "conflictPolicy": {"type": "string", "enum": ["QueueUntilSlotFree", "AllowOverflow", "ReplacePrevious", "Skip"]},
            "retentionPolicy": {"type": "string", "enum": ["UntilLineEnd", "UntilNextReveal", "Immediate"]},
            "enterDurationUs": time, "exitDurationUs": time,
            "enterFrom": ref("ProjectOrchestrationSceneTransformDtoInput"), "hold": ref("ProjectOrchestrationSceneTransformDtoInput"),
            "exitTo": ref("ProjectOrchestrationSceneTransformDtoInput"),
            "exitEasing": {"type": "string", "enum": ["Linear", "EaseIn", "EaseOut", "EaseInOut"]},
            "easing": {"type": "string", "enum": ["Linear", "EaseIn", "EaseOut", "EaseInOut"]}},
    }
    def clr_type(value):
        if "anyOf" in value:
            value = value["anyOf"][0]
        if "$ref" in value:
            return value["$ref"].split("/")[-1].removesuffix("Input")
        return {"number": "Double", "integer": "Int32", "string": "String", "boolean": "Boolean"}[value["type"]]

    for clr, fields in definitions.items():
        schema_for(clr, "request")
        if {p["name"] for p in metadata[clr]["properties"]} != set(fields):
            raise ValueError("Unreviewed orchestration property DTO: " + clr)
        if any(prop["type"] != clr_type(fields[prop["name"]]) or prop["nullable"] or prop["optional"] or prop["defaultValue"] is not None
               for prop in metadata[clr]["properties"]):
            raise ValueError("Unreviewed orchestration property CLR types/nullability/defaults: " + clr)
        schemas[clr + "Input"] = {**obj(copy.deepcopy(fields), strict=True), "x-clr-type": "MakoKaraGenerator.Backend.Contracts." + clr}
    schemas["ProjectOrchestrationSafeAreaDtoInput"]["description"] = "四有限边界0..1，且right>left、bottom>top；跨字段有序关系由Core校验。"
    commands = [
        ("setNodeGeometry", "SetOrchestrationNodeGeometryCommandDto", {"nodeId": stable, "placement": placement, "referenceRect": ref("ProjectOrchestrationReferenceRectDtoInput")}),
        ("setLayoutSettings", "SetOrchestrationLayoutSettingsCommandDto", {"containerId": stable, "safeArea": ref("ProjectOrchestrationSafeAreaDtoInput"), "maxWidthRatio": {"type": "number", "exclusiveMinimum": 0, "maximum": 1}}),
        ("setSlotAnimation", "SetOrchestrationSlotAnimationCommandDto", {"containerId": stable, "slotAnimation": nullable(ref("ProjectOrchestrationSlotAnimationDtoInput"))}),
        ("setProgramAnimationPolicy", "SetOrchestrationProgramAnimationPolicyCommandDto", {"branchId": stable, "animationPolicy": nullable({"type": "string", "enum": ["Karaoke", "Static", "FollowPrimary"]})}),
        ("createCountdown", "CreateOrchestrationCountdownCommandDto", {"parentId": stable, "name": nullable({"type": "string"}), "count": {"type": "integer", "minimum": 1, "maximum": 64}}),
        ("createLeadIn", "CreateOrchestrationLeadInCommandDto", {"branchId": stable, "leadInId": stable, "name": nullable({"type": "string"}), "targetLineId": stable, "indicatorContainerId": stable, "durationUs": duration, "enabled": {"type": "boolean"}}),
        ("updateLeadIn", "UpdateOrchestrationLeadInCommandDto", {"branchId": stable, "leadInId": stable, "name": nullable({"type": "string"}), "targetLineId": stable, "indicatorContainerId": stable, "durationUs": duration, "enabled": {"type": "boolean"}}),
        ("deleteLeadIn", "DeleteOrchestrationLeadInCommandDto", {"branchId": stable, "leadInId": stable}),
        ("setLineLocalTransform", "SetLineLocalTransformCommandDto", {"branchId": stable, "lineId": stable, "transform": nullable(ref("ProjectLineLocalTransformDtoInput"))}),
    ]
    for operation, clr, fields in commands:
        info = metadata[clr]
        if info.get("baseType") != "ProjectCommandDto" or {p["name"] for p in info["properties"]} != set(base_command["properties"]) | set(fields):
            raise ValueError("Unreviewed orchestration command shape: " + clr)
        if any(prop["type"] != ("ProjectOrchestrationPlacementDto" if prop["name"] == "placement" else clr_type(fields[prop["name"]]))
               or prop["optional"] or prop["defaultValue"] is not None
               for prop in info["properties"] if prop["name"] in fields):
            raise ValueError("Unreviewed orchestration command CLR types/defaults: " + clr)
        component = schema_for(clr, "request")["$ref"].split("/")[-1]
        schemas[component] = {**obj({"type": {"type": "string", "const": "orchestration." + operation}, **copy.deepcopy(fields)}, strict=True),
                              "x-clr-type": "MakoKaraGenerator.Backend.Contracts." + clr}
        schemas[component]["x-capability"] = "project-sessions.commands.orchestration." + operation
        variants.append(ref(component))
    # JsonIgnore(Never) response fields must remain present even when null.
    for component in ("ProjectLineLocalTransformResponse", "LineLocalTransformCommandResultDto"):
        schemas[component]["properties"]["transform"] = nullable(ref("ProjectLineLocalTransformDto"))
        if "transform" not in schemas[component]["required"]:
            schemas[component]["required"].append("transform")
    layout = schemas["ProjectOrchestrationLayoutDto"]
    layout["properties"]["slotAnimation"] = nullable(ref("ProjectOrchestrationSlotAnimationDto"))
    layout["required"].append("slotAnimation")
    schemas["ProjectOrchestrationProgramDto"]["required"].append("leadIns")
    for name, ids in (("ProjectGeometryReferenceQuery", ["nodeId"]), ("ProjectLineLocalTransformQuery", ["branchId", "lineId"])):
        schemas[name] = obj({"expectedRevision": {"type": "string", "pattern": int64_pattern(), "x-int64-string": True},
            **{field: {"type": "string", "minLength": 1, "maxLength": 1024 if field == "nodeId" else 128, "pattern": r"\S"} for field in ids}}, strict=True)
    geometry = schemas["ProjectGeometryReferenceResponse"]["properties"]
    geometry["referenceKind"] = {"type": "string", "const": "fixedContainerReferenceV1"}
    geometry["finalPixelGeometry"] = {"type": "boolean", "const": False}
    for field in ("canvasWidth", "canvasHeight"):
        geometry[field]["minimum"] = 1
    for field in ("width", "height"):
        schemas["ProjectGeometryReferenceDto"]["properties"][field]["exclusiveMinimum"] = 0
    schemas["ProjectGeometryReferenceDto"]["properties"]["worldCorners"].update(minItems=4, maxItems=4)
    schemas["OrchestrationPropertiesCommandResultDto"]["properties"]["operation"]["enum"] = ["setNodeGeometry", "setLayoutSettings", "setSlotAnimation", "setProgramAnimationPolicy"]
    schemas["OrchestrationLeadInCommandResultDto"]["properties"]["operation"]["enum"] = ["createCountdown", "createLeadIn", "updateLeadIn", "deleteLeadIn"]
    for component in ("ProjectOrchestrationSlotAnimationDto",):
        for field, value in definitions[component].items():
            schemas[component]["properties"][field] = copy.deepcopy(value)
            if "$ref" in value:
                schemas[component]["properties"][field] = ref(value["$ref"].split("/")[-1].removesuffix("Input"))
    for component in ("ProjectOrchestrationSceneTransformDto", "ProjectLineLocalTransformDto"):
        for field, value in definitions[component].items():
            schemas[component]["properties"][field] = copy.deepcopy(value)
    for field, description in {
        "propertiesResult": "仅G21-B四种属性/动画命令提供；可空nodeId/branchId省略，canonical值通过orchestration读取。",
        "leadInResult": "仅G23四种命令提供；createCountdown返回真实生成容器/标记身份，空身份字段省略。",
        "lineLocalTransformResult": "仅G24命令提供；transform始终出现，显式null表示清除。"}.items():
        schemas["ProjectCommandResponse"]["properties"][field]["description"] = description
