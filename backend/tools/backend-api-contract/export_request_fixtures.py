#!/usr/bin/env python3
"""Export request examples; validate against the generated contract with --extra-examples."""
import json


def cases():
    request = dict(expectedRevision="0", includeAudio=False, metadata=dict(
        requestId="export-fixture", hostInstanceId="host", validUntilUtc="2026-10-18T10:00:00Z"))
    out = [dict(name="export-request-default-format", schema="ExportJobRequest", value=request, valid=True)]
    for format_name, valid in (("opaqueMp4", True), ("transparentProRes4444", True),
                               ("transparentMov", False), ("TransparentProRes4444", False), ("mov", False)):
        out.append(dict(name="export-request-format-" + format_name, schema="ExportJobRequest",
                        value={**request, "format": format_name}, valid=valid))
    return out


if __name__ == "__main__":
    print(json.dumps({"cases": cases()}, ensure_ascii=False, indent=2))
