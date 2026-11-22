"""Reviewed Phase 1 lyrics beat-strike, line split/merge, non-destructive ruby, and text-diff wire contracts."""
import copy
from orchestration_properties_schema import int64_pattern


def ref(name):
    return {"$ref": "#/components/schemas/" + name}


def obj(props, required=None):
    return {
        "type": "object",
        "properties": props,
        "required": list(props) if required is None else required,
        "additionalProperties": False,
    }


STABLE = {"type": "string", "minLength": 1, "maxLength": 128, "pattern": r"\S"}
TIME = {
    "type": "string",
    "pattern": r"^(?:\+?" + int64_pattern()[1:-1] + r"|-0*(?:" + int64_pattern()[1:-1] + r"|9223372036854775808))$",
    "x-int64-string": True,
    "description": "十进制Int64微秒字符串。",
}

NEW_SCHEMAS = (
    "LyricBeatTargetDtoInput",
    "TextDiffLineDtoInput",
    "StrikeBeatCommandDtoInput",
    "CloseBeatCommandDtoInput",
    "TogglePhraseEndHintCommandDtoInput",
    "SplitLineCommandDtoInput",
    "MergeLinesCommandDtoInput",
    "SetRubySegmentsCommandDtoInput",
    "ApplyTextDiffCommandDtoInput",
    "ClearLineTimingCommandDtoInput",
    "ClearBranchTimingCommandDtoInput",
)


