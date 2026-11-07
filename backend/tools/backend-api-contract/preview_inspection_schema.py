"""Reviewed per-frame preview inspection projection (contract 1.48.0)."""
import copy

from static_style_schema import audit

NEW_SCHEMAS = ("PreviewFrameInspectionResponse", "PreviewInspectionDto", "PreviewInspectionCanvasDto", "PreviewInspectionTokenDto",
    "PreviewInspectionClusterDto", "PreviewInspectionRubyDto", "PreviewInspectionLineDto", "PreviewInspectionCursorDto",
    "PreviewInspectionParticlesDto", "PreviewInspectionEffectDto", "PreviewInspectionCostDto", "PreviewInspectionScriptDto",
    "PreviewInspectionAnchorsDto", "PreviewInspectionTruncationDto", "PreviewInspectionHostCostDto")
LIMITS = {"maxProjectionBytes": 262144, "encoding": "web-json-utf8", "tokens": 1000, "clustersPerToken": 16, "rubyPerToken": 8,
    "lines": 256, "cursors": 64, "birthPointsPerInstance": 256, "effects": 64, "lineIdsPerEffect": 64, "polygonPoints": 16,
    "scripts": 16, "retainedWithFrame": True}
SCRIPT_STAGES = ["Initialize", "Advance", "Evaluate", "OnEvent", "AdvanceVisuals", "EvaluateVisuals", "EvaluateEmitters", "other"]
RESPONSE_FIELDS = dict(frameId="String", previewId="String", previewGeneration="String", sourceRevision="String", timeUs="String",
    width="Int32", height="Int32", renderBackend="String", inspection="PreviewInspectionDto", host="PreviewInspectionHostCostDto",
    protocolVersion="String")
POLYGON = {"type": "array", "items": {"type": "number"}, "maxItems": 32,
    "description": "工程画布坐标的闭合多边形 x0,y0,x1,y1,…；Warp 后可多于 4 点，超过 16 点时退为外接四边形。不可得时省略。"}
POINT = {"type": "array", "items": {"type": "number"}, "minItems": 2, "maxItems": 2}


def extend(schemas, metadata):
    audit(metadata, "PreviewFrameInspectionResponse", RESPONSE_FIELDS, optional=("protocolVersion",))
    for name in NEW_SCHEMAS:
        assert name in schemas, name
    response = schemas["PreviewFrameInspectionResponse"]
    response["properties"]["protocolVersion"] = {"type": "string", "const": "1"}
    response["properties"]["timeUs"] = {"type": "string", "pattern": r"^[0-9]+$", "x-int64-string": True}
    response["properties"]["renderBackend"]["enum"] = ["cpu", "gpu"]
    response["description"] = (
        "与帧同寿命的只读检查投影：frameId/previewGeneration/sourceRevision/timeUs与帧元数据一致，几何与像素来自同一次准备。"
        "坐标为工程画布坐标（inspection.canvas），客户端按显示尺寸缩放。只含身份、数值与代码，不含作者对象、源码或本机路径。"
        "inspection的完整Web UTF-8 JSON最多262144字节，超出时按比例收紧条目上限，省略数记在truncated与各条目的omitted*字段。")
    dto = schemas["PreviewInspectionDto"]
    dto["description"] = "prepared=false表示本帧没有效果准备结果（无效果或仅静态），此时tokens/lines/cursors/particles为空。"
    for name, field in (("PreviewInspectionTokenDto", "ink"), ("PreviewInspectionClusterDto", "ink"),
                        ("PreviewInspectionRubyDto", "ink"), ("PreviewInspectionLineDto", "bounds")):
        schemas[name]["properties"][field] = copy.deepcopy(POLYGON)
    cursor = schemas["PreviewInspectionCursorDto"]["properties"]
    for field in ("point", "start", "end"):
        cursor[field] = copy.deepcopy(POINT)
    cursor["progress"].update(minimum=0, maximum=1)
    cursor["progressSource"]["enum"] = ["unavailable", "fromLine", "fromToken", "fromRubySegment"]
    schemas["PreviewInspectionParticlesDto"]["properties"]["birthPoints"].update(maxItems=512,
        description="出生锚点 x0,y0,x1,y1,…（工程画布坐标），每个实例最多 256 点。")
    effect = schemas["PreviewInspectionEffectDto"]["properties"]
    effect["sdkMajor"]["enum"] = [1, 2]
    effect["bindingKind"]["enum"] = ["project", "branch", "lines", "tokens", "clips", "node", "unknown"]
    effect["scriptId"] = {"type": "string", "pattern": r"^[0-9A-F]{16}$", "description": "对应cost.scripts[].scriptId；未计时的实例省略。"}
    cost = schemas["PreviewInspectionCostDto"]
    cost["properties"]["scripts"]["maxItems"] = 16
    cost["description"] = ("Worker内本帧各阶段Host墙钟包含时间（毫秒），彼此可能重叠，不能相加当作总耗时，也不是GPU硬件时间。"
        "allocatedBytes是全进程增量。脚本只计Declare/Advance/OnEvent等阶段，热路径Evaluate不单独计时。sessionOpenMs为固定会话打开耗时。")
    script = schemas["PreviewInspectionScriptDto"]["properties"]
    script["scriptId"] = {"type": "string", "pattern": r"^[0-9A-F]{16}$"}
    script["stage"]["enum"] = SCRIPT_STAGES
    schemas["PreviewInspectionHostCostDto"]["description"] = "Host侧：从提交渲染到收到帧的往返耗时（不含会话打开）、PNG编码耗时与PNG字节数。"


def configure(op, item):
    if item["operationId"] != "previewFrameInspection":
        return
    op["x-capability"] = "preview-frames.inspection"
    op["x-preview-inspection-limits"] = copy.deepcopy(LIMITS)
    op["description"] += (" 只在帧请求带inspect=true且投影生成成功时可读，帧元数据给出inspectionUrl；"
        "与帧PNG同一保留条目和到期时间。未开启或未生成返回404 previewInspectionUnavailable，帧不存在404 previewFrameNotFound，"
        "帧到期410 previewFrameExpired，非owner 401 resourceUnauthorized。投影不改变PNG、导出或缓存。")
