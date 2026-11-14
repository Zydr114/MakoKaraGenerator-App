"""Reviewed owner-only source inspection and atomic configuration adoption."""
import copy

from p3_workshop_schema import nullable
from p4_author_schema import REVISION, DRAFT_ID, configuration_properties, configuration_rules
from static_style_schema import audit, obj, ref

NEW_SCHEMAS = ("AuthorSourceInspectionResponse",)
INSPECTION_FIELDS = dict(draftId="String", draftRevision="String", sourceRevision="String", packageId="String",
    packageRevision="String", name="String", version="String", packageVersion="Int32", sdkVersion="String",
    shaderProfile="String", structureValid="Boolean", parameters="EffectPackageParameterSchemaDto[]",
    exports="EffectPackageExportSchemaDto[]", bindingKinds="String[]", inputs="EffectPackageInputsDto[]",
    files="EffectPackageFileSummaryDto[]", diagnostics="EffectPackageStructureDiagnosticDto[]", protocolVersion="String")
CONFIGURATION_FIELDS = dict(name="String", exportId="String", parametersJson="String",
    relativeTarget="TemplateEffectTargetDto", readScope="TemplateScriptReadScopeDto", branchInput="String",
    fixedSeed="String", overlayLayer="TemplateEffectOverlayLayerDto")
MAXIMUM_RESPONSE_BYTES = 8 * 1024 * 1024


def uint64_pattern():
    # Lexical decimal comparison, preserving the server's accepted leading zeros.
    # Every shorter significant value or first lesser digit is <= UInt64.MaxValue.
    maximum = "18446744073709551615"
    alternatives = [r"[0-9]{1,19}", maximum]
    for index, digit in enumerate(maximum):
        if digit != "0":
            lesser = "[0-" + str(int(digit) - 1) + "]" if digit != "1" else "0"
            alternatives.append(maximum[:index] + lesser + "[0-9]{" + str(len(maximum) - index - 1) + "}")
    # End assertion also rejects a final newline (Python/ECMA $ may accept one).
    return r"^0*(?:" + "|".join(alternatives) + r")$(?![\s\S])"


def extend(schemas, metadata):
    audit(metadata, "AuthorSourceInspectionResponse", INSPECTION_FIELDS, optional=("protocolVersion",))
    audit(metadata, "AuthorAdoptSourceRevisionRequest", dict(expectedDraftRevision="String", sourceRevision="String",
        metadata="RequestMetadataDto", configuration="AuthorPackageConfigurationInputDto"),
        nullable=("metadata", "configuration"), optional=("metadata", "configuration"))
    audit(metadata, "AuthorPackageConfigurationInputDto", CONFIGURATION_FIELDS, nullable=("branchInput", "fixedSeed"))
    response = schemas["AuthorSourceInspectionResponse"]
    assert set(response["properties"]) == set(INSPECTION_FIELDS)
    assert set(response["required"]) == set(INSPECTION_FIELDS)
    for field in ("draftRevision", "sourceRevision", "packageRevision"):
        response["properties"][field] = copy.deepcopy(REVISION)
    response["properties"]["draftId"] = copy.deepcopy(DRAFT_ID)
    response["properties"]["protocolVersion"] = {"type": "string", "const": "1"}
    response["properties"]["packageVersion"]["enum"] = [3]
    response["properties"]["sdkVersion"]["description"] = "固定包声明的SDK版本；沿现有profile兼容规则，SDK2补丁版本不收窄为2.0.0。"
    response["properties"]["shaderProfile"]["enum"] = ["skia-ganesh-3.119.4-v2"]
    response["description"] = (
        "owner草稿保留的精确源码声明完整投影；sourceRevision等于packageRevision，packageId固定为草稿包身份。"
        "独立响应复用参数/导出/input/file/diagnostic DTO，不包含reference、manifest/manifestJson、源码或机器路径。"
        "parameters的*Json是保留整数原文的JSON文本字符串，declaration/inputs保留JSON值；unit/minimumJson/maximumJson为null时省略。files使用size而非sizeBytes。"
        "全响应最多8388608个Web UTF-8字节，超限整次拒绝。普通响应继续允许忽略未来未知字段。")
    # Adopt has stronger complete-object presence than historical CLR nullable
    # inference; keep this inline so package-files' existing schema is unchanged.
    properties = configuration_properties(package=False)
    properties["name"].update(maxLength=256)
    properties["fixedSeed"] = nullable({"type": "string", "pattern": uint64_pattern(), "x-uint64-string": True,
        "description": "UInt64十进制字符串0..18446744073709551615，允许前导零；禁止JSON number、符号和空串。"})
    configuration = configuration_rules(obj(properties))
    request = schemas["AuthorAdoptSourceRevisionRequest"]
    request["properties"]["configuration"] = nullable(configuration)
    assert "configuration" not in request["required"]
    request["description"] = (
        "显式采用此草稿retained修订；SDK2 C#目标须有此草稿该精确sourceRevision的成功编译记录。"
        "configuration省略/null沿用旧配置；非null必须显式给完整八字段，branchInput/fixedSeed允许null但不可省略，未知/重复字段拒绝。"
        "name非空白且至多256个UTF-16码元，保存时trim；按目标包验证export/参数/绑定和真实sample options，再同一次document发布配置、名称、导出与采用标记。"
        "CurrentSourceRevision与最近一次编译摘要保持原意；同值no-op、CAS及包含完整configuration的receipt语义保持。")


