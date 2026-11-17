"""Contract 1.55.0 fixtures for importing every parsed lyrics source in one edit."""
import copy

LEGACY_CONTRACT = "1.54.0"
PREFIX = "ttml-import-"
NEW_SCHEMAS = ("AllSourcesLyricsDestinationDtoInput",)


def fixtures():
    """Tail examples for allSources and the unchanged single-source request."""
    out = []

    def add(name, schema, value, valid=True):
        out.append({
            "name": PREFIX + name,
            "schema": schema,
            "valid": valid,
            "value": copy.deepcopy(value),
        })

    add("all-sources", "ProjectCommandDtoInput", {
        "type": "lyrics.importAsset",
        "assetId": "asset-ttml",
        "destination": {"kind": "allSources"},
    })
    add("all-sources-options-without-branch", "ProjectCommandDtoInput", {
        "type": "lyrics.importAsset",
        "assetId": "asset-ttml",
        "destination": {"kind": "allSources"},
        "options": {"splitRule": "none", "estimateSplitTimes": False},
    })
    add("all-sources-extra-field", "ProjectCommandDtoInput", {
        "type": "lyrics.importAsset",
        "assetId": "asset-ttml",
        "destination": {"kind": "allSources", "branchId": "primary"},
    }, False)
    add("all-sources-on-append", "ProjectCommandDtoInput", {
        "type": "lyrics.appendText",
        "text": "甲",
        "destination": {"kind": "allSources"},
    }, False)
    add("single-source-still-valid", "ProjectCommandDtoInput", {
        "type": "lyrics.importAsset",
        "assetId": "asset-ttml",
        "destination": {"kind": "newBranch", "name": "翻译", "role": "translation"},
        "options": {"sourceBranchId": "source-translation"},
    })
    return out