def extend_lyrics_refactor_commands(schemas, metadata, schema_for, variants, base_command):
    beat_info = metadata["LyricBeatTargetDto"]
    if not beat_info["strict"] or {p["name"] for p in beat_info["properties"]} != {"branchId", "lineId", "tokenId", "rubyId", "segmentId"}:
        raise ValueError("Unreviewed LyricBeatTargetDto shape")
    schemas["LyricBeatTargetDtoInput"] = {
        **obj(
            {
                "branchId": copy.deepcopy(STABLE),
                "lineId": copy.deepcopy(STABLE),
                "tokenId": copy.deepcopy(STABLE),
                "rubyId": {"anyOf": [copy.deepcopy(STABLE), {"type": "null"}]},
                "segmentId": {"anyOf": [copy.deepcopy(STABLE), {"type": "null"}]},
            },
            required=["branchId", "lineId", "tokenId"],
        ),
        "description": "拍点目标：rubyId 与 segmentId 同时省略／null 表示基础词元，同时提供表示注音分段。",
    }

    diff_line_info = metadata["TextDiffLineDto"]
    if not diff_line_info["strict"] or {p["name"] for p in diff_line_info["properties"]} != {"text", "sourceLineId"}:
        raise ValueError("Unreviewed TextDiffLineDto shape")
    schemas["TextDiffLineDtoInput"] = obj(
        {
            "text": {"type": "string"},
            "sourceLineId": {"anyOf": [copy.deepcopy(STABLE), {"type": "null"}]},
        },
        required=["text"],
    )

    token_target = obj(
        {"branchId": copy.deepcopy(STABLE), "lineId": copy.deepcopy(STABLE), "tokenId": copy.deepcopy(STABLE)},
        required=["branchId", "lineId", "tokenId"],
    )
    line_target = obj(
        {"branchId": copy.deepcopy(STABLE), "lineId": copy.deepcopy(STABLE)},
        required=["branchId", "lineId"],
    )

    definitions = (
        (
            "strikeBeat",
            "StrikeBeatCommandDto",
            {"branchId", "closeTarget", "openTarget", "playheadUs", "closeGuardGapUs", "fallbackDurationUs"},
            {
                "branchId": copy.deepcopy(STABLE),
                "closeTarget": {"anyOf": [ref("LyricBeatTargetDtoInput"), {"type": "null"}]},
                "openTarget": ref("LyricBeatTargetDtoInput"),
                "playheadUs": copy.deepcopy(TIME),
                "closeGuardGapUs": {"anyOf": [copy.deepcopy(TIME), {"type": "null"}]},
                "fallbackDurationUs": {"anyOf": [copy.deepcopy(TIME), {"type": "null"}]},
            },
            ["type", "branchId", "openTarget", "playheadUs"],
        ),
        (
            "closeBeat",
            "CloseBeatCommandDto",
            {"beatTarget", "playheadUs"},
            {
                "target": ref("LyricBeatTargetDtoInput"),
                "playheadUs": copy.deepcopy(TIME),
            },
            ["type", "target", "playheadUs"],
        ),
        (
            "togglePhraseEndHint",
            "TogglePhraseEndHintCommandDto",
            set(),
            {"target": copy.deepcopy(token_target)},
            ["type", "target"],
        ),
        (
            "splitLine",
            "SplitLineCommandDto",
            {"tokenIndex"},
            {
                "target": copy.deepcopy(line_target),
                "tokenIndex": {"type": "integer", "minimum": 1, "maximum": 2147483647},
            },
            ["type", "target", "tokenIndex"],
        ),
        (
            "mergeLines",
            "MergeLinesCommandDto",
            set(),
            {"target": copy.deepcopy(line_target)},
            ["type", "target"],
        ),
        (
            "setRubySegments",
            "SetRubySegmentsCommandDto",
            {"segments"},
            {
                "target": copy.deepcopy(token_target),
                "segments": {"type": "array", "items": {"type": "string"}, "minItems": 0, "maxItems": 20000},
            },
            ["type", "target", "segments"],
        ),
        (
            "applyTextDiff",
            "ApplyTextDiffCommandDto",
            {"branchId", "lines"},
            {
                "branchId": copy.deepcopy(STABLE),
                "lines": {"type": "array", "items": ref("TextDiffLineDtoInput"), "minItems": 0, "maxItems": 20000},
            },
            ["type", "branchId", "lines"],
        ),
        (
            "clearLineTiming",
            "ClearLineTimingCommandDto",
            {"keepLineTime"},
            {
                "target": copy.deepcopy(line_target),
                "keepLineTime": {"anyOf": [{"type": "boolean"}, {"type": "null"}]},
            },
            ["type", "target"],
        ),
        (
            "clearBranchTiming",
            "ClearBranchTimingCommandDto",
            {"branchId"},
            {
                "branchId": copy.deepcopy(STABLE),
            },
            ["type", "branchId"],
        ),
    )

    for suffix, clr, own_fields, wire_props, required in definitions:
        info = metadata[clr]
        if info.get("baseType") != "ProjectCommandDto" or {p["name"] for p in info["properties"]} != set(base_command["properties"]) | own_fields:
            raise ValueError("Unreviewed lyric refactor command inheritance: " + clr)
        component = clr + "Input"
        schemas[component] = {
            **obj({"type": {"type": "string", "const": "lyrics." + suffix}, **copy.deepcopy(wire_props)}, required=required),
            "x-clr-type": "MakoKaraGenerator.Backend.Contracts." + clr,
            "x-capability": "project-sessions.commands.lyrics." + suffix,
        }
        variants.append(ref(component))


def extend_lyrics_refactor_responses(schemas, metadata):
    token_props = {p["name"]: p["type"] for p in metadata["ProjectLyricTokenDto"]["properties"]}
    if token_props != dict(id="String", text="String", time="LyricTimeDto", timingStatus="ProjectLyricTimingStatus", phraseEndHint="Boolean"):
        raise ValueError("Unreviewed ProjectLyricTokenDto shape")
    token_schema = schemas["ProjectLyricTokenDto"]
    token_schema["properties"]["phraseEndHint"] = {"type": "boolean"}
    if "phraseEndHint" in token_schema.get("required", []):
        token_schema["required"] = [k for k in token_schema["required"] if k != "phraseEndHint"]

    timing_props = {p["name"]: p["type"] for p in metadata["LyricTimingCommandResultDto"]["properties"]}
    if timing_props != dict(estimatedPlaceholder="Boolean", closeGuardApplied="Boolean"):
        raise ValueError("Unreviewed LyricTimingCommandResultDto shape")
    timing_schema = schemas["LyricTimingCommandResultDto"]
    timing_schema["properties"]["closeGuardApplied"] = {"type": "boolean"}
    if "closeGuardApplied" in timing_schema.get("required", []):
        timing_schema["required"] = [k for k in timing_schema["required"] if k != "closeGuardApplied"]
