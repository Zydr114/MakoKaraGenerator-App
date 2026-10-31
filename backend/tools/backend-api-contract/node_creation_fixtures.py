"""Independent G20-B examples; synthetic wire checks, not HTTP acceptance."""
import copy
import json


def cases():
    out = []

    def add(name, schema, value, valid=True):
        out.append(dict(name="node-creation-" + name, schema=schema, value=copy.deepcopy(value), valid=valid))

    create = "CreateOrchestrationNodeCommandDtoInput"
    duplicate = "CopyOrchestrationSubtreeCommandDtoInput"
    commands = [(create, dict(type="orchestration.createNode", kind="group", parentId=None, name=None, slotCount=None)),
                (duplicate, dict(type="orchestration.copySubtree", nodeId="node", name=None))]
    metadata = dict(expectedRevision="0", requestId="fixture", hostInstanceId="host", validUntilUtc="2026-10-18T10:00:00Z")
    for schema, value in commands:
        operation = value["type"].split(".")[-1]
        add(operation, schema, value)
        add(operation + "-union", "ProjectCommandDtoInput", value)
        add(operation + "-envelope", "ProjectCommandRequest", {**metadata, "command": value})
        for suffix, bad in (("null", None), ("wrong-case", value["type"].upper()), ("number", 1)):
            add(operation + "-invalid-type-" + suffix, schema, {**value, "type": bad}, False)
        for field in value:
            changed = copy.deepcopy(value); del changed[field]
            add(operation + "-missing-" + field, schema, changed, False)
        for field, bad in (("target", None), ("startUs", None), ("endUs", None), ("analysisJobId", None), ("id", "client-generated"), ("unknown", True)):
            add(operation + "-extra-" + field, schema, {**value, field: bad}, False)
        for suffix, name, valid in (("empty", "", True), ("limit", "名" * 256, True), ("over", "名" * 257, False),
                                    ("number", 1, False), ("array", [], False)):
            add(operation + "-name-" + suffix, schema, {**value, "name": name}, valid)
        id_field = "parentId" if schema == create else "nodeId"
        for suffix, identity, valid in (("empty", "", False), ("blank", " \t", False), ("limit", "x" * 128, True),
                                        ("over", "x" * 129, False), ("number", 1, False), ("object", {}, False), ("null", None, schema == create)):
            add(operation + "-id-" + suffix, schema, {**value, id_field: identity}, valid)
    base = commands[0][1]
    for kind in ("group", "auto", "slotSequence"):
        for suffix, count in (("null", None), ("zero", 0), ("one", 1), ("limit", 64), ("over", 65), ("negative", -1),
                              ("fraction", 1.5), ("text", "2"), ("bool", True)):
            valid = count is None if kind != "slotSequence" else type(count) is int and 1 <= count <= 64
            add(kind + "-count-" + suffix, create, {**base, "kind": kind, "slotCount": count}, valid)
    for suffix, kind in (("null", None), ("int", 1), ("Group", "Group"), ("Auto", "Auto"), ("slotsequence", "slotsequence"),
                         ("regular", "regular"), ("slot", "slot"), ("countdown", "countdown"), ("unknown", "other")):
        add("invalid-kind-" + suffix, create, {**base, "kind": kind}, False)
    result_schema = "OrchestrationStructureCommandResultDto"
    for operation in ("createNode", "copySubtree"):
        value = dict(operation=operation, nodeId="new", changed=True, createdNodeIds=["new", "child"])
        add(operation + "-result", result_schema, value)
        add(operation + "-result-parent", result_schema, {**value, "parentId": "parent"})
        for field in ("nodeId", "parentId"):
            add(operation + "-result-null-" + field, result_schema, {**value, field: None}, False)
        for field in ("nodeId", "createdNodeIds"):
            changed = copy.deepcopy(value); del changed[field]
            add(operation + "-result-missing-" + field, result_schema, changed, False)
        for suffix, ids in (("null", None), ("empty", []), ("duplicates", ["new", "new"]), ("over", ["n" + str(i) for i in range(129)])):
            add(operation + "-result-ids-" + suffix, result_schema, {**value, "createdNodeIds": ids}, False)
        for field, bad in (("changed", False), ("containerId", "container"), ("orderedIds", ["new"])):
            add(operation + "-result-bad-" + field, result_schema, {**value, field: bad}, False)
        add(operation + "-result-max", result_schema, {**value, "createdNodeIds": ["n" + str(i) for i in range(128)]})
    for operation in ("setNode", "reparent", "reorderChildren", "addSlot", "removeNode", "reorderSlots"):
        value = dict(operation=operation, changed=True)
        add(operation + "-legacy-result", result_schema, value)
        add(operation + "-legacy-no-created-ids", result_schema, {**value, "createdNodeIds": ["new"]}, False)
    return out


if __name__ == "__main__":
    print(json.dumps({"cases": cases()}, ensure_ascii=False, indent=2))
