"""Reviewed W8 author editing, recovery, history, and package download boundary."""
import copy

from p4_author_schema import REVISION, DRAFT_ID
from static_style_schema import obj
from template_library_schema import REVISION as LIBRARY_REVISION

NEW_SCHEMAS = (
    "ProfileAuthorDraftDto", "ProfileAuthorDraftListResponse", "AuthorDraftClaimRequest", "AuthorDraftClaimResponse",
    "AuthorPackageFilesResponse", "AuthorPackageFileDto", "AuthorPackageFilesRequest",
    "AuthorPackageTextFileDtoInput", "AuthorPackageConfigurationInputDtoInput",
    "AuthorSourceRevisionDto", "AuthorSourceRevisionsResponse",
    "AuthorPackageQuery", "AuthorSourceRevisionsQuery", "ProfileAuthorDraftQuery",
)
TEXT = {"type": "string", "minLength": 1, "pattern": r"\S"}
OWNERSHIP = REVISION
CAPABILITIES = {
    "readProfileAuthorDrafts": "author.drafts.profile-list",
    "claimAuthorDraft": "author.drafts.claim",
    "readAuthorPackageFiles": "author.drafts.package-files.read",
    "saveAuthorPackageFiles": "author.drafts.package-files.write",
    "readAuthorSourceRevisions": "author.drafts.source-revisions",
    "downloadAuthorPackage": "author.drafts.package.download",
    "downloadScriptConfigurationPackage": "script-configurations.package.download",
}


def extend(schemas, metadata):
    # CLR metadata still owns fields, nullability, and strict request shape.
    for name in NEW_SCHEMAS[:11]:
        assert name in schemas, f"Missing public W8 CLR schema: {name}"
    for name in NEW_SCHEMAS[:11]:
        for field in ("sourceRevision", "draftRevision", "expectedDraftRevision", "packageRevision", "nextAfterSourceRevision"):
            if field in schemas[name]["properties"]:
                schemas[name]["properties"][field] = copy.deepcopy(REVISION)
        for field in ("draftId", "nextAfterDraftId"):
            if field in schemas[name]["properties"]:
                schemas[name]["properties"][field] = copy.deepcopy(DRAFT_ID)
    for name, field in (("AuthorDraftClaimRequest", "expectedOwnershipRevision"),
                        ("AuthorDraftClaimResponse", "ownershipRevision"),
                        ("ProfileAuthorDraftDto", "ownershipRevision")):
        schemas[name]["properties"][field] = copy.deepcopy(OWNERSHIP)
    schemas["ProfileAuthorDraftDto"]["properties"]["ownershipState"] = {
        "type": "string", "enum": ["ownedByCaller", "active", "dormant", "orphaned"]}
    schemas["ProfileAuthorDraftListResponse"]["properties"]["drafts"]["maxItems"] = 100
    schemas["AuthorSourceRevisionsResponse"]["properties"]["revisions"]["maxItems"] = 100
    request = schemas["AuthorPackageFilesRequest"]
    request["properties"]["manifestJson"] = copy.deepcopy(TEXT)
    request["properties"]["textFiles"].update(maxItems=256)
    schemas["AuthorPackageTextFileDtoInput"]["properties"]["path"] = copy.deepcopy(TEXT)
    # The server derives packageId/sourceRevision. SDK1 preserves the atomic
    # migration; SDK2 preserves the in-use source until compile and adoption.
    configuration = copy.deepcopy(schemas["AuthorDraftConfigurationInputDtoInput"])
    for field in ("packageId", "sourceRevision"):
        configuration["properties"].pop(field)
        configuration["required"].remove(field)
    schemas["AuthorPackageConfigurationInputDtoInput"] = configuration
    schemas["AuthorPackageQuery"] = obj({"expectedDraftRevision": copy.deepcopy(REVISION),
        "sourceRevision": copy.deepcopy(REVISION)})
    schemas["AuthorSourceRevisionsQuery"] = obj({"expectedDraftRevision": copy.deepcopy(REVISION),
        "afterSourceRevision": copy.deepcopy(REVISION),
        "limit": {"type": "integer", "minimum": 1, "maximum": 100, "default": 100}}, ["expectedDraftRevision"])
    schemas["ProfileAuthorDraftQuery"] = obj({"afterDraftId": copy.deepcopy(DRAFT_ID),
        "limit": {"type": "integer", "minimum": 1, "maximum": 100, "default": 100}}, [])
    schemas["AuthorDraftResponse"]["properties"]["canCompile"]["description"] = (
        "编译参考集可用且currentSourceRevision含C#；不代表已通过编译，不按configuration在用修订推断。")


