"""Reviewed G20-B strict creation/copy wire; apply only to current CLR metadata."""
import copy


def extend_node_creation_schemas(schemas, metadata, schema_for, variants, base_command):
    stable = {"type": "string", "minLength": 1, "maxLength": 128, "pattern": r"\S"}
    name = {"anyOf": [{"type": "string", "maxLength": 256}, {"type": "null"}],
            "description": "必须显式提供；null表示未命名，复制时清除副本根名称。服务按256 UTF-16 code units校验；schema的code-point上限仅为必要条件。"}
    commands = (
        ("createNode", "CreateOrchestrationNodeCommandDto", {"kind": "String", "parentId": "String", "name": "String", "slotCount": "Int32"}),
        ("copySubtree", "CopyOrchestrationSubtreeCommandDto", {"nodeId": "String", "name": "String"}),
    )
    for operation, clr, fields in commands:
        info = metadata[clr]
        if info.get("baseType") != "ProjectCommandDto" or {p["name"] for p in info["properties"]} != set(base_command["properties"]) | set(fields):
            raise ValueError("Unreviewed G20-B command shape: " + clr)
        if any(p["type"] != fields[p["name"]] or not p["nullable"] or p["optional"] or p["defaultValue"] is not None
               for p in info["properties"] if p["name"] in fields):
            raise ValueError("Unreviewed G20-B command CLR types/nullability/defaults: " + clr)
        component = schema_for(clr, "request")["$ref"].split("/")[-1]
        props = {"type": {"type": "string", "const": "orchestration." + operation}}
        if operation == "createNode":
            props.update(kind={"type": "string", "enum": ["group", "auto", "slotSequence"]},
                         parentId={"anyOf": [copy.deepcopy(stable), {"type": "null"}], "description": "必填；显式null表示画布根层，否则必须为已有普通组。"},
                         name=copy.deepcopy(name), slotCount={"anyOf": [{"type": "integer", "minimum": 1, "maximum": 64}, {"type": "null"}]})
        else:
            props.update(nodeId=copy.deepcopy(stable), name=copy.deepcopy(name))
        definition = {"type": "object", "properties": props, "required": list(props), "additionalProperties": False,
                      "x-clr-type": "MakoKaraGenerator.Backend.Contracts." + clr,
                      "x-capability": "project-sessions.commands.orchestration." + operation,
                      "x-node-creation-limits": {"maxIntentBytes": 16384, "maxNodes": 2000, "maxDepth": 64,
                          "maxCopiedNodes": 128, "maxSlots": 64, "maxNameUtf16Units": 256,
                          "maxInputItems": 20000, "maxInputCharacters": 1048576,
                          "maxReadBytes": 1048576, "maxReadItems": 20000,
                          "description": "maxSlots只限制createNode初始slotCount，copy完整子树最多128节点。源/候选验证输入20000项包含节点/程序/引用/覆盖及歌词branch/line/token遍历项，累计身份/名称/标记文本最多1048576 UTF-16 units；长歌词也可能超限。完整编排输出另限1MiB/20000项，实际完整回执预算在提交前验证。schema不证明真实树/预算/身份安全。"}}
        if operation == "createNode":
            definition["oneOf"] = [
                {"properties": {"kind": {"const": kind}, "slotCount": {"type": "null"}}} for kind in ("group", "auto")
            ] + [{"properties": {"kind": {"const": "slotSequence"}, "slotCount": {"type": "integer", "minimum": 1, "maximum": 64}}}]
        schemas[component] = definition
        variants.append({"$ref": "#/components/schemas/" + component})
    result = metadata["OrchestrationStructureCommandResultDto"]
    field = next(p for p in result["properties"] if p["name"] == "createdNodeIds")
    if field["type"] != "String[]" or not field["nullable"] or not field["optional"] or not field["omitNull"] or field["defaultValue"] is not None:
        raise ValueError("Unreviewed G20-B createdNodeIds CLR wire")
    structure = schemas["OrchestrationStructureCommandResultDto"]
    structure["properties"]["operation"]["enum"] += ["createNode", "copySubtree"]
    structure["properties"]["createdNodeIds"] = {"type": "array", "minItems": 1, "maxItems": 128, "uniqueItems": True,
        "items": copy.deepcopy(stable), "description": "仅createNode/copySubtree返回；新根第一，其余为完整候选新增顺序，无旧新映射。旧六命令省略。"}
    structure["allOf"] = [{"if": {"properties": {"operation": {"enum": ["createNode", "copySubtree"]}}, "required": ["operation"]},
        "then": {"required": ["nodeId", "createdNodeIds"], "properties": {"changed": {"const": True}},
                 "not": {"anyOf": [{"required": ["containerId"]}, {"required": ["orderedIds"]}]}},
        "else": {"not": {"required": ["createdNodeIds"]}}}]
    schemas["ProjectCommandResponse"]["properties"]["structureResult"]["description"] = "G20八种结构命令的真实结果；createNode/copySubtree新增createdNodeIds，旧六命令省略。可空身份与顺序槽省略，根层parentId因此省略。"
