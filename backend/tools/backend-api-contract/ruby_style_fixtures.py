"""Contract 1.56 Ruby layout additions with reversible 1.55 example upgrades."""
import copy

from static_style_schema import PROPERTIES

PREFIX = "ruby-style-"
RUBY_PATHS = ("ruby.relativeScale", "ruby.gapPx")
OLD_PATHS = [path for path in PROPERTIES if path not in RUBY_PATHS]
RUBY_DEFAULT = {"relativeScale": None, "gapPx": None}
CANONICAL_KEYS = {"typography", "unsung", "sung", "wipe", "verticalCenterRatio", "horizontalAlignment", "opacity", "scale"}


def canonical(value):
    return len(CANONICAL_KEYS.intersection(value)) >= 3 and bool({"typography", "unsung", "sung", "wipe"}.intersection(value))


def transform(value, upgrade=True):
    if isinstance(value, list):
        result = [transform(item, upgrade) for item in value]
        if all(isinstance(item, dict) and set(item) == {"property", "sourcePath"} for item in result):
            paths = [item["property"] for item in result]
            if upgrade and paths == OLD_PATHS:
                sources = {item["property"]: item for item in result}
                sources.update({path: {"property": path, "sourcePath": "project.baseStyle"} for path in RUBY_PATHS})
                return [sources[path] for path in PROPERTIES]
            if not upgrade and paths == PROPERTIES and all(item["sourcePath"] == "project.baseStyle" for item in result if item["property"] in RUBY_PATHS):
                return [item for item in result if item["property"] not in RUBY_PATHS]
        return result
    if not isinstance(value, dict):
        return value
    result = {key: transform(item, upgrade) for key, item in value.items()}
    if canonical(result):
        if upgrade and "ruby" not in result:
            result["ruby"] = copy.deepcopy(RUBY_DEFAULT)
        elif not upgrade and result.get("ruby") == RUBY_DEFAULT:
            del result["ruby"]
    return result


def migrate_cases(cases):
    return [{**case, "value": transform(case["value"])} for case in cases]


def restore_cases(cases):
    return [{**case, "value": transform(case["value"], upgrade=False)} for case in cases]


def fixtures():
    cases = []
    line = [{"kind": "line", "branchId": "b", "lineId": "l"}]
    token = [{"kind": "token", "branchId": "b", "lineId": "l", "tokenId": "t"}]
    def add(name, schema, value, valid=True):
        cases.append({"name": PREFIX + name, "schema": schema, "value": value, "valid": valid})
    for path, value in (("ruby.relativeScale", .65), ("ruby.gapPx", 3)):
        for target, label in ((line, "line"), (token, "token")):
            for selected in (value, None):
                add(label + "-" + path + ("-null" if selected is None else ""), "ProjectCommandDtoInput",
                    {"type": "styles.patch", "targets": target, "changes": [{"property": path, "value": selected}]})
        add("restore-" + path, "ProjectCommandDtoInput", {"type": "styles.restore", "targets": line, "properties": [path]})
        add("negative-" + path, "StyleChangeDtoInput", {"property": path, "value": -1}, False)
    add("zero-scale", "StyleChangeDtoInput", {"property": "ruby.relativeScale", "value": 0}, False)
    add("zero-gap", "StyleChangeDtoInput", {"property": "ruby.gapPx", "value": 0})
    add("huge-scale", "StyleChangeDtoInput", {"property": "ruby.relativeScale", "value": 3.5e38}, False)
    add("mark-ruby-invalid", "SetMarkAppearanceCommandDtoInput", {"type": "orchestration.setMarkAppearance", "nodeId": "mark", "changes": [{"property": "ruby.gapPx", "value": 2}]}, False)
    return cases
