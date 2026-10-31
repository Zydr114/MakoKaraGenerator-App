"""Finite positive and negative examples for the W8 author API additions."""
import copy
import json

PREFIX = "w8-authoring-"
REVISION = "sha256:" + "a" * 64
DRAFT = "draft-" + "b" * 32
OWNERSHIP = "sha256:" + "c" * 64
METADATA = {"requestId": "w8-example", "hostInstanceId": "host-example", "validUntilUtc": "2026-10-30T12:00:00Z"}


def fixtures():
    cases = []

    def add(name, schema, value, valid=True):
        cases.append({"name": PREFIX + name, "schema": schema, "valid": valid, "value": copy.deepcopy(value)})

    def request_examples(name, schema, value):
        add(name, schema, value)
        for field in value:
            if field == "configuration":
                continue
            add(name + "-missing-" + field, schema, {k: v for k, v in value.items() if k != field}, False)
            add(name + "-null-" + field, schema, {**value, field: None}, False)
        add(name + "-unknown", schema, {**value, "hostPath": "/not-a-public-input"}, False)

    request_examples("claim", "AuthorDraftClaimRequest", {"expectedDraftRevision": REVISION,
        "expectedOwnershipRevision": OWNERSHIP, "metadata": METADATA})
    request_examples("package-files", "AuthorPackageFilesRequest", {"expectedDraftRevision": REVISION,
        "manifestJson": "{}", "textFiles": [{"path": "src/Effect.cs", "text": "// saved source"}], "metadata": METADATA})
    add("package-files-empty-text-set", "AuthorPackageFilesRequest", {"expectedDraftRevision": REVISION,
        "manifestJson": "{}", "textFiles": [], "metadata": METADATA})
    for value in ("", "invalid", "a" * 64):
        add("claim-revision-" + str(len(value)), "AuthorDraftClaimRequest", {"expectedDraftRevision": value,
            "expectedOwnershipRevision": OWNERSHIP, "metadata": METADATA}, False)
    add("package-files-too-many", "AuthorPackageFilesRequest", {"expectedDraftRevision": REVISION,
        "manifestJson": "{}", "textFiles": [{"path": f"src/{i}.cs", "text": ""} for i in range(257)], "metadata": METADATA}, False)
    add("package-text-empty-path", "AuthorPackageTextFileDtoInput", {"path": " ", "text": ""}, False)
    add("package-text-empty-content", "AuthorPackageTextFileDtoInput", {"path": "src/Empty.cs", "text": ""})
    add("package-text-role-injection", "AuthorPackageTextFileDtoInput", {"path": "src/Effect.cs", "text": "", "role": "asset"}, False)
    configuration = {"name": "Example", "exportId": "effect", "parametersJson": "{}", "relativeTarget": "application",
        "readScope": "boundSources", "branchInput": None, "fixedSeed": None, "overlayLayer": "package"}
    add("package-configuration", "AuthorPackageConfigurationInputDtoInput", configuration)
    for field in ("packageId", "sourceRevision"):
        add("package-configuration-server-field-" + field, "AuthorPackageConfigurationInputDtoInput",
            {**configuration, field: REVISION}, False)
    for schema, value in (("AuthorPackageQuery", {"expectedDraftRevision": REVISION, "sourceRevision": REVISION}),
                          ("AuthorSourceRevisionsQuery", {"expectedDraftRevision": REVISION}),
                          ("ProfileAuthorDraftQuery", {})):
        add(schema + "-valid", schema, value)
        add(schema + "-unknown", schema, {**value, "path": "../"}, False)
    for schema, value in (("AuthorSourceRevisionsQuery", {"expectedDraftRevision": REVISION}), ("ProfileAuthorDraftQuery", {})):
        for limit in (1, 100):
            add(schema + "-limit-" + str(limit), schema, {**value, "limit": limit})
        for limit in (0, 101, "10", None):
            add(schema + "-invalid-limit-" + str(limit), schema, {**value, "limit": limit}, False)
    add("history-unknown-time", "AuthorSourceRevisionDto", {"sourceRevision": REVISION})
    add("history-known-time", "AuthorSourceRevisionDto", {"sourceRevision": REVISION, "savedAtUtc": "2026-10-30T10:00:00Z"})
    add("history-null-time", "AuthorSourceRevisionDto", {"sourceRevision": REVISION, "savedAtUtc": None}, False)
    add("history-invalid-time", "AuthorSourceRevisionDto", {"sourceRevision": REVISION, "savedAtUtc": "yesterday"}, False)
    add("history-page", "AuthorSourceRevisionsResponse", {"draftId": DRAFT, "draftRevision": REVISION,
        "revisions": [{"sourceRevision": REVISION}], "protocolVersion": "1"})
    for state, claim in (("ownedByCaller", True), ("active", False), ("dormant", True), ("orphaned", True)):
        add("profile-" + state, "ProfileAuthorDraftDto", {"draftId": DRAFT, "draftRevision": REVISION,
            "name": "Example", "ownershipRevision": OWNERSHIP, "ownershipState": state, "canClaim": claim})
    add("profile-unknown-state", "ProfileAuthorDraftDto", {"draftId": DRAFT, "draftRevision": REVISION,
        "name": "Example", "ownershipRevision": OWNERSHIP, "ownershipState": "shared", "canClaim": True}, False)
    return {"cases": cases}


def append_to(existing):
    return {**existing, "cases": [case for case in existing["cases"] if not case["name"].startswith(PREFIX)] + fixtures()["cases"]}


if __name__ == "__main__":
    print(json.dumps(fixtures(), ensure_ascii=False, indent=2))