def configure(operation, item):
    operation_id = item["operationId"]
    if operation_id not in CAPABILITIES:
        return
    operation["x-capability"] = CAPABILITIES[operation_id]
    fields = {}
    required = []
    query_schema = None
    if operation_id in ("readAuthorPackageFiles", "downloadAuthorPackage"):
        fields = {"expectedDraftRevision": REVISION, "sourceRevision": REVISION}
        required = list(fields)
        query_schema = "AuthorPackageQuery"
    elif operation_id == "readAuthorSourceRevisions":
        fields = {"expectedDraftRevision": REVISION, "afterSourceRevision": REVISION,
            "limit": {"type": "integer", "minimum": 1, "maximum": 100, "default": 100}}
        required = ["expectedDraftRevision"]
        query_schema = "AuthorSourceRevisionsQuery"
    elif operation_id == "readProfileAuthorDrafts":
        fields = {"afterDraftId": DRAFT_ID, "limit": {"type": "integer", "minimum": 1, "maximum": 100, "default": 100}}
        query_schema = "ProfileAuthorDraftQuery"
    elif operation_id == "downloadScriptConfigurationPackage":
        fields = {"expectedRevision": LIBRARY_REVISION}
        required = list(fields)
    operation["parameters"] += [{"name": name, "in": "query", "required": name in required,
        "schema": copy.deepcopy(shape)} for name, shape in fields.items()]
    operation["x-query-policy"] = {"allowUnknown": False, "allPresentExactlyOnce": True,
        "caseSensitiveNames": True, "required": required, "allowed": list(fields)}
    if query_schema:
        operation["x-query-schema"] = {"$ref": "#/components/schemas/" + query_schema}
    if operation_id == "saveAuthorPackageFiles":
        operation["x-author-package-limits"] = {"maxRequestBytes": 32 * 1024 * 1024,
            "maxTextBytes": 16 * 1024 * 1024, "maxFileTextBytes": 4 * 1024 * 1024, "maxTextFiles": 256}
        operation["description"] += " 完整文本集合与manifest原子保存；图片、字体等二进制保持固定。可选configuration绑定新修订；结构/配置失败不发布。保存不代表编译或渲染通过。"
    elif operation_id in ("downloadAuthorPackage", "downloadScriptConfigurationPackage"):
        operation["x-package-download-limits"] = {"maxConcurrentDownloads": 2, "timeoutSeconds": 30}
        operation["responses"]["200"]["headers"].update({
            "ETag": {"schema": {"type": "string"}, "description": "完整归档的SHA-256，带双引号。"},
            "Cache-Control": {"schema": {"type": "string", "const": "no-store"}},
        })
        operation["description"] += " 仅完整GET，不支持Range/HEAD/条件304；全局最多2个并发下载，30秒期限，关闭或断开释放临时归档。"
    elif operation_id == "claimAuthorDraft":
        operation["description"] += " 仅认领本profile无活动所有者的草稿；内容与所有权双CAS，活动owner冲突409。旧任务/预览不转移。"
    elif operation_id == "readAuthorSourceRevisions":
        operation["description"] += " 按源码修订字典序分页；未知历史保存时间省略；编译摘要按固定源码修订保留。"
