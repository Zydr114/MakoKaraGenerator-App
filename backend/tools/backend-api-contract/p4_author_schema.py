"""Reviewed P4 author/configuration shapes; CLR export remains the source of fields."""
import copy

from static_style_schema import audit, obj, ref
from p3_workshop_schema import TEXT, JSON_TEXT, nullable, null_slots, SAMPLES, SLOTS
from template_library_schema import REVISION as LIBRARY_REVISION

REVISION = {"type": "string", "pattern": r"^sha256:[0-9a-f]{64}$"}
DRAFT_ID = {"type": "string", "pattern": r"^draft-[0-9a-f]{32}$"}
AUTHOR_VALIDATION_PROFILE = "author-script-sample-v1"
CONFIGURATION_FIELDS = dict(name="String", packageId="String", sourceRevision="String", exportId="String",
    parametersJson="String", relativeTarget="TemplateEffectTargetDto", readScope="TemplateScriptReadScopeDto",
    branchInput="String", fixedSeed="String", overlayLayer="TemplateEffectOverlayLayerDto")


def configuration_properties(package=True):
    result = dict(name=copy.deepcopy(TEXT), exportId=copy.deepcopy(TEXT), parametersJson=copy.deepcopy(JSON_TEXT),
        relativeTarget=ref("TemplateEffectTargetDto"), readScope=ref("TemplateScriptReadScopeDto"),
        branchInput=nullable(TEXT), fixedSeed=nullable({"type": "string", "pattern": r"^[0-9]+$",
            "x-uint64-string": True}), overlayLayer=ref("TemplateEffectOverlayLayerDto"))
    if package:
        result.update(packageId=copy.deepcopy(TEXT), sourceRevision=copy.deepcopy(REVISION))
    return result


def configuration_rules(schema):
    schema["allOf"] = [{"if": {"properties": {"readScope": {"const": "branchInput"}}},
        "then": {"properties": {"branchInput": copy.deepcopy(TEXT)}},
        "else": {"properties": {"branchInput": {"type": "null"}}}}]
    return schema


