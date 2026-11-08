"""Reviewed author draft whole-file image/font replacement (contract 1.49.0)."""
import copy

from p3_workshop_schema import nullable
from p4_author_schema import REVISION, DRAFT_ID
from static_style_schema import audit, ref

NEW_SCHEMAS = ("AuthorPackageFileReplacementDtoInput", "AuthorPackageFileReplacementInspectionRequest",
    "AuthorPackageFileReplacementInspectionResponse", "AuthorPackageResourceInfoDto", "AuthorPackageReplacementWarningDto")
CAPABILITY = "author.drafts.package-files.replace"
WARNINGS = ["imageSizeChanged", "fontFamilyChanged"]
FONT_FORMATS = ["truetype", "opentype", "collection"]
LIMITS = {"roles": ["image", "font"], "sameFormat": True, "maxRequestBytes": 64 * 1024,
    "maxFileBytes": 64 * 1024 * 1024, "warningsBlockSave": False}
TEXT = {"type": "string", "minLength": 1, "pattern": r"\S"}
SAVE_FIELDS = dict(expectedDraftRevision="String", manifestJson="String", textFiles="AuthorPackageTextFileDto[]",
    configuration="AuthorPackageConfigurationInputDto", metadata="RequestMetadataDto",
    replacedFiles="AuthorPackageFileReplacementDto[]", baseSourceRevision="String")
INSPECTION_FIELDS = dict(draftId="String", draftRevision="String", sourceRevision="String", path="String", role="String",
    current="AuthorPackageResourceInfoDto", replacement="AuthorPackageResourceInfoDto",
    warnings="AuthorPackageReplacementWarningDto[]", protocolVersion="String")
RESOURCE_FIELDS = dict(format="String", sizeBytes="Int64", sha256="String", width="Int32", height="Int32", familyNames="String[]")


def extend(schemas, metadata):
    audit(metadata, "AuthorPackageFilesRequest", SAVE_FIELDS, nullable=("configuration", "metadata", "replacedFiles", "baseSourceRevision"),
        optional=("configuration", "metadata", "replacedFiles", "baseSourceRevision"))
    audit(metadata, "AuthorPackageFileReplacementDto", dict(path="String", assetId="String"))
    audit(metadata, "AuthorPackageFileReplacementInspectionRequest", dict(expectedDraftRevision="String", sourceRevision="String",
        path="String", assetId="String"))
    audit(metadata, "AuthorPackageFileReplacementInspectionResponse", INSPECTION_FIELDS, optional=("protocolVersion",))
    audit(metadata, "AuthorPackageResourceInfoDto", RESOURCE_FIELDS, nullable=("width", "height", "familyNames"),
        optional=("width", "height", "familyNames"))
    audit(metadata, "AuthorPackageReplacementWarningDto", dict(code="String", message="String"))
    for name in NEW_SCHEMAS:
        assert name in schemas, name

    save = schemas["AuthorPackageFilesRequest"]
    save["properties"]["replacedFiles"] = nullable({"type": "array", "items": ref("AuthorPackageFileReplacementDtoInput"),
        "description": "整份替换基准修订里的图片或字体；每个path最多一次，新字节取自本客户端上传且未过期的资源。省略/null/空数组不替换。"})
    save["properties"]["baseSourceRevision"] = nullable(copy.deepcopy(REVISION))
    save["properties"]["baseSourceRevision"]["description"] = (
        "不可编辑文件取自的保留修订（编辑器所看的修订）；省略/null为当前最新修订。须属于此草稿保留历史，否则404。")
    for field in ("replacedFiles", "baseSourceRevision"):
        assert field not in save["required"], field
    save["description"] = (
        "manifestJson里非文本文件的path/role/sha256须与基准修订一致；replacedFiles列出的图片或字体换成新字节，"
        "清单sha256由服务重算。新文件须与原文件同一格式并通过与渲染相同的解码或字体加载；尺寸、家族名变化不阻止保存。")
    item = schemas["AuthorPackageFileReplacementDtoInput"]
    item["properties"]["path"] = copy.deepcopy(TEXT)
    item["properties"]["assetId"] = copy.deepcopy(TEXT)

    request = schemas["AuthorPackageFileReplacementInspectionRequest"]
    for field in ("expectedDraftRevision", "sourceRevision"):
        request["properties"][field] = copy.deepcopy(REVISION)
    for field in ("path", "assetId"):
        request["properties"][field] = copy.deepcopy(TEXT)
    request["description"] = "只读检查：sourceRevision里的path换成assetId会怎样；与保存同一套校验，不改变草稿、不创建资源。"

    response = schemas["AuthorPackageFileReplacementInspectionResponse"]
    for field in ("draftRevision", "sourceRevision"):
        response["properties"][field] = copy.deepcopy(REVISION)
    response["properties"]["draftId"] = copy.deepcopy(DRAFT_ID)
    response["properties"]["role"] = {"type": "string", "enum": ["image", "font"]}
    response["properties"]["protocolVersion"] = {"type": "string", "const": "1"}
    response["properties"]["warnings"]["maxItems"] = len(WARNINGS)
    response["description"] = "原文件与新文件的格式、大小、摘要，以及图片宽高或字体家族名；warnings只是提醒，不阻止保存。"
    info = schemas["AuthorPackageResourceInfoDto"]
    info["properties"]["format"] = {"type": "string", "pattern": r"^[a-z0-9]+$",
        "description": "图片为Skia识别的编码名小写（png、jpeg、webp等）；字体为" + "、".join(FONT_FORMATS) + "。"}
    info["properties"]["sizeBytes"].update(minimum=1)
    info["properties"]["sha256"] = {"type": "string", "pattern": r"^[0-9a-f]{64}$"}
    for field in ("width", "height"):
        info["properties"][field].update(minimum=1)
    info["properties"]["familyNames"].update(minItems=1)
    info["description"] = "图片带width/height，字体带familyNames（去重、按出现顺序）；另一类字段省略。"
    warning = schemas["AuthorPackageReplacementWarningDto"]
    warning["properties"]["code"] = {"type": "string", "enum": WARNINGS}


def configure(operation, item):
    name = item["operationId"]
    if name == "inspectAuthorPackageFileReplacement":
        operation["x-capability"] = CAPABILITY
        operation["x-author-replacement-limits"] = copy.deepcopy(LIMITS)
        operation["x-query-policy"] = {"allowUnknown": False, "allPresentExactlyOnce": True,
            "caseSensitiveNames": True, "required": [], "allowed": []}
        operation["description"] += (
            " 只读POST，无回执；锁内核对owner、草稿CAS与保留修订，解码在锁外。path须是该修订里role为image或font的文件；"
            "新文件须同一格式（图片按编码，字体按TrueType/OpenType/集合文件头）并通过完整解码或字体加载。"
            "目标不对invalidRequest/AuthorReplacementTargetInvalid field=path；格式不同AuthorReplacementFormatMismatch、"
            "字体无法加载AuthorReplacementFontInvalid、图片PackageImageDecodeFailed/PackageImageBudgetExceeded、"
            "单文件超预算PackageFileBudgetExceeded，field=assetId；资源不存在/过期/非本客户端沿资源错误。请求JSON超过64KiB为413。")
    elif name == "saveAuthorPackageFiles":
        operation["description"] += (
            " 能力" + CAPABILITY + "存在时可选replacedFiles整份替换基准修订（baseSourceRevision，省略为最新）里的图片或字体，校验同替换检查；"
            "失败field为replacedFiles[i].path或replacedFiles[i].assetId，不发布修订。")
