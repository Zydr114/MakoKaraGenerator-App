"""Reviewed P3 package inspection, full template definitions and workshop workflow."""
import copy
from static_style_schema import audit, followable, obj, ref, strokes_array
from template_library_schema import IDENTIFIER, REVISION, PROPERTIES, definition_constraints
from editing_completion_schema import preset_target_union

TEXT = copy.deepcopy(IDENTIFIER)
PACKAGE_REVISION = {"type": "string", "minLength": 1, "pattern": r"\S", "description": "固定包内容修订；服务与实际包身份比较，不解析为数字。"}
JSON_TEXT = {"type": "string", "contentMediaType": "application/json", "description": "完整JSON文本，客户端保留十进制整数原文；对象语法与包参数语义由服务验证。"}
SAMPLES = ["default-v1", "workshop-single-line-v1", "workshop-multi-line-v1"]
SLOTS = ["unsungFill", "sungFill", "stroke"]


def nullable(schema):
    return {"anyOf": [copy.deepcopy(schema), {"type": "null"}]}


def null_slots(names):
    return {name: {"type": "null"} for name in names}


def extend(schemas, metadata, schema_for):
    for name, values in {
        "TemplateEffectTargetDto": ["line", "tokenFamily", "application"],
        "TemplateEffectOverlayLayerDto": ["package", "behindText", "aboveText"],
        "TemplateScriptReadScopeDto": ["boundSources", "project", "branchInput"],
    }.items():
        schemas[name] = {"type": "string", "enum": values}
    # P4 extends the same package-reference wire with a fixed configuration
    # library source; historical asset/library examples remain byte-for-byte.
    reference_fields = ["packageAssetId", "libraryId", "libraryRevision", "templateId", "templateRevision", "configurationId", "configurationRevision"]
    audit(metadata, "EffectPackageReferenceDto", {field: "String" for field in ("kind", "packageId", "packageRevision", *reference_fields)},
          nullable=reference_fields, optional=reference_fields)
    base = dict(kind=TEXT, packageId=TEXT, packageRevision=PACKAGE_REVISION)
    branches = []
    for kind, fields in (("asset", ["packageAssetId"]), ("library", ["libraryId", "libraryRevision", "templateId", "templateRevision"]),
                         ("configuration", ["configurationId", "configurationRevision"])):
        # Sort the set difference: generated JSON must be byte-stable across
        # processes with different hash seeds.
        props = {**copy.deepcopy(base), **null_slots(sorted(set(reference_fields) - set(fields)))}
        props["kind"] = {"const": kind, "type": "string"}
        for field in fields: props[field] = copy.deepcopy(REVISION if field in ("libraryRevision", "configurationRevision") else TEXT)
        branches.append(obj(props, ["kind", "packageId", "packageRevision", *fields]))
    for name in ("EffectPackageReferenceDto", "EffectPackageReferenceDtoInput"):
        schemas[name] = {"oneOf": copy.deepcopy(branches), "description": "授权asset、固定模板库或固定配置库修订的包引用；无关字段只能省略/null，不接受服务器路径。"}
    inspect = obj({"packageAssetId": nullable(TEXT), "expectedPackageRevision": nullable(TEXT), "reference": nullable(ref("EffectPackageReferenceDtoInput"))}, [])
    inspect["oneOf"] = [
        {"required": ["packageAssetId"], "properties": {"packageAssetId": TEXT, "reference": {"type": "null"}}},
        {"required": ["reference"], "properties": {"reference": ref("EffectPackageReferenceDtoInput"), "packageAssetId": {"type": "null"}, "expectedPackageRevision": {"type": "null"}}}]
    schemas["EffectPackageInspectRequest"] = inspect
    schemas["EffectPackageInspectResponse"]["properties"]["reference"] = ref("EffectPackageReferenceDto")
    schemas["EffectPackageInspectResponse"]["properties"]["manifest"] = {"type": "object"}
    schemas["EffectPackageInspectResponse"]["properties"]["manifestJson"] = copy.deepcopy(JSON_TEXT)
    for name in ("defaultValueJson", "minimumJson", "maximumJson"):
        schemas["EffectPackageParameterSchemaDto"]["properties"][name] = copy.deepcopy(JSON_TEXT)
    schemas["EffectPackageParameterSchemaDto"]["properties"]["type"]["enum"] = ["number", "integer", "duration", "boolean", "enum", "text", "color", "curve", "asset"]
    schemas["EffectPackageExportSchemaDto"]["properties"]["kind"]["enum"] = ["controller", "material", "graph"]
    schemas["EffectPackageExportSchemaDto"]["properties"]["declaration"] = {"type": "object"}
    schemas["EffectPackageInputsDto"]["properties"]["inputs"] = {"type": "array", "items": {"type": "object"}}
    schemas["EffectPackageInspectResponse"]["description"] = "完整包声明与结构诊断，不编译/运行脚本或解码媒体；ManifestJson和参数*Json保精确整数，manifest/declaration/inputs供展示。结构失败保原manifest/files且typed参数/exports为空。"

    # Full input uses the same canonical values but accepts package materials with
    # exact parameter JSON, without imposing legacy static admission quotas.
    package = obj(dict(kind={"const": "package", "type": "string"}, packageId=TEXT, revision=PACKAGE_REVISION, exportId=TEXT, parametersJson=copy.deepcopy(JSON_TEXT)))
    schemas["FullTemplatePackageMaterial"] = package
    schemas["FullTemplateMaterial"] = {"oneOf": [ref("SolidStyleMaterialDto"), ref("GradientStyleMaterialDto"), ref("FullTemplatePackageMaterial")]}
    stroke = copy.deepcopy(schemas["StyleStrokeDto"])
    stroke["properties"]["material"] = ref("FullTemplateMaterial")
    schemas["FullTemplateStroke"] = stroke
    # Package materials are admitted in fills and stroke layer 0 only (D-P3-9).
    strokes = strokes_array(ref("FullTemplateStroke"), response=True)
    strokes["description"] = "从内到外至多4层；包材质只允许在第0层。"
    unsung = copy.deepcopy(schemas["StyleAppearanceDto"])
    unsung["properties"].update(fill=ref("FullTemplateMaterial"), strokes=copy.deepcopy(strokes))
    sung = copy.deepcopy(schemas["StyleSungAppearanceDto"])
    sung["properties"].update(fill=ref("FullTemplateMaterial"), strokes=followable(strokes, response=False))
    for value in (unsung, sung):
        value.pop("x-clr-type", None)
    schemas["FullTemplateAppearance"] = unsung
    schemas["FullTemplateSungAppearance"] = sung
    style = copy.deepcopy(schemas["CanonicalStyleDto"])
    style["properties"]["unsung"] = ref("FullTemplateAppearance")
    style["properties"]["sung"] = ref("FullTemplateSungAppearance")
    schemas["FullTemplateStyle"] = style
    audit(metadata, "FullTemplateDefinitionDto", dict(name="String", kind="TemplateKind", style="CanonicalStyleDto", properties="String[]", effectRecipes="TemplateEffectRecipeDto[]", packages="EffectPackageReferenceDto[]"), base="StaticTemplateDefinitionDto")
    recipe_fields = dict(recipeId=TEXT, name=TEXT, target=ref("TemplateEffectTargetDto"), packageId=TEXT, revision=PACKAGE_REVISION,
        exportId=TEXT, parametersJson=copy.deepcopy(JSON_TEXT), overlayLayer=ref("TemplateEffectOverlayLayerDto"), enabled={"type": "boolean"},
        readScope=ref("TemplateScriptReadScopeDto"), branchInput=nullable(TEXT), fixedSeed=nullable({"type": "string", "pattern": r"^[0-9]+$", "x-uint64-string": True, "description": "UInt64十进制字符串，范围0..18446744073709551615由服务校验。"}), configurationId=nullable(TEXT))
    audit(metadata, "TemplateEffectRecipeDto", dict(recipeId="String", name="String", target="TemplateEffectTargetDto", packageId="String", revision="String", exportId="String", parametersJson="String", overlayLayer="TemplateEffectOverlayLayerDto", enabled="Boolean", readScope="TemplateScriptReadScopeDto", branchInput="String", fixedSeed="String", configurationId="String"), nullable=("branchInput", "fixedSeed", "configurationId"))
    recipe = obj(recipe_fields)
    recipe["allOf"] = [{"if": {"properties": {"readScope": {"const": "branchInput"}}}, "then": {"properties": {"branchInput": TEXT}}, "else": {"properties": {"branchInput": {"type": "null"}}}}]
    for suffix in ("", "Input"): schemas["TemplateEffectRecipeDto" + suffix] = copy.deepcopy(recipe)
    props = dict(name=TEXT, kind=ref("TemplateKind"), style=ref("FullTemplateStyle"), properties={"type": "array", "maxItems": len(PROPERTIES), "uniqueItems": True, "items": ref("StylePropertyPath")},
        effectRecipes={"type": "array", "items": ref("TemplateEffectRecipeDto")}, packages={"type": "array", "items": ref("EffectPackageReferenceDtoInput")})
    for suffix in ("", "Input"):
        definition = obj(copy.deepcopy(props))
        definition["allOf"] = definition_constraints(PROPERTIES)
        definition["anyOf"] = [{"properties": {"properties": {"minItems": 1}}}, {"properties": {"effectRecipes": {"minItems": 1}}}]
        schemas["FullTemplateDefinitionDto" + suffix] = definition
    for kind, name in (("create", "CreateTemplateCommandDtoInput"), ("replace", "ReplaceTemplateCommandDtoInput")):
        old = copy.deepcopy(schemas[name])
        full = copy.deepcopy(old)
        full["properties"]["definitionFormat"] = {"type": "string", "const": "full"}
        full["required"].append("definitionFormat")
        full["properties"]["definition"] = ref("FullTemplateDefinitionDtoInput")
        schemas[name] = {"oneOf": [old, full], "x-clr-type": old["x-clr-type"]}
    detail = schemas["TemplateDetailResponse"]
    detail["properties"]["definitionAvailability"]["enum"] = ["staticAvailable", "advancedRuntimeAvailable", "advancedUnsupported", "fullAvailable"]
    detail["properties"]["definitionFormat"] = {"type": "string", "const": "full"}
    detail["properties"]["packageClosureDetails"] = {"type": "array", "items": ref("EffectPackageInspectResponse")}
    detail["properties"]["definition"] = {"anyOf": [ref("StaticTemplateDefinitionDto"), ref("FullTemplateDefinitionDto"), {"type": "null"}]}
    for field in ("definitionFormat", "packageClosureDetails"):
        if field in detail["required"]: detail["required"].remove(field)
    detail["allOf"] = [{"if": {"required": ["definitionFormat"]},
        "then": {"required": ["packageClosureDetails"], "properties": {"definitionAvailability": {"const": "fullAvailable"}, "definition": ref("FullTemplateDefinitionDto")}},
        "else": {"properties": {"definitionAvailability": {"enum": ["staticAvailable", "advancedRuntimeAvailable", "advancedUnsupported"]}},
            "not": {"required": ["packageClosureDetails"]}, "allOf": [
                {"if": {"properties": {"definitionAvailability": {"const": "staticAvailable"}}}, "then": {"properties": {"definition": ref("StaticTemplateDefinitionDto"), "summary": {"properties": {"hasEffectRecipes": {"const": False}, "hasPackageMaterials": {"const": False}, "hasPackages": {"const": False}}}}}, "else": {"properties": {"definition": {"type": "null"}}}}]}}]

    source_names = ["libraryId", "libraryRevision", "templateId", "templateRevision", "draftRevision", "definition", "definitionFormat"]
    saved = obj({"kind": {"const": "savedTemplate", "type": "string"}, **null_slots(source_names)}, ["kind", "libraryId", "libraryRevision", "templateId", "templateRevision"])
    for field in ("libraryId", "libraryRevision", "templateId", "templateRevision"): saved["properties"][field] = copy.deepcopy(REVISION if field == "libraryRevision" else TEXT)
    draft_base = {"kind": {"const": "draft", "type": "string"}, **null_slots(source_names)}
    draft_base["draftRevision"] = {**TEXT, "maxLength": 256}
    legacy = obj(copy.deepcopy(draft_base), ["kind", "draftRevision", "definition"])
    legacy["properties"]["definition"] = ref("StaticTemplateDefinitionDtoInput")
    legacy["properties"]["definitionFormat"] = nullable({"type": "string", "const": "static"})
    full = obj(copy.deepcopy(draft_base), ["kind", "draftRevision", "definition", "definitionFormat"])
    full["properties"]["definition"] = ref("FullTemplateDefinitionDtoInput")
    full["properties"]["definitionFormat"] = {"type": "string", "const": "full"}
    schemas["TemplatePreviewSourceDtoInput"] = {"oneOf": [saved, legacy, full]}
    schemas["TemplatePreviewOptionsRequest"] = obj({"source": ref("TemplatePreviewSourceDtoInput")})
    preview = schemas["TemplatePreviewSessionRequest"]
    preview["properties"]["source"] = ref("TemplatePreviewSourceDtoInput")
    preview["properties"]["sampleId"] = {"type": "string", "enum": SAMPLES}
    preview["properties"]["targetKey"] = nullable(TEXT)
    preview["properties"]["materialSlot"] = nullable({"type": "string", "enum": SLOTS})
    preview["description"] = "default-v1保持旧三秒试样；workshop单/多行12秒，targetKey来自options。不传targetKey取首个兼容目标；materialSlot只是编辑焦点，不隔离模板运行。元数据必填。"
    candidate = schemas["PresetApplicationPreviewRequest"]
    old_fields = ["templateLibraryId", "templateLibraryRevision", "templateId", "templateRevision"]
    for field in old_fields: candidate["properties"][field] = nullable(TEXT)
    candidate["required"] = [field for field in candidate["required"] if field not in old_fields]
    candidate["properties"]["source"] = nullable(ref("TemplatePreviewSourceDtoInput"))
    candidate["oneOf"] = [
        {"required": old_fields, "properties": {**{field: TEXT for field in old_fields}, "source": {"type": "null"}}},
        {"required": ["source"], "properties": {"source": ref("TemplatePreviewSourceDtoInput"), **null_slots(old_fields)}}]
    candidate["properties"]["targets"] = {"type": "array", "minItems": 1, "maxItems": 1000, "items": ref("PresetApplicationTargetDtoInput")}
    candidate["description"] = "旧精确库引用字段或source二选一；source允许固定库模板或完整/静态草稿，不写库/工程。工程expectedProjectRevision仍独立复核。"
    selection = schemas["TemplatePreviewSelectionDto"]
    selection["properties"]["sampleId"] = {"type": "string", "enum": SAMPLES}
    selection["properties"]["materialSlot"] = {"type": "string", "enum": SLOTS}
    schemas["TemplateMaterialSlotOptionDto"]["properties"]["material"] = nullable(ref("FullTemplateMaterial"))
    if "material" not in schemas["TemplateMaterialSlotOptionDto"]["required"]: schemas["TemplateMaterialSlotOptionDto"]["required"].append("material")
    schemas["TemplateMaterialSlotOptionDto"]["properties"]["slot"] = {"type": "string", "enum": SLOTS}
    schemas["TemplateSampleOptionDto"]["properties"]["sampleId"] = {"type": "string", "enum": SAMPLES[1:]}
    schemas["TemplateSampleOptionDto"]["properties"]["durationUs"] = {"type": "string", "const": "12000000", "x-int64-string": True}
    # Existing target branches retain irrelevant-null compatibility; clips is a real
    # domain binding, not flattened into line IDs.
    target = preset_target_union()
    clip_props = {"kind": {"const": "clips", "type": "string"}, "branchId": TEXT,
        "clipIds": {"type": "array", "minItems": 1, "maxItems": 1000, "uniqueItems": True, "items": TEXT}, **null_slots(["lineId", "tokenId", "nodeId", "includeDescendants"])}
    for branch in target["oneOf"]: branch["properties"]["clipIds"] = {"type": "null"}
    target["oneOf"].insert(-1, obj(clip_props, ["kind", "branchId", "clipIds"]))
    target["description"] = "六类真实绑定；clips要求branchId与1..1000唯一clipIds。node包含显式includeDescendants并通过原兼容判据支持完整高级模板；其它无关字段仅null。"
    schemas["PresetApplicationTargetDtoInput"] = target
    read_target = schemas["ProjectPresetApplicationTargetDto"]
    read_target["properties"]["kind"]["enum"] = ["project", "branch", "line", "token", "clips", "node"]


def configure(operation, item):
    name = item["operationId"]
    if name in ("inspectEffectPackage", "templatePreviewOptions"):
        operation["x-receipt-location"] = "none"
        operation["x-query-policy"] = {"allowUnknown": False, "allowed": []}
        operation["description"] += " 只读，无metadata/回执；授权/精确修订、原领域兼容性由服务核验。"
    if name == "inspectEffectPackage": operation["x-capability"] = "effect-packages.inspect"
    if name == "templatePreviewOptions": operation["x-capability"] = "template-preview-options.read"
    if name == "templateDetail":
        operation["parameters"].append({"name": "definitionFormat", "in": "query", "required": False, "schema": {"type": "string", "enum": ["static", "full"]}})
        policy = operation.get("x-query-policy", {})
        if "allowed" in policy: policy["allowed"].append("definitionFormat")
        operation["description"] += " definitionFormat=full返回完整高级定义与外层packageClosureDetails；省略/static保旧静态形状及advancedRuntimeAvailable/null。"
    if name in ("createTemplatePreview", "createPresetApplicationPreview"):
        operation["description"] += " 接受显式full草稿及固定库模板，包引用复用N05；保持精确来源与只读候选。"