def extend(schemas, metadata, schema_for):
    if "ScriptConfigurationCommandResponse" in schemas:
        # An empty personal library reports its sentinel revision as the
        # literal `missing`; populated libraries return the 64-hex index SHA.
        schemas["ScriptConfigurationCommandResponse"]["properties"]["previousRevision"] = copy.deepcopy(LIBRARY_REVISION)
    for component in ("JobResponse", "JobSummaryDto"):
        schemas[component]["properties"]["kind"]["enum"].append("author-compilation")
    for component in ("BackendJobInputDto", "BackendJobInputDtoRaw"):
        schema = schemas[component]
        properties = schema["properties"]
        project_revision = copy.deepcopy(properties["sourceRevision"])
        project_profiles = copy.deepcopy(properties["sourceProfile"])
        properties["sourceKind"]["enum"] = ["project", "authorDraft"]
        properties["sourceRevision"] = {"oneOf": [project_revision, copy.deepcopy(REVISION)],
            "description": "project使用十进制工程修订；authorDraft使用固定配置源码sha256修订，按sourceKind联动校验。"}
        properties["sourceProfile"]["enum"].append(AUTHOR_VALIDATION_PROFILE)
        schema.setdefault("allOf", []).append({
            "if": {"properties": {"sourceKind": {"const": "authorDraft"}}, "required": ["sourceKind"]},
            "then": {"properties": {"sourceId": copy.deepcopy(DRAFT_ID), "sourceRevision": copy.deepcopy(REVISION),
                "sourceProfile": {"const": AUTHOR_VALIDATION_PROFILE}}},
            "else": {"properties": {"sourceKind": {"const": "project"}, "sourceRevision": project_revision,
                "sourceProfile": project_profiles}},
        })
        schema["description"] += " authorDraft验证固定作者试样，源码修订为sha256，任务profile为author-script-sample-v1；DTO不包含taskProfile字段。"
    schemas["AuthorValidationJobInputDto"]["properties"].update(
        sampleId={"type": "string", "enum": SAMPLES[1:]}, targetKey=copy.deepcopy(TEXT),
        materialSlot=nullable({"type": "string", "enum": SLOTS}))
    if "AuthorDraftCreateRequest" in schemas:
        create = schemas["AuthorDraftCreateRequest"]
        props = create["properties"]
        # A draft can originate from a package asset or an existing personal
        # configuration.  The two source selectors are mutually exclusive;
        # configuration cloning carries its exact library revision.
        if "packageAssetId" in props:
            props["packageAssetId"] = nullable(TEXT)
        props["configurationId"] = nullable(TEXT)
        props["expectedConfigurationRevision"] = nullable(copy.deepcopy(LIBRARY_REVISION))
        for field in ("packageAssetId", "configurationId", "expectedConfigurationRevision"):
            if field not in create.get("required", []):
                create.setdefault("required", []).append(field)
        # Alternative source selectors are required by their oneOf branch;
        # keeping them in the object-level required list would reject the
        # historical packageAssetId example before oneOf is evaluated.
        create["required"] = [field for field in create["required"]
                               if field not in ("packageAssetId", "configurationId", "expectedConfigurationRevision")]
        create["oneOf"] = [
            {"required": ["packageAssetId"], "properties": {"packageAssetId": copy.deepcopy(TEXT),
                "configurationId": {"type": "null"}, "expectedConfigurationRevision": {"type": "null"}}},
            {"required": ["configurationId", "expectedConfigurationRevision"], "properties": {
                "packageAssetId": {"type": "null"}, "configurationId": copy.deepcopy(TEXT),
                "expectedConfigurationRevision": copy.deepcopy(LIBRARY_REVISION)}},
        ]
    audit(metadata, "AuthorDraftConfigurationInputDto", CONFIGURATION_FIELDS,
        nullable=("branchInput", "fixedSeed"))
    schemas["AuthorDraftConfigurationInputDtoInput"] = configuration_rules(obj(configuration_properties()))
    schemas["AuthorDraftConfigurationRequest"]["properties"]["expectedDraftRevision"] = copy.deepcopy(REVISION)
    audit(metadata, "AuthorCompilationRequest", dict(expectedDraftRevision="String", metadata="RequestMetadataDto", executionMode="String"),
        nullable=("metadata", "executionMode"), optional=("metadata", "executionMode"))
    schemas["AuthorCompilationRequest"]["properties"]["executionMode"] = nullable({"type": "string", "enum": ["sync", "async"]})
    schemas["AuthorCompilationRequest"]["description"] = "省略/null/sync保持同步200；async返回202 JobResponse。任务捕获固定源码，不把旧结果改成最新草稿。"
    for name in ("AuthorDraftSourcesResponse", "AuthorCompilationJobInputDto", "AuthorValidationJobInputDto"):
        if name not in schemas:
            continue
        for field in ("draftId", "draftRevision", "sourceRevision"):
            if field in schemas[name]["properties"]:
                schemas[name]["properties"][field] = copy.deepcopy(DRAFT_ID if field == "draftId" else REVISION)
    if "AuthorValidationJobRequest" in schemas:
        render_fields = ("renderBackend",) if any(p["name"] == "renderBackend"
            for p in metadata["AuthorValidationJobRequest"]["properties"]) else ()
        audit(metadata, "AuthorValidationJobRequest", dict(expectedDraftRevision="String", sampleId="String", targetKey="String",
            materialSlot="String", metadata="RequestMetadataDto", **{field:"String" for field in render_fields}),
            nullable=("materialSlot", "metadata", *render_fields), optional=("materialSlot", "metadata", *render_fields))
        request = schemas["AuthorValidationJobRequest"]
        request["properties"].update(expectedDraftRevision=copy.deepcopy(REVISION), sampleId={"type": "string", "enum": SAMPLES[1:]},
            targetKey=copy.deepcopy(TEXT), materialSlot=nullable({"type": "string", "enum": SLOTS}))
        request["description"] = "固定草稿配置、源码及试样目标；不接受projectSessionId、客户端帧计划或Worker路径。"

    # PreviewSourceDto is an input union: project/effect candidates retain the
    # historical shape, while authorDraft carries the exact draft revision,
    # fixed source revision and workshop selection.  The source revision is
    # deliberately separate from sourceRevision (the latter is the draft CAS
    # revision used by preview generations).
    if "PreviewSourceDtoInput" in schemas:
        base = copy.deepcopy(schemas["PreviewSourceDtoInput"])
        base_props = base["properties"]
        base_props["kind"] = {"type": "string", "const": "project"}
        base_props["authorSourceRevision"] = {"type": "null"}
        # Project previews use the project session identity only; workshop
        # sample selection is valid exclusively for authorDraft sources.
        base_props["templateSample"] = {"type": "null"}
        project = obj(base_props, ["kind", "sourceId", "sourceRevision"])

        selection = copy.deepcopy(schemas["TemplatePreviewSelectionDtoInput"])
        selection["properties"]["sampleId"] = {"type": "string", "enum": SAMPLES[1:]}
        selection["properties"]["materialSlot"] = nullable({"type": "string", "enum": SLOTS})
        author_props = {
            "kind": {"type": "string", "const": "authorDraft"},
            "sourceId": copy.deepcopy(DRAFT_ID),
            "sourceRevision": copy.deepcopy(REVISION),
            "authorSourceRevision": copy.deepcopy(REVISION),
            "templateSample": selection,
        }
        schemas["PreviewSourceDtoInput"] = {
            "oneOf": [ref("PreviewSourceDtoInputProject"), ref("PreviewSourceDtoInputAuthorDraft")],
            "discriminator": {"propertyName": "kind", "mapping": {
                "project": "#/components/schemas/PreviewSourceDtoInputProject",
                "authorDraft": "#/components/schemas/PreviewSourceDtoInputAuthorDraft",
            }},
            "description": "固定项目或作者草稿预览来源联合；authorDraft的sourceId/sourceRevision/source及样本选择必须完整，禁止混入其它来源字段。",
        }
        schemas["PreviewSourceDtoInputProject"] = project
        schemas["PreviewSourceDtoInputAuthorDraft"] = obj(author_props)
        schemas["PreviewSourceDto"]["properties"]["authorSourceRevision"] = nullable(copy.deepcopy(REVISION))
        schemas["PreviewSourceDto"]["properties"]["authorSourceRevision"]["description"] = "authorDraft固定的效果包源码修订；普通工程来源省略。"

    if "PreviewSessionRequest" in schemas:
        schemas["PreviewSessionRequest"].setdefault("allOf", []).append({
            "if": {"properties": {"source": {"properties": {"kind": {"const": "authorDraft"}}}}},
            "then": {"properties": {"previewKind": {"const": "authorDraft"}, "committable": {"const": False}},
                     "required": ["previewKind", "committable"]},
            "description": "authorDraft来源必须显式使用previewKind=authorDraft且committable=false。",
        })

    source_fields = ("draftId", "expectedDraftRevision", "sourceRevision", "configurationId", "expectedConfigurationRevision", "package")
    source_branches = []
    for active in (("draftId", "expectedDraftRevision", "sourceRevision"),
                   ("configurationId", "expectedConfigurationRevision"), ("package",)):
        props = null_slots(source_fields)
        for field in active:
            props[field] = (ref("EffectPackageReferenceDtoInput") if field == "package" else
                copy.deepcopy(REVISION if field in ("expectedDraftRevision", "sourceRevision") else
                    LIBRARY_REVISION if field == "expectedConfigurationRevision" else DRAFT_ID if field == "draftId" else TEXT))
        source_branches.append(obj(props, list(active)))
    audit(metadata, "ScriptConfigurationSourceDto", dict(draftId="String", expectedDraftRevision="String", sourceRevision="String",
        configurationId="String", expectedConfigurationRevision="String", package="EffectPackageReferenceDto"),
        nullable=source_fields, optional=source_fields)
    schemas["ScriptConfigurationSourceDtoInput"] = {"oneOf": source_branches,
        "description": "固定草稿源码、固定库配置或授权包三来源互斥；无关字段只可省略/null。"}
    input_props = configuration_properties(package=False)
    input_props["source"] = ref("ScriptConfigurationSourceDtoInput")
    schemas["ScriptConfigurationInputDtoInput"] = configuration_rules(obj(input_props))
    branches = []
    mapping = {}
    for kind, clr, fields in (("create", "ScriptConfigurationCreateCommandDto", ("configuration",)),
            ("replace", "ScriptConfigurationReplaceCommandDto", ("configurationId", "configuration")),
            ("copy", "ScriptConfigurationCopyCommandDto", ("configurationId", "name")),
            ("rename", "ScriptConfigurationRenameCommandDto", ("configurationId", "name")),
            ("delete", "ScriptConfigurationDeleteCommandDto", ("configurationId",))):
        audit(metadata, clr, {"type": "String", **{field: "ScriptConfigurationInputDto" if field == "configuration" else "String" for field in fields}},
            base="ScriptConfigurationCommandDto")
        component = schema_for(clr, "request")["$ref"].split("/")[-1]
        props = {"type": {"type": "string", "const": kind}}
        props.update({field: ref("ScriptConfigurationInputDtoInput") if field == "configuration" else copy.deepcopy(TEXT) for field in fields})
        schemas[component] = obj(props)
        branches.append(ref(component))
        mapping[kind] = ref(component)["$ref"]
    schemas["ScriptConfigurationCommandDtoInput"] = {"oneOf": branches, "discriminator": {"propertyName": "type", "mapping": mapping}}
    schemas["ScriptConfigurationCommandRequest"]["properties"]["expectedRevision"] = copy.deepcopy(LIBRARY_REVISION)


