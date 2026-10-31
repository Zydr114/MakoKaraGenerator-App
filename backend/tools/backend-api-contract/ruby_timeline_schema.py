"""Reviewed ADR-0233 independent ruby wire contracts, after current CLR reflection."""
import copy
from orchestration_properties_schema import int64_pattern


def ref(name):
    return {"$ref": "#/components/schemas/" + name}


def obj(props, required=None):
    return {"type": "object", "properties": props, "required": list(props) if required is None else required, "additionalProperties": False}


STABLE = {"type": "string", "minLength": 1, "maxLength": 128, "pattern": r"\S"}
TIME = {"type": "string", "pattern": r"^(?:\+?" + int64_pattern()[1:-1] + r"|-0*(?:" + int64_pattern()[1:-1] + r"|9223372036854775808))$", "x-int64-string": True,
        "description": "十进制Int64微秒字符串，含超过2^53的精确值；非负有序区间与重复目标由服务校验（领域失败422）。"}


def extend_ruby_commands(schemas, metadata, schema_for, variants, base_command):
    for clr, fields in (("RubyGroupTargetDto", ["branchId", "lineId", "tokenId", "rubyId"]),
                        ("RubySegmentTargetDto", ["branchId", "lineId", "tokenId", "rubyId", "segmentId"])):
        info = metadata[clr]
        if not info["strict"] or [(p["name"], p["type"], p["nullable"], p["optional"]) for p in info["properties"]] != [(name, "String", False, False) for name in fields]:
            raise ValueError("Unreviewed ruby target shape: " + clr)
        schemas[clr + "Input"] = obj({name: copy.deepcopy(STABLE) for name in fields})
    schemas["RubySegmentTimeChangeDtoInput"] = obj({"target": ref("RubySegmentTargetDtoInput"), "startUs": copy.deepcopy(TIME), "endUs": copy.deepcopy(TIME)})
    change = metadata["RubySegmentTimeChangeDto"]
    if {p["name"]: p["type"] for p in change["properties"]} != dict(target="RubySegmentTargetDto", startUs="String", endUs="String") or not change["strict"]:
        raise ValueError("Unreviewed ruby batch entry")
    group, segment = ref("RubyGroupTargetDtoInput"), ref("RubySegmentTargetDtoInput")
    definitions = (
        ("setRubySegmentTime", "SetRubySegmentTimeCommandDto", dict(rubyTarget="RubySegmentTargetDto", segmentStartUs="String", segmentEndUs="String"), dict(target=segment, startUs=TIME, endUs=TIME)),
        ("clearRubySegmentTime", "ClearRubySegmentTimeCommandDto", dict(rubyTarget="RubySegmentTargetDto"), dict(target=segment)),
        ("setRubySegmentText", "SetRubySegmentTextCommandDto", dict(rubyTarget="RubySegmentTargetDto", text="String"), dict(target=segment, text={"type": "string"})),
        ("addRubySegment", "AddRubySegmentCommandDto", dict(rubyTarget="RubyGroupTargetDto", text="String", afterSegmentId="String"), dict(target=group, text={"type": "string"}, afterSegmentId={"anyOf": [STABLE, {"type": "null"}]})),
        ("removeRubySegment", "RemoveRubySegmentCommandDto", dict(rubyTarget="RubySegmentTargetDto"), dict(target=segment)),
        ("reorderRubySegments", "ReorderRubySegmentsCommandDto", dict(rubyTarget="RubyGroupTargetDto", orderedSegmentIds="String[]"), dict(target=group, orderedSegmentIds={"type": "array", "items": STABLE, "minItems": 1, "maxItems": 20000})),
        ("setRubySegmentTimes", "SetRubySegmentTimesCommandDto", dict(changes="RubySegmentTimeChangeDto[]"), dict(changes={"type": "array", "items": ref("RubySegmentTimeChangeDtoInput"), "minItems": 1, "maxItems": 1000})),
    )
    for suffix, clr, fields, wire in definitions:
        info = metadata[clr]
        if info.get("baseType") != "ProjectCommandDto" or {p["name"] for p in info["properties"]} != set(base_command["properties"]) | set(fields):
            raise ValueError("Unreviewed ruby command inheritance: " + clr)
        if any(p["type"] != fields[p["name"]] or not p["nullable"] or p["optional"] for p in info["properties"] if p["name"] in fields):
            raise ValueError("Unreviewed ruby command fields: " + clr)
        component = clr + "Input"
        schemas[component] = {**obj({"type": {"type": "string", "const": "lyrics." + suffix}, **copy.deepcopy(wire)}),
            "x-clr-type": "MakoKaraGenerator.Backend.Contracts." + clr,
            "x-capability": "project-sessions.commands.lyrics." + suffix,
            "x-ruby-edit-limits": dict(maxIntentBytes=65536, maxChanges=1000, maxPreparedBytes=16777216,
                maxReadBytes=1048576, maxReadItems=20000, maxChangeSetBytes=16384,
                description="完整父链隶属、非空片段正文、完整有序身份集合与原子历史由业务校验；允许时间重叠和超行，不延长行窗口。")}
        variants.append(ref(component))


def extend_ruby_responses(schemas, metadata):
    expected = dict(id="String", text="String", time="LyricTimeDto", timingStatus="ProjectLyricTimingStatus")
    if {p["name"]: p["type"] for p in metadata["ProjectRubyTextSegmentDto"]["properties"]} != expected:
        raise ValueError("Unreviewed ruby read segment")
    ruby = schemas["ProjectRubyAnnotationDto"]
    ruby["properties"]["segments"]["maxItems"] = 19999
    ruby["description"] = "text为segments正文按保存顺序拼接的兼容投影；segments保留各自身份、绝对微秒时间与原始状态。"
    result = schemas["RubySegmentCommandResultDto"]
    if {p["name"]: p["type"] for p in metadata["RubySegmentCommandResultDto"]["properties"]} != dict(changedSegmentCount="Int32", target="RubyGroupTargetDto", selectedSegmentId="String", createdSegmentId="String", selectionChanged="Boolean"):
        raise ValueError("Unreviewed ruby feedback")
    result["properties"]["changedSegmentCount"]["minimum"] = 0
    result["description"] = "真实改变片段数；target为单组目标时提供。selectionChanged=true时以selectedSegmentId更新选择；省略该ID表示清空选择。新增返回createdSegmentId；时间批次可省target/选择。撤销重做返回实际ruby反馈。"