def configure(operation, item):
    name = item["operationId"]
    if name == "inspectAuthorSource":
        fields = ("expectedDraftRevision", "sourceRevision")
        operation["parameters"] += [{"name": field, "in": "query", "required": True,
            "schema": copy.deepcopy(REVISION)} for field in fields]
        operation["x-query-policy"] = {"allowUnknown": False, "allPresentExactlyOnce": True,
            "caseSensitiveNames": True, "required": list(fields), "allowed": list(fields)}
        operation["x-query-schema"] = ref("AuthorPackageQuery")
        operation["x-request-body-policy"] = "forbidden"
        operation["x-capability"] = "author.drafts.source-inspection"
        operation["x-author-inspection-limits"] = {"maxResponseBytes": MAXIMUM_RESPONSE_BYTES, "encoding": "web-json-utf8", "truncate": False}
        operation["x-receipt-location"] = "none"
        operation["description"] += (
            " 两个query必须非空且各出现一次；不接受未知query或body。锁内核对owner、草稿CAS、retained成员与完整包身份。"
            "未编译源码也可检查，不编译/执行作者、不准备native SVG、不创建资源。响应独立且reference-free，"
            "manifest原文继续从package-files读取，两次读取核对相同完整身份tuple。8MiB完整UTF-8响应超限整次拒绝；no-store。")
        operation["responses"]["200"].setdefault("headers", {})["Cache-Control"] = {"schema": {"type": "string", "const": "no-store"}}
        rejection_descriptions = {
            "400": "invalidRequest：非法draft/revision、缺失/重复/空白/未知query或请求body；field为draftId、对应query名称或body。",
            "401": "resourceUnauthorized：不是草稿当前owner；登记/令牌失败沿统一认证错误。",
            "404": "resourceNotFound：草稿或所选源码不存在，或源码不属于此草稿retained历史；field为draftId/sourceRevision。",
            "409": "revisionConflict：expectedDraftRevision过期；作者停止时sessionClosing，宿主停止全局拒绝可为503。",
            "413": "resourceTooLarge：完整Web UTF-8响应超过8388608字节；field=sourceRevision，failureCode=AuthorSourceInspectionTooLarge。",
            "422": "projectLoadFailed：读取失败AuthorSourceInspectionReadFailed或身份不符AuthorSourceIdentityMismatch；field=sourceRevision。包结构读取拒绝沿commandRejected及原failureCode/path。",
        }
        for status, description in rejection_descriptions.items():
            operation["responses"][status] = {"description": description,
                "content": {"application/problem+json": {"schema": ref("BackendErrorResponse")}}}
    elif name == "adoptAuthorSourceRevision":
        operation["description"] += (
            " 可选完整configuration按目标retained源码的声明原子采用，省略/null沿用既有配置。"
            "SDK2 C#必须此草稿精确源编译成功；无效新配置不部分切换，CurrentSourceRevision和最近编译报告保持原义。"
            "同值no-op；receipt指纹含完整配置，原样重放返回原结果，改payload复用requestId拒绝。"
            "配置格式/绑定失败为invalidRequest/AuthorConfigurationInvalid，field=configuration.<字段>；"
            "export/参数包错误为commandRejected并保留原failureCode与精确配置field。")
    elif name == "saveAuthorPackageFiles":
        operation["description"] += (
            " SDK2只推进候选currentSourceRevision；可选configuration仍按当前在用源码验证。"
            "跨schema迁移须先编译目标修订，再在adopt-source-revision提交完整configuration；SDK1保持原规则。")
