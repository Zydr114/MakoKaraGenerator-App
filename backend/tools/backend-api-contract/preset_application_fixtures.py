#!/usr/bin/env python3
"""Incremental examples for the four project preset-assignment commands."""
import copy
import json
import sys


def fixtures():
    cases = []
    revision = "a" * 64
    targets = {
        "project": {"kind": "project"},
        "branch": {"kind": "branch", "branchId": "primary"},
        "line": {"kind": "line", "branchId": "primary", "lineId": "line-1"},
        "token": {"kind": "token", "branchId": "primary", "lineId": "line-1", "tokenId": "token-1"},
    }

    def add(name, schema, value, valid=True):
        cases.append({"name": "preset-application-" + name, "schema": schema, "valid": valid, "value": copy.deepcopy(value)})

    apply = {"type": "presetApplications.apply", "templateLibraryId": "builtin", "templateId": "builtin:line-classic",
             "templateRevision": revision, "targets": [targets["project"]]}
    update = {"type": "presetApplications.update", "applicationId": "preset:one", "templateLibraryId": "personal",
              "templateId": "personal:one", "templateRevision": revision}
    enabled = {"type": "presetApplications.setEnabled", "applicationId": "preset:one", "enabled": False}
    remove = {"type": "presetApplications.remove", "applicationIds": ["preset:one", "preset:two"]}
    for name, command in (("apply", apply), ("update", update), ("set-enabled", enabled), ("remove", remove)):
        add(name, "ProjectCommandDtoInput", command)
        add(name + "-unknown", "ProjectCommandDtoInput", {**command, "extra": True}, False)
        add(name + "-missing-type", "ProjectCommandDtoInput", {k: v for k, v in command.items() if k != "type"}, False)
    for kind, target in targets.items():
        value = {**apply, "targets": [target]}
        add("target-" + kind, "ProjectCommandDtoInput", value)
    add("apply-branch-inputs", "ProjectCommandDtoInput", {**apply, "branchInputs": {"translation": "branch-1"}})
    add("apply-null-branch-inputs", "ProjectCommandDtoInput", {**apply, "branchInputs": None})
    add("apply-no-targets", "ProjectCommandDtoInput", {**apply, "targets": []}, False)
    add("apply-too-many-targets", "ProjectCommandDtoInput", {**apply, "targets": [targets["project"]] * 1001}, False)
    add("apply-duplicate-targets", "ProjectCommandDtoInput", {**apply, "targets": [targets["project"], targets["project"]]}, False)
    add("apply-unknown-library", "ProjectCommandDtoInput", {**apply, "templateLibraryId": "other"}, False)
    add("apply-target-extra", "ProjectCommandDtoInput", {**apply, "targets": [{**targets["project"], "branchId": "x"}]}, False)
    add("apply-target-missing-parent", "ProjectCommandDtoInput", {**apply, "targets": [{"kind": "line", "lineId": "line-1"}]}, False)
    add("set-enabled-null", "ProjectCommandDtoInput", {**enabled, "enabled": None}, False)
    add("remove-empty", "ProjectCommandDtoInput", {**remove, "applicationIds": []}, False)
    add("remove-non-string", "ProjectCommandDtoInput", {**remove, "applicationIds": [1]}, False)
    add("remove-duplicate", "ProjectCommandDtoInput", {**remove, "applicationIds": ["preset:one", "preset:one"]}, False)
    return cases


if __name__ == "__main__":
    json.dump({"cases": fixtures()}, sys.stdout, ensure_ascii=False, indent=2)
    sys.stdout.write("\n")