def configure(operation, item):
    name = item["operationId"]
    if name == "compileAuthorDraft":
        operation["responses"]["202"] = {"description": "executionMode=async时接受作者编译任务；读取job/SSE/cancel与固定compilation-result。",
            "content": {"application/json": {"schema": ref("JobResponse")}}}
        operation["description"] += " 省略/null/sync返回200 AuthorCompilationResponse；async返回202 JobResponse。"
        operation["x-execution-mode-responses"] = {"sync": "200", "async": "202", "default": "sync"}
    if name == "readAuthorSources":
        operation["parameters"] += [{"name": field, "in": "query", "required": True,
            "schema": copy.deepcopy(REVISION)} for field in ("expectedDraftRevision", "sourceRevision")]
        operation["description"] += " 两个修订必填；源为该草稿仍保留的固定C#闭包，不返回主机路径。"
    if name in ("readAuthorDrafts", "readAuthorSources", "readAuthorCompilationResult"):
        operation["x-receipt-location"] = "none"
    if name == "readAuthorCompilationResult":
        operation["description"] += " 返回job捕获源码的完整不可变结果；取消/失败且未发布结果时读取job状态，不伪造空成功。"
    if name in ("scriptConfigurationList", "scriptConfigurationDetail"):
        operation["parameters"].append({"name": "expectedRevision", "in": "query", "required": name == "scriptConfigurationDetail",
            "schema": copy.deepcopy(LIBRARY_REVISION)})
        operation["x-receipt-location"] = "none"
